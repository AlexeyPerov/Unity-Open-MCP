import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import type { BridgeEventStream } from "./event-stream.js";
import { AssetModelCache, isCompressible, routeCompressible } from "./compressible-router.js";
import { listAssetsOffline, findReferencesOffline, dependenciesOffline } from "./offline.js";
import { editorLogPath, readLogTail, DEFAULT_LOG_TAIL_BYTES } from "./unity-log.js";
import { summarizeProjectHealth } from "./project-health.js";
import { buildCapabilities } from "./capabilities/build-capabilities.js";
import { RULE_CATALOG, FIX_CATALOG } from "./capabilities/rule-catalog.js";
import {
  GROUP_IDS,
  TOOL_GROUPS,
  AUTO_ACTIVATE_GROUPS,
  groupToTools,
} from "./capabilities/tool-groups.js";
import { listRules } from "./capabilities/list-rules.js";
import { generateSkill } from "./skill/generate-skill.js";
import { knownClientKeys } from "./skill/client-paths.js";
import { ALL_TOOLS } from "./tools/index.js";
import { lockPath, readInstanceLock, classifyInstance, isPidAlive, type InstanceLock } from "./instance-discovery.js";
import { BATCH_TOOL_NAMES } from "./batch-spawn.js";
import type { ToolSessionState } from "./tool-session-state.js";
import {
  readProfileAndDetail,
  applyPaging,
  attachPagination,
  foldVerifyResult,
  parseResultBody,
  withResultBody,
  type OutputProfile,
} from "./output-profile.js";

export interface RouteMeta {
  route: "live" | "batch";
  fallbackReason?: string;
}

function injectRouteMeta(
  result: CallToolResult,
  meta: RouteMeta,
): CallToolResult {
  if (result.content.length === 0) return result;
  // M20 Plan 1 / T20.1.1 — capture_inline returns an MCP image content block
  // followed by a text metadata block. Inject _route into whichever text block
  // carries JSON; leave image/other blocks untouched. Fall back to the first
  // block when it is text (the common single-block path).
  const textIndex = result.content.findIndex(
    (c) => c.type === "text",
  );
  if (textIndex < 0) return result;
  const block = result.content[textIndex];
  if (block.type !== "text") return result;
  try {
    const body = JSON.parse(block.text) as Record<string, unknown>;
    body._route = meta;
    const newContent = result.content.slice();
    newContent[textIndex] = { type: "text", text: JSON.stringify(body) };
    return { ...result, content: newContent };
  } catch {
    return result;
  }
}

function activeGroupsEqual(
  a: readonly string[],
  b: readonly string[],
): boolean {
  if (a.length !== b.length) return false;
  const sa = [...a].sort();
  const sb = [...b].sort();
  return sa.every((v, i) => v === sb[i]);
}

// Extract the typed /ping body from a CallToolResult's first text content.
// Returns null on a non-text / non-JSON / non-object result so the
// bridge_status synthesis can fall through to the "stopped" branch without
// throwing.
function parsePingBody(result: {
  isError?: boolean;
  content: Array<{ type: string; text?: string }>;
}): {
  connected?: boolean;
  compiling?: boolean;
  isPlaying?: boolean;
  unityVersion?: string | null;
  bridgeVersion?: string;
  mode?: string;
} | null {
  const first = result.content[0];
  if (!first || first.type !== "text" || typeof first.text !== "string") {
    return null;
  }
  try {
    const parsed = JSON.parse(first.text);
    if (parsed && typeof parsed === "object") {
      return parsed as {
        connected?: boolean;
        compiling?: boolean;
        isPlaying?: boolean;
        unityVersion?: string | null;
        bridgeVersion?: string;
        mode?: string;
      };
    }
  } catch {
    // fall through
  }
  return null;
}

// Compact lock summary for the bridge_status response. Mirrors the field set
// the CLI `status` command surfaces so operators see the same pid/port/state
// metadata regardless of how they asked.
function summarizeBridgeStatusLock(lock: InstanceLock) {
  return {
    pid: lock.pid,
    port: lock.port,
    state: lock.state,
    isCompiling: lock.isCompiling,
    isPlaying: lock.isPlaying,
    heartbeatAt: lock.heartbeatAt,
    bridgeVersion: lock.bridgeVersion,
    unityVersion: lock.unityVersion,
  };
}

// ===========================================================================
// M22 — heavy-tool result post-processors (find_references + verify tools).
//
// These run after the live/batch/offline hop returns. The bridge emits the full
// result regardless of profile; we fold + page server-side so the wire shape
// matches the profile/paging contract without a bridge change. Each returns a
// new CallToolResult (never mutates the input); injectRouteMeta runs after to
// append `_route`.
// ===========================================================================

/** Page the offline find_references `referencedBy` list and attach pagination. */
function pageFindReferencesResult<T extends { referencedBy?: unknown }>(
  result: T,
  pageSize: number,
  cursor: string | undefined,
): T & { pagination: import("./output-profile.js").PaginationBlock } {
  const referencedBy = Array.isArray(result.referencedBy) ? result.referencedBy : [];
  const { page, block } = applyPaging(referencedBy, "find_references", { page_size: pageSize, cursor });
  return attachPagination({ ...result, referencedBy: page }, block);
}

/**
 * Fold + page the LIVE find_references result. The bridge returns a flat
 * `referencedBy` path list (no byKind/byFolder groupings, no locations); we
 * derive the compact grouping server-side and page when requested.
 */
function foldFindReferencesLive(
  result: CallToolResult,
  profile: OutputProfile | undefined,
  wantPaging: boolean,
  args: Record<string, unknown>,
): CallToolResult {
  const body = parseResultBody(result);
  if (body === null) return result;
  if (body.error) return result;

  // Bridge shape: { referencedBy: string[], totalCount, ... } (flat paths).
  const rawList = Array.isArray(body.referencedBy) ? body.referencedBy : [];
  const pageSize = typeof args.page_size === "number" && args.page_size > 0 ? (args.page_size as number) : 0;
  const cursor = typeof args.cursor === "string" ? (args.cursor as string) : undefined;

  if (profile === "compact") {
    // compact: counts + byKind grouping only — drop the per-asset list.
    const { referencedBy: _dropped, ...rest } = body;
    void _dropped;
    const byKind = groupLiveRefsByKind(rawList as string[]);
    return withResultBody(result, {
      ...rest,
      totalCount: rawList.length,
      byKind,
      referencedBy: [],
    });
  }

  // balanced / full: keep the path list (paged when requested).
  if (pageSize > 0) {
    const { page, block } = applyPaging(rawList as string[], "find_references", { page_size: pageSize, cursor });
    const withPage = attachPagination({ ...body, referencedBy: page, totalCount: rawList.length }, block);
    return withResultBody(result, withPage);
  }

  return withResultBody(result, { ...body, totalCount: rawList.length });
}

/** Derive a byKind grouping from flat referenced paths for the live compact view. */
function groupLiveRefsByKind(paths: string[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const path of paths) {
    const dot = path.lastIndexOf(".");
    const kind = dot > 0 ? path.slice(dot + 1).toLowerCase() : "other";
    counts[kind] = (counts[kind] ?? 0) + 1;
  }
  return counts;
}

// Operator-facing "what to do next" hint for each coarse status. Kept short
// and action-oriented so the Validation Suite can render it inline.
function bridgeStatusNextStep(
  status: "running" | "compiling" | "stopped" | "dead_bridge" | "unreachable",
): string {
  switch (status) {
    case "running":
      return "Bridge is ready. Proceed with live-only MCP tools.";
    case "compiling":
      return "Unity is compiling. Wait for the bridge to return to idle, or poll unity_open_mcp_bridge_status again.";
    case "stopped":
      return "Bridge listener is not reachable. Open the bridge window (Unity menu: Tools/Unity Open MCP Bridge) and ensure it is started, or launch Unity if it is not running, then call unity_open_mcp_bridge_status again to confirm.";
    case "unreachable":
      // M20 Plan 4-5 / T-fix-3 — distinct from "stopped": the Unity process is
      // alive (lock PID live) but the listener did not respond. Usually a
      // transient domain-reload window (the bridge tears down its HTTP socket
      // for the reload duration) — retry shortly rather than treating it as a
      // clean stop. This makes the reload-window flakiness visible instead of
      // being masked by a clean-looking "stopped".
      return "Bridge listener is not responding but Unity is running — likely a transient domain-reload window (the bridge tears down its HTTP socket during compiles). Wait a moment and call unity_open_mcp_bridge_status again; if it persists, call unity_open_mcp_read_compile_errors to check for a failed recompile.";
    case "dead_bridge":
      // M23 Plan 2 — safe-mode surfacing (feedback 2026-06-29). A dead_bridge
      // signature (live PID + stale heartbeat) almost always means Unity is
      // sitting in Safe Mode / showing compile errors; the generic "open the
      // bridge window" hint was misleading. Name safe mode explicitly and
      // point at the one channel that works in that state.
      return "Unity is likely in Safe Mode or showing compile errors — the bridge " +
        "assembly failed to recompile, so the HTTP listener will not return on its " +
        "own. Call unity_open_mcp_read_compile_errors to retrieve the compiler " +
        "errors from Editor.log, fix the cited file/line, then trigger a recompile " +
        "(e.g. a no-op edit + focus Unity, or unity_open_mcp_compile_check once the " +
        "source compiles). If Unity is NOT in safe mode, the bridge toolbar toggle " +
        "may be off — open the bridge window (Unity menu: Tools/Unity Open MCP " +
        "Bridge) and confirm it is started.";
  }
}

// M23 Plan 2 — structured recovery hint surfaced alongside `status`. Lets
// agents (and the Validation Suite) branch on a machine-readable signal
// instead of scraping `nextStep` prose. `null` when the status is healthy or
// the recovery path is just "wait/retry" (no specific tool to call). Today
// only `dead_bridge` carries a hint; the shape is extensible so future
// failure modes (e.g. version_mismatch) can add their own.
interface BridgeRecoveryHint {
  /** The tool an agent should call next to diagnose/recover. */
  tool: string;
  /** Why that tool — one short sentence. */
  reason: string;
}

function bridgeStatusRecoveryHint(
  status: "running" | "compiling" | "stopped" | "dead_bridge" | "unreachable",
): BridgeRecoveryHint | null {
  if (status === "dead_bridge") {
    return {
      tool: "unity_open_mcp_read_compile_errors",
      reason:
        "Unity is likely in Safe Mode / compile failure — read_compile_errors " +
        "reads Editor.log offline (the only channel that works with the bridge " +
        "assembly dead).",
    };
  }
  return null;
}

export class ToolRouter implements Router {
  private readonly modelCache = new AssetModelCache();
  constructor(
    private live: LiveClient,
    private batch: BatchSpawn,
    private projectPath: string,
    private eventStream: BridgeEventStream,
    private sessionState: ToolSessionState,
    private onToolListChanged?: () => void | Promise<void>,
  ) {}

  async route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    return this.routeCore(this.live, toolName, args);
  }

  /**
   * M23 Plan 3 — route with a per-call live-client override. Used when a
   * per-request `_meta.port` override targets a different bridge instance than
   * the default. The override client bypasses shared session state (it is a
   * fresh LiveClient aimed at the override port); all other routing logic is
   * identical to {@link route}. When `overrideLive` is null the default client
   * is used (identical to {@link route}).
   */
  async routeOverride(
    toolName: string,
    args: Record<string, unknown>,
    overrideLive: LiveClient,
  ): Promise<CallToolResult> {
    return this.routeCore(overrideLive, toolName, args);
  }

  private async routeCore(
    live: LiveClient,
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    // list_assets — always offline (no live equivalent needed).
    if (toolName === "unity_open_mcp_list_assets") {
      return this.routeListAssets(args);
    }

    // read_compile_errors — always offline. Reads Unity's Editor.log directly;
    // the one channel that works when the bridge assembly itself has failed to
    // compile (every in-bridge channel is dead with it, and compile_check can't
    // run). Never touches the bridge or spawns Unity.
    if (toolName === "unity_open_mcp_read_compile_errors") {
      return this.routeReadCompileErrors(args);
    }

    // M13 T4.4 — bridge event stream pull. The MCP server is the only long-lived
    // hop between the bridge and the agent; a per-process SSE reader amortizes
    // the connection and shares the queue across pulls. Lives only when the
    // bridge is reachable; an unreachable bridge returns a clear error instead
    // of hanging on connect.
    if (toolName === "unity_senses_pull_events") {
      return this.routePullEvents(args, live);
    }

    // capabilities — local capability-discovery surface (no live/batch hop).
    if (toolName === "unity_open_mcp_capabilities") {
      return this.routeCapabilities(args, live);
    }

    // list_rules — local rule catalog (no live/batch hop). Lets an agent
    // discover which rules apply to which asset kinds before calling
    // scan_paths / validate_edit.
    if (toolName === "unity_open_mcp_list_rules") {
      return this.routeListRules(args);
    }

    // generate_skill — local skill generation (no live/batch hop).
    if (toolName === "unity_open_mcp_generate_skill") {
      return this.routeGenerateSkill(args);
    }

    // M18 Plan 2 / T18.2.2 — manage_tools is server-only. It mutates the
    // per-session tool-group visibility state that lives in the MCP server
    // (ToolSessionState). The bridge does not track session state — it answers
    // manage_tools meta-calls by reporting the compiled tool set; the MCP
    // server applies the activate / deactivate filter to ListTools. Always
    // visible regardless of the current active set (meta-tool).
    if (toolName === "unity_open_mcp_manage_tools") {
      return this.routeManageTools(args, live);
    }

    // testsuite-tauri phase-3 — bridge_status. Operator-only health snapshot.
    // Server-resolved (classifyInstance + one /ping via the LiveClient); never
    // hits the bridge tool endpoint and never spawns Unity. Returns a coarse
    // `status` token (running/compiling/stopped/dead_bridge) the Validation
    // Suite app drives its offline-scenario gate off. Read-only, gate-free.
    if (toolName === "unity_open_mcp_bridge_status") {
      return this.routeBridgeStatus(args, live);
    }

    // find_references — offline-first when no bridge is connected; live
    // ReferenceGraph remains available when the bridge is up.
    if (toolName === "unity_open_mcp_find_references") {
      return this.routeFindReferences(args, live);
    }

    // dependencies — offline-first (M24 Plan 2 / T24.2). Forward + reverse
    // edges + impact analysis all compute from YAML on disk when no bridge is
    // connected; the live DependenciesTool remains available when the bridge
    // is up. The offline path is the only one that exposes include_impact
    // (transitive reverse closure) today.
    if (toolName === "unity_open_mcp_dependencies") {
      return this.routeDependencies(args, live);
    }

    // M22 — verify tools. The bridge returns the full issues[] list; we fold +
    // page server-side per the output profile (compact = counts only).
    if (toolName === "unity_open_mcp_validate_edit" || toolName === "unity_open_mcp_scan_paths") {
      return this.routeVerifyResult(toolName, args, live);
    }

    // M22 — scene_get_data. Resolves the output profile (onto detail) and pages
    // the node stream server-side; the bridge detail/max_nodes params keep
    // working as back-compat aliases.
    if (toolName === "unity_open_mcp_scene_get_data") {
      return this.routeSceneGetData(args, live);
    }

    // Compact drill-down reads: offline-first for text-serialized assets, fall
    // back to live bridge for binary formats. The compressible-router handles
    // the source selection internally.
    if (isCompressible(toolName)) {
      const result = await routeCompressible(
        toolName,
        args,
        live,
        this.modelCache,
        this.projectPath,
      );
      return injectRouteMeta(result, { route: "live" });
    }

    const canBatch = this.batch.isBatchTool(toolName);
    const isPing = toolName === "unity_open_mcp_ping";

    // compile_check always routes to batch — even when the live bridge is up.
    // The whole point is to spawn a FRESH Unity that recompiles from scratch;
    // running it against a live Editor that already compiled successfully
    // would trivially report "compile_passed" and never surface the broken
    // state the tool exists to diagnose.
    if (toolName === "unity_open_mcp_compile_check") {
      console.error(
        `[unity-open-mcp] Route: ${toolName} -> batch (compile check always spawns fresh)`,
      );
      const result = await this.batch.route(toolName, args);
      return injectRouteMeta(result, {
        route: "batch",
        fallbackReason: "compile_check_always_batch",
      });
    }

    if (!canBatch && !isPing) {
      const result = await live.route(toolName, args);
      return injectRouteMeta(result, { route: "live" });
    }

    const liveAvailable = await live.isLiveAvailable();

    if (liveAvailable) {
      console.error(`[unity-open-mcp] Route: ${toolName} -> live`);
      const result = await live.route(toolName, args);
      return injectRouteMeta(result, { route: "live" });
    }

    console.error(
      `[unity-open-mcp] Route: ${toolName} -> batch (live bridge unavailable)`,
    );

    if (isPing) {
      return this.batchPingResult();
    }

    const result = await this.batch.route(toolName, args);
    return injectRouteMeta(result, {
      route: "batch",
      fallbackReason: "live_unavailable",
    });
  }

  private batchPingResult(): CallToolResult {
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            connected: false,
            projectPath: this.projectPath,
            unityVersion: "unknown",
            bridgeVersion: "unknown",
            mode: "batch",
            compiling: false,
            isPlaying: false,
            _route: {
              route: "batch",
              fallbackReason: "live_unavailable",
            },
          }),
        },
      ],
      isError: false,
    };
  }

  private async routeReadCompileErrors(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const tailBytes =
      typeof args.tail_bytes === "number" && args.tail_bytes >= 4096
        ? Math.min(Math.floor(args.tail_bytes), 1048576)
        : DEFAULT_LOG_TAIL_BYTES;

    const logPath = editorLogPath();
    const tail = readLogTail(logPath, tailBytes);

    if (tail.error) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              error: {
                code: "editor_log_unreadable",
                message: `Could not read Editor.log at ${logPath}: ${tail.error}`,
                logPath,
              },
            }),
          },
        ],
        isError: true,
      };
    }

    if (!tail.exists) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              status: "log_not_found",
              errorCount: 0,
              errors: [],
              message: `No Editor.log found at ${logPath}. The Unity Editor ` +
                "may not have written errors yet, or it is running with a " +
                "custom -logFile path that this tool does not resolve.",
              logPath,
            }),
          },
        ],
        isError: false,
      };
    }

    // Build the full health summary: CSxxxx compiler errors PLUS package /
    // assembly-level red flags (unresolved-assembly Cecil failures, package
    // deprecation, Package Manager errors) from the SAME log tail. The
    // compilerErrors list is the same shape as before (kept as `errors` for
    // backward compatibility) so existing callers do not break.
    const health = summarizeProjectHealth(tail.content);
    const errors = health.compilerErrors;
    const status = health.unhealthy
      ? errors.length > 0
        ? "compile_failed"
        : "project_unhealthy"
      : "no_errors_found";
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            status,
            unhealthy: health.unhealthy,
            headline: health.headline,
            errorCount: errors.length,
            errors,
            // Package / assembly issues from the same log tail. Empty when the
            // only red flags are compiler errors (the common case).
            issues: health.issues,
            issueCount: health.issues.length,
            logPath,
            tailBytes: tail.bytes,
            _source: "offline",
          }),
        },
      ],
      isError: false,
    };
  }

  private async routeListAssets(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    try {
      const result = await listAssetsOffline({
        folder: typeof args.folder === "string" ? args.folder : "Assets",
        type: typeof args.type === "string" ? args.type : undefined,
        maxPerFolder: typeof args.max_per_folder === "number" ? args.max_per_folder : 30,
        projectRoot: this.projectPath,
      });
      return {
        content: [{ type: "text", text: JSON.stringify({ ...result, _source: "offline" }) }],
        isError: false,
      };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return {
        content: [
          { type: "text", text: JSON.stringify({ error: { code: "offline_error", message } }) },
        ],
        isError: true,
      };
    }
  }

  // M13 T4.4 — drain the bridge event queue. The SSE reader is started lazily
  // on the first call; subsequent calls return only events that arrived since
  // the previous drain. The subscription never buffers across server restarts
  // (a fresh subscriber id is minted per process), so a restarted MCP server
  // begins "now" — agents that care about historical logs should still call
  // unity_senses_read_console.
  private async routePullEvents(
    args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const liveAvailable = await live.isLiveAvailable();
    if (!liveAvailable) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              error: {
                code: "bridge_unavailable",
                message:
                  "Bridge event stream requires a live Unity Editor connection. " +
                  "Ensure the Unity Editor is open with the Agent Bridge running. " +
                  "The bridge port is per-project (20000 + sha256(projectPath) % 10000), not fixed — " +
                  `if Unity is open, check the instance lock at ${lockPath(this.projectPath)} ` +
                  "for the live port/pid, or set UNITY_OPEN_MCP_BRIDGE_PORT.",
              },
            }),
          },
        ],
        isError: true,
      };
    }

    const maxEvents =
      typeof args.max_events === "number" && args.max_events > 0
        ? Math.min(args.max_events, 1000)
        : 50;

    const result = this.eventStream.pull(maxEvents);
    return {
      content: [{ type: "text", text: JSON.stringify(result) }],
      isError: false,
    };
  }

  private async routeCapabilities(
    args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const kind =
      args.kind === "tools" || args.kind === "rules" || args.kind === "fixes"
        ? args.kind
        : undefined;
    const includePlanned = args.include_planned !== false;

    // M18 Plan 2 / T18.2.3 — when the bridge is live, probe its compiled-
    // state tool inventory so per-group `available` reflects whether each
    // domain dependency compiled in (e.g. UNITY_OPEN_MCP_EXT_NAVIGATION).
    // Capabilities stays local-route; the bridge probe is a read-only fetch
    // that does not change the route classification.
    let availableBridgeTools: ReadonlySet<string> | undefined;
    const liveAvailable = await live.isLiveAvailable();
    if (liveAvailable) {
      const inventory = await live.listBridgeTools();
      if (inventory) availableBridgeTools = inventory.tools;
    }

    // M20 Plan 7 / T20.7.0 — reconcile auto-activation before reporting the
    // group catalog so the capability output reflects packages that came
    // online since the last call. Auto-activated groups surface with
    // `autoActivated: true` + `packageDependency` (see build-capabilities).
    // Reuse the inventory already fetched above (no second GET /tools hop).
    await this.reconcileAutoActivation(
      availableBridgeTools ? { tools: availableBridgeTools } : null,
    );

    const result = buildCapabilities(
      {
        tools: ALL_TOOLS,
        batchToolNames: BATCH_TOOL_NAMES,
        rules: RULE_CATALOG,
        fixes: FIX_CATALOG,
        availableBridgeTools,
      },
      { kind, includePlanned },
    );
    return {
      content: [
        { type: "text", text: JSON.stringify({ ...result, _source: "local" }) },
      ],
      isError: false,
    };
  }

  private async routeListRules(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const assetKind =
      typeof args.asset_kind === "string" ? args.asset_kind : undefined;
    const extension =
      typeof args.extension === "string" ? args.extension : undefined;
    const implementedOnly = args.implemented_only === true;

    const result = listRules(
      { rules: RULE_CATALOG, fixes: FIX_CATALOG },
      { assetKind, extension, implementedOnly },
    );
    return {
      content: [
        { type: "text", text: JSON.stringify({ ...result, _source: "local" }) },
      ],
      isError: false,
    };
  }

  private async routeGenerateSkill(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const write = args.write === true;
    const rawClients = Array.isArray(args.clients) ? args.clients : [];
    // Client allowlist comes from the single-source manifest at
    // `skills/client-paths.json`. Do not add a literal union here.
    const allowedClients = new Set(knownClientKeys());
    const clients = rawClients.filter(
      (c): c is string => typeof c === "string" && allowedClients.has(c),
    );

    const caps = buildCapabilities(
      {
        tools: ALL_TOOLS,
        batchToolNames: BATCH_TOOL_NAMES,
        rules: RULE_CATALOG,
        fixes: FIX_CATALOG,
      },
      { includePlanned: false },
    );

    try {
      const result = await generateSkill(this.projectPath, caps, {
        write,
        clients: clients.length > 0 ? clients : undefined,
      });
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              skill: result.skill,
              project: result.project,
              written: result.written,
              _source: "local",
            }),
          },
        ],
        isError: false,
      };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              error: { code: "skill_generation_failed", message },
            }),
          },
        ],
        isError: true,
      };
    }
  }

  // M18 Plan 2 / T18.2.2 — manage_tools routes local. Mutates the per-
  // session tool-group visibility state (ToolSessionState) that ListTools
  // consults to filter tools. The bridge does not see these calls — it is
  // session-state the MCP server owns (the resolved decision in M18
  // execution-plan.md).
  //
  // Compiled-state availability (whether the bridge compiled the domain in)
  // is reported in the `available` + `availableReason` fields per group. The
  // MCP server detects this by querying the bridge /capabilities surface
  // when reachable; when the bridge is offline it reports `available: null`
  // (unknown) and the agent should fall back to `unity_open_mcp_capabilities`
  // for the authoritative compiled-state report.
  private async routeManageTools(
    args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const action = typeof args.action === "string" ? args.action : "";
    const group = typeof args.group === "string" ? args.group.trim() : "";

    if (action === "list_groups") {
      return this.manageToolsListGroups(live);
    }

    if (action === "reset") {
      const before = this.sessionState.activeGroups();
      this.sessionState.reset();
      await this.maybeNotifyToolListChanged(before);
      return this.manageToolsResult({
        reset: true,
        activeGroups: this.sessionState.activeGroups(),
        message:
          "Tool-group visibility restored to defaults (`core` only). " +
          "MCP clients that support listChanged will refresh ListTools automatically.",
      });
    }

    if (action === "activate" || action === "deactivate") {
      if (!group) {
        return this.manageToolsError(
          "missing_parameter",
          `'group' is required for action '${action}'.`,
        );
      }
      if (!GROUP_IDS.has(group)) {
        return this.manageToolsError(
          "unknown_group",
          `Unknown group '${group}'. Valid ids: ${Array.from(GROUP_IDS)
            .sort()
            .join(", ")}.`,
        );
      }
      const changed =
        action === "activate"
          ? this.sessionState.activate(group)
          : this.sessionState.deactivate(group);
      if (changed) {
        await this.onToolListChanged?.();
      }
      return this.manageToolsResult({
        action,
        group,
        changed,
        activeGroups: this.sessionState.activeGroups(),
        message:
          action === "activate"
            ? changed
              ? `Group '${group}' activated. Its tools will appear in subsequent ListTools responses; MCP clients that support listChanged will refresh automatically.`
              : `Group '${group}' was already active.`
            : changed
              ? `Group '${group}' deactivated. Its tools are now hidden from ListTools; MCP clients that support listChanged will refresh automatically.`
              : `Group '${group}' was already inactive.`,
      });
    }

    return this.manageToolsError(
      "unknown_action",
      `Unknown action '${action}'. Valid actions: list_groups, activate, deactivate, reset.`,
    );
  }

  private async manageToolsListGroups(live: LiveClient): Promise<CallToolResult> {
    // M20 Plan 7 / T20.7.0 — fetch the compiled-state inventory once and
    // reuse it for both auto-activation reconciliation and compiled-state
    // availability (two GET /tools hops → one). When the bridge is offline
    // both paths degrade gracefully (no auto-activation; availability=null).
    const liveAvailable = await live.isLiveAvailable();
    const inventory = liveAvailable
      ? await live.listBridgeTools()
      : null;
    // Reconcile auto-activation first so the listed active set reflects
    // packages that came online since the last call.
    await this.reconcileAutoActivation(
      inventory ? { tools: inventory.tools } : null,
    );
    const groupsList = groupToTools();
    const compiledAvailability = this.computeCompiledAvailability(
      inventory,
      groupsList,
    );

    const groups = TOOL_GROUPS.map((g) => {
      const tools = groupsList[g.id] ?? [];
      const availability = compiledAvailability.get(g.id);
      return {
        id: g.id,
        description: g.description,
        active: this.sessionState.isGroupActive(g.id),
        // M20 Plan 7 / T20.7.0 — why the group is active (default / manual /
        // auto). `null` when the group is not active. Lets an agent learn
        // that a group is visible because its package is installed, not
        // because the operator opted in.
        activationSource: this.sessionState.activationSource(g.id),
        autoActivated: this.sessionState.activationSource(g.id) === "auto",
        defaultEnabled: g.defaultEnabled,
        // Compiled-state availability of the domain dependency. `true` for
        // groups without a domainDefine (always compiled in); `false` /
        // `null` (unknown — bridge offline) otherwise. Session activation
        // is independent: an agent can activate a group whose dependency is
        // missing; the tools will appear in ListTools but error at call
        // time. capabilities is the authoritative compiled-state source.
        available: availability?.available ?? null,
        availableReason: availability?.reason ?? null,
        unityPackage: g.unityPackage ?? null,
        packageDependency: g.unityPackage ?? null,
        toolCount: tools.length,
        tools,
      };
    });

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            groups,
            activeGroups: this.sessionState.activeGroups(),
            note:
              "Activate a group to add its tools to your ListTools surface; " +
              "deactivate to hide them. State is per-session and ephemeral — " +
              "it resets to `core`-only when the MCP server restarts. " +
              "Compiled-state availability (the `available` field) reflects " +
              "whether the Unity domain dependency is compiled in; use " +
              "unity_open_mcp_capabilities for the authoritative compiled-state " +
              "report.",
            _source: "local",
          }),
        },
      ],
      isError: false,
    };
  }

  /**
   * Notify MCP clients when the filtered ListTools surface changed. Skips the
   * callback when the active group set is unchanged (e.g. no-op reset).
   */
  private async maybeNotifyToolListChanged(
    before: readonly string[],
  ): Promise<void> {
    if (!this.onToolListChanged) return;
    const after = this.sessionState.activeGroups();
    if (activeGroupsEqual(before, after)) return;
    await this.onToolListChanged();
  }

  // M20 Plan 7 / T20.7.0 — reconcile the auto-activated set against the
  // currently-compiled-in bridge inventory. A group with `autoActivate: true`
  // is considered package-present when any of its compiled-in tool names
  // appears in the bridge's `GET /tools` inventory — the same signal
  // `resolveCompiledAvailability` uses, so no extra package_list round-trip.
  // The session store records the outcome; we fire the listChanged
  // notification exactly once when the active set changed. Returns true when
  // the active set changed (so callers like capabilities / list_groups can
  // report a fresh snapshot).
  //
  // Callers that have ALREADY fetched the bridge inventory (capabilities,
  // list_groups) pass it via `prefetchedInventory` to avoid a second
  // `GET /tools` round-trip on the same call.
  private async reconcileAutoActivation(
    prefetchedInventory?: {
      tools: ReadonlySet<string>;
    } | null,
  ): Promise<boolean> {
    if (AUTO_ACTIVATE_GROUPS.length === 0) return false;
    let inventory = prefetchedInventory ?? null;
    if (!inventory) {
      const liveAvailable = await this.live.isLiveAvailable();
      if (!liveAvailable) return false;
      inventory = await this.live.listBridgeTools();
      if (!inventory) return false;
    }
    const groupsList = groupToTools();

    const satisfied = new Set<string>();
    for (const entry of AUTO_ACTIVATE_GROUPS) {
      const groupTools = groupsList[entry.groupId] ?? [];
      const anyCompiledIn =
        groupTools.length > 0 &&
        groupTools.some((t) => inventory!.tools.has(t));
      if (anyCompiledIn) satisfied.add(entry.groupId);
    }

    const before = this.sessionState.activeGroups();
    const changed = this.sessionState.reconcileAutoActivation(satisfied);
    if (changed.length > 0) {
      await this.maybeNotifyToolListChanged(before);
      return true;
    }
    return false;
  }

  // Resolve compiled-state availability per group from the live bridge. The
  // bridge reports which tools it compiled in via `GET /tools`; a domain-
  // gated group is `available: true` when any of its compiled-in tool names
  // appears in that set, `false` when none do (Unity domain package not
  // installed), and `null` (unknown) when the bridge is offline.
  //
  // Thin async wrapper over {@link computeCompiledAvailability}: fetches the
  // inventory (or null when the bridge is offline) and delegates. Kept for
  // the few call sites that don't already have an inventory in hand.
  private async resolveCompiledAvailability(): Promise<
    Map<string, { available: boolean | null; reason: string | null }>
  > {
    const liveAvailable = await this.live.isLiveAvailable();
    const inventory = liveAvailable
      ? await this.live.listBridgeTools()
      : null;
    return this.computeCompiledAvailability(inventory, groupToTools());
  }

  // Pure per-group availability computation given a (possibly null) bridge
  // tool inventory. Extracted so callers that already fetched the inventory
  // (capabilities, list_groups) can avoid a second GET /tools hop.
  private computeCompiledAvailability(
    inventory: { tools: ReadonlySet<string> } | null,
    groupsList: Record<string, string[]>,
  ): Map<string, { available: boolean | null; reason: string | null }> {
    const out = new Map<string, { available: boolean | null; reason: string | null }>();

    for (const g of TOOL_GROUPS) {
      if (!g.domainDefine) {
        out.set(g.id, { available: true, reason: null });
        continue;
      }
      if (!inventory) {
        out.set(g.id, {
          available: null,
          reason:
            "Bridge offline — compiled-state availability unknown. Call " +
            "unity_open_mcp_capabilities for the authoritative compiled-state " +
            "report.",
        });
        continue;
      }
      const groupTools = groupsList[g.id] ?? [];
      const anyCompiledIn =
        groupTools.length > 0 &&
        groupTools.some((t) => inventory.tools.has(t));
      out.set(g.id, {
        available: anyCompiledIn,
        reason: anyCompiledIn
          ? null
          : `Unity package '${g.unityPackage}' not installed — the bridge ` +
            `did not compile the ${g.domainDefine} domain.`,
      });
    }
    return out;
  }

  private manageToolsResult(
    body: Record<string, unknown>,
  ): CallToolResult {
    return {
      content: [
        { type: "text", text: JSON.stringify({ ...body, _source: "local" }) },
      ],
      isError: false,
    };
  }

  private manageToolsError(code: string, message: string): CallToolResult {
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ error: { code, message }, _source: "local" }),
        },
      ],
      isError: true,
    };
  }

  // testsuite-tauri phase-3 — bridge_status. Combines the instance-lock
  // classifier (the same pure function the LiveClient uses for dead-bridge
  // detection) with a single /ping probe. The /ping is driven through the
  // LiveClient's ping handler so it mirrors what `unity_open_mcp_ping` and
  // the CLI `ping`/`wait-for-ready` see (auth header, ping cache, body shape).
  //
  // The coarse `status` token is derived from the two signals:
  //
  //   lock="dead_bridge"                       → "dead_bridge"
  //   /ping reachable, compiling=true          → "compiling"
  //   /ping reachable, connected=true          → "running"
  //   /ping UNreachable + Unity running        → "unreachable" (T-fix-3)
  //   otherwise (offline / toolbar off / gone) → "stopped"
  //
  // "stopped" intentionally folds two indistinguishable cases from the
  // MCP-server's vantage point: Unity is not running at all, OR Unity is
  // running but the operator toggled the bridge off via the toolbar. The
  // lock summary in the response disambiguates them (`lock === null` → no
  // Unity; `lock.pid` alive but no listener → toolbar off). The tool never
  // errors on an offline bridge — `stopped` IS the answer in that case.
  //
  // M20 Plan 4-5 / T-fix-3 — "unreachable" splits out the case where Unity IS
  // running (lock PID alive, often mid-reload) but the listener did not
  // respond. Pre-fix this collapsed to "stopped", hiding transient reload-
  // window flakiness behind a clean-looking status. "unreachable" makes it
  // visible so agents retry instead of treating it as a clean stop. The
  // `isError:false` contract is preserved — this is richer status, not an
  // error path.
  private async routeBridgeStatus(
    _args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const lockOnDisk = lockPath(this.projectPath);
    let lock: InstanceLock | null = null;
    try {
      lock = readInstanceLock(this.projectPath);
    } catch {
      // Unreadable lock → treat as no lock (gone).
      lock = null;
    }
    const classification = classifyInstance(lock);

    // One /ping probe. The LiveClient's ping handler returns the full body
    // on success, or an isError result with `bridge_offline` on ECONNREFUSED
    // — both shapes are valid inputs here. A compile-in-progress bridge
    // returns HTTP 503 from /ping which isLiveAvailable() treats as reachable;
    // the ping body's `compiling` field is the authoritative signal.
    const pingResult = await live.route("unity_open_mcp_ping", {});
    const pingBody = parsePingBody(pingResult);
    const pingReachable = !pingResult.isError && pingBody !== null;
    const compiling = pingReachable === true && pingBody?.compiling === true;
    const connected = pingReachable === true && pingBody?.connected === true;

    // T-fix-3 — when the ping is unreachable, distinguish "Unity is running but
    // the listener is momentarily down" (lock PID alive / reload window) from a
    // genuine "nothing there" stop. classification "reloading" covers the
    // domain-reload window; a live-PID "healthy"/"gone"-with-alive-pid lock
    // also indicates Unity is up.
    const lockPidAlive = lock !== null && isPidAlive(lock.pid);

    let status: "running" | "compiling" | "stopped" | "dead_bridge" | "unreachable";
    if (classification === "dead_bridge") {
      status = "dead_bridge";
    } else if (compiling) {
      status = "compiling";
    } else if (connected) {
      status = "running";
    } else if (!pingReachable && (classification === "reloading" || lockPidAlive)) {
      // Unity is running but the listener didn't respond — transient (reload
      // window) or toolbar-off. Surface as unreachable so it isn't masked by
      // a clean "stopped".
      status = "unreachable";
    } else {
      status = "stopped";
    }

    const body = {
      status,
      // Coarse ready flag for clients that want a single boolean: true only
      // when the bridge is connected AND idle (the same rule wait-for-ready
      // uses for poll termination).
      ready: status === "running",
      projectPath: this.projectPath,
      // M23 Plan 2 — top-level mirror of the instance-lock classification
      // (healthy | reloading | dead_bridge | gone) + a structured recovery
      // hint. Mirrors `instance.classification` at the top level so agents
      // can branch on a single field without digging into the instance
      // sub-object; `recoveryHint` is null unless the status has a specific
      // recovery tool to call (today: dead_bridge → read_compile_errors).
      // Feedback 2026-06-29: a dead_bridge must read as "Unity likely in
      // Safe Mode / compile failure", not the generic "stopped" the bare
      // status used to suggest.
      classification,
      recoveryHint: bridgeStatusRecoveryHint(status),
      instance: {
        lockPath: lockOnDisk,
        classification,
        lock: lock ? summarizeBridgeStatusLock(lock) : null,
      },
      ping: pingReachable
        ? {
            reachable: true,
            connected: pingBody?.connected ?? null,
            compiling: pingBody?.compiling ?? null,
            isPlaying: pingBody?.isPlaying ?? null,
            unityVersion: pingBody?.unityVersion ?? null,
            bridgeVersion: pingBody?.bridgeVersion ?? null,
            mode: pingBody?.mode ?? null,
          }
        : { reachable: false },
      nextStep: bridgeStatusNextStep(status),
      _source: "local",
    };

    // bridge_status never reports an error — even a stopped bridge is a
    // successful status read. _source=local because the synthesis happens in
    // the MCP server (no bridge tool endpoint, no batch Unity).
    return {
      content: [{ type: "text", text: JSON.stringify(body) }],
      isError: false,
    };
  }

  private async routeFindReferences(
    args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    // M22 — resolve profile -> detail. compact (default) maps to summary
    // (counts/byKind/byFolder only); balanced -> normal; full -> verbose.
    const { detail, profile } = readProfileAndDetail(args, "summary");
    const wantPaging = typeof args.page_size === "number" && args.page_size > 0;

    const liveAvailable = await live.isLiveAvailable();
    if (liveAvailable) {
      console.error("[unity-open-mcp] Route: find_references -> live");
      // The live bridge only honors max_results (no detail/per-file/threshold).
      // Ask for the full set when paging so the cursor can walk it; otherwise
      // honor the legacy max_results cap.
      const liveArgs: Record<string, unknown> = {
        ...args,
        max_results: wantPaging ? 0 : (typeof args.max_results === "number" ? args.max_results : 100),
      };
      const result = await live.route("unity_open_mcp_find_references", liveArgs);
      const folded = foldFindReferencesLive(result, profile, wantPaging, args);
      return injectRouteMeta(folded, { route: "live" });
    }

    console.error(
      "[unity-open-mcp] Route: find_references -> offline (live bridge unavailable)",
    );

    const assetPath = typeof args.asset_path === "string" ? args.asset_path : undefined;
    const guid = typeof args.guid === "string" ? args.guid : undefined;

    if (!assetPath && !guid) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              error: {
                code: "missing_parameter",
                message: "Either 'asset_path' or 'guid' is required.",
              },
            }),
          },
        ],
        isError: true,
      };
    }

    try {
      const result = await findReferencesOffline({
        assetPath,
        guid,
        detail,
        // When paging, fetch the full set; otherwise honor the legacy cap.
        maxResults: wantPaging ? 0 : (typeof args.max_results === "number" ? args.max_results : 100),
        maxPerFile: typeof args.max_per_file === "number" ? args.max_per_file : 5,
        patternThreshold: typeof args.pattern_threshold === "number" ? args.pattern_threshold : 0,
        projectRoot: this.projectPath,
      });
      // M22 — page the referencedBy list when requested (balanced/full only;
      // compact returns an empty list by design).
      const withPaging = wantPaging
        ? pageFindReferencesResult(result, args.page_size as number, typeof args.cursor === "string" ? (args.cursor as string) : undefined)
        : result;
      return {
        content: [
          { type: "text", text: JSON.stringify({ ...withPaging, _source: "offline" }) },
        ],
        isError: false,
      };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              error: { code: "offline_error", message },
            }),
          },
        ],
        isError: true,
      };
    }
  }

  // M24 Plan 2 / T24.2 — dependencies (forward + reverse edges + impact
  // analysis). Offline-first when no bridge is connected; the live
  // DependenciesTool is the fallback when the bridge is up. The live tool does
  // not yet honor include_impact / max_impact_depth — those are offline-only
  // today, so when the bridge is up and impact is requested we route offline
  // regardless (the offline path is the only one with the transitive closure).
  private async routeDependencies(
    args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const { detail } = readProfileAndDetail(args, "normal");
    const assetPath = typeof args.asset_path === "string" ? args.asset_path : undefined;
    const guid = typeof args.guid === "string" ? args.guid : undefined;
    const includeImpact = args.include_impact === true;

    if (!assetPath && !guid) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              error: {
                code: "missing_parameter",
                message: "Either 'asset_path' or 'guid' is required.",
              },
            }),
          },
        ],
        isError: true,
      };
    }

    const liveAvailable = await live.isLiveAvailable();

    // Impact analysis is offline-only. When requested, go straight to the
    // offline path even if the bridge is up — the live DependenciesTool has no
    // transitive-closure surface yet.
    if (!includeImpact && liveAvailable) {
      console.error("[unity-open-mcp] Route: dependencies -> live");
      // The live tool honors asset_path/guid/detail/max_results only.
      const liveArgs: Record<string, unknown> = {
        asset_path: assetPath,
        guid,
        detail,
        max_results: typeof args.max_results === "number" ? args.max_results : 100,
      };
      const result = await live.route("unity_open_mcp_dependencies", liveArgs);
      return injectRouteMeta(result, { route: "live" });
    }

    console.error(
      includeImpact && liveAvailable
        ? "[unity-open-mcp] Route: dependencies -> offline (include_impact is offline-only)"
        : "[unity-open-mcp] Route: dependencies -> offline (live bridge unavailable)",
    );

    try {
      const result = await dependenciesOffline({
        assetPath,
        guid,
        detail,
        maxResults: typeof args.max_results === "number" ? args.max_results : 100,
        includeImpact,
        maxImpactDepth: typeof args.max_impact_depth === "number" ? args.max_impact_depth : 5,
        projectRoot: this.projectPath,
      });
      return {
        content: [{ type: "text", text: JSON.stringify(result) }],
        isError: false,
      };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              error: { code: "offline_error", message },
            }),
          },
        ],
        isError: true,
      };
    }
  }

  // M22 — verify tools (validate_edit / scan_paths). The bridge always returns
  // the full issues[] list; we fold + page server-side per the output profile
  // (compact = counts only, balanced/full keep + page issues).
  private async routeVerifyResult(
    toolName: string,
    args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const { profile } = readProfileAndDetail(args, "summary");
    const pageSize = typeof args.page_size === "number" && args.page_size > 0 ? (args.page_size as number) : 0;
    const cursor = typeof args.cursor === "string" ? (args.cursor as string) : undefined;

    // Forward to live/batch exactly as the generic path would. The bridge
    // ignores the unknown profile/page_size/cursor keys (JsonBody substring-
    // searches only for keys it asks for), so they pass through harmlessly.
    const canBatch = this.batch.isBatchTool(toolName);
    let raw: CallToolResult;
    if (!canBatch) {
      raw = await live.route(toolName, args);
      raw = injectRouteMeta(raw, { route: "live" });
    } else {
      const liveAvailable = await live.isLiveAvailable();
      if (liveAvailable) {
        raw = await live.route(toolName, args);
        raw = injectRouteMeta(raw, { route: "live" });
      } else {
        raw = await this.batch.route(toolName, args);
        raw = injectRouteMeta(raw, { route: "batch", fallbackReason: "live_unavailable" });
      }
    }

    const body = parseResultBody(raw);
    if (body === null) return raw;
    if (body.error) return raw;

    const folded = foldVerifyResult(body, toolName, profile, { page_size: pageSize, cursor });
    return withResultBody(raw, folded);
  }

  // M22 — scene_get_data. Resolves the output profile (onto detail) and pages
  // the node stream server-side. The bridge detail/max_nodes params keep
  // working as back-compat aliases; profile wins when both are present.
  private async routeSceneGetData(
    args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const { detail } = readProfileAndDetail(args, "summary");
    const wantPaging = typeof args.page_size === "number" && args.page_size > 0;

    // Build the args the bridge sees: profile resolved to detail (the bridge's
    // real knob), legacy max_nodes honored, paging keys stripped (the bridge
    // would ignore them anyway, but keep the call clean).
    const bridgeArgs: Record<string, unknown> = { ...args };
    delete bridgeArgs.profile;
    delete bridgeArgs.page_size;
    delete bridgeArgs.cursor;
    bridgeArgs.detail = detail;

    const canBatch = this.batch.isBatchTool("unity_open_mcp_scene_get_data");
    let raw: CallToolResult;
    if (!canBatch) {
      raw = await live.route("unity_open_mcp_scene_get_data", bridgeArgs);
      raw = injectRouteMeta(raw, { route: "live" });
    } else {
      const liveAvailable = await live.isLiveAvailable();
      if (liveAvailable) {
        raw = await live.route("unity_open_mcp_scene_get_data", bridgeArgs);
        raw = injectRouteMeta(raw, { route: "live" });
      } else {
        raw = await this.batch.route("unity_open_mcp_scene_get_data", bridgeArgs);
        raw = injectRouteMeta(raw, { route: "batch", fallbackReason: "live_unavailable" });
      }
    }

    if (!wantPaging) return raw;

    const body = parseResultBody(raw);
    if (body === null) return raw;
    if (body.error) return raw;

    const pageSize = args.page_size as number;
    const cursor = typeof args.cursor === "string" ? (args.cursor as string) : undefined;
    const paged = pageSceneNodes(body, pageSize, cursor);
    return withResultBody(raw, paged);
  }
}

/**
 * Page the scene_get_data node stream. The bridge emits a per-root `roots`
 * array (summary: root roster) or a nested hierarchy (normal/verbose). We page
 * the FLATTENED node stream so the cursor is a stable position regardless of
 * the detail mode, then re-hang the page as a flat `nodes` array with the
 * original scene metadata preserved.
 */
function pageSceneNodes(
  body: Record<string, unknown>,
  pageSize: number,
  cursor: string | undefined,
): Record<string, unknown> {
  const flat = flattenSceneBody(body);
  const { page, block } = applyPaging(flat, "scene_get_data", { page_size: pageSize, cursor });

  // Preserve scene-level metadata; replace the structural root/hierarchy fields
  // with the flat paged node list + a note explaining the page shape.
  const { roots, rootGameObjects, nodes, truncated, pagination: _existing, ...meta } = body;
  void roots;
  void rootGameObjects;
  void nodes;
  void truncated;
  void _existing;

  return attachPagination(
    {
      ...meta,
      nodes: page,
      pagingNote: "page_size returns a flattened node stream; re-read without page_size for the structural root/hierarchy view",
    },
    block,
  );
}

/**
 * Flatten the scene body into a node stream for paging. Handles both the
 * summary root roster (array of root objects) and the nested normal/verbose
 * hierarchy (objects with `children`).
 */
function flattenSceneBody(body: Record<string, unknown>): Record<string, unknown>[] {
  const out: Record<string, unknown>[] = [];
  const walk = (node: unknown) => {
    if (!node || typeof node !== "object") return;
    const obj = node as Record<string, unknown>;
    out.push(obj);
    const children = obj.children;
    if (Array.isArray(children)) {
      for (const child of children) walk(child);
    }
  };

  // The bridge emits `roots` (summary) or `rootGameObjects` (normal/verbose);
  // accept either.
  const rootField = Array.isArray(body.roots) ? body.roots : body.rootGameObjects;
  if (Array.isArray(rootField)) {
    for (const root of rootField) walk(root);
  }
  return out;
}
