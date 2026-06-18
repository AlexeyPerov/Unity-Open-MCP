import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import type { BridgeEventStream } from "./event-stream.js";
import { AssetModelCache, isCompressible, routeCompressible } from "./compressible-router.js";
import { listAssetsOffline, findReferencesOffline } from "./offline.js";
import { editorLogPath, readLogTail, DEFAULT_LOG_TAIL_BYTES } from "./unity-log.js";
import { extractStructuredCompilerErrors } from "./compiler-errors.js";
import { buildCapabilities } from "./capabilities/build-capabilities.js";
import { RULE_CATALOG, FIX_CATALOG } from "./capabilities/rule-catalog.js";
import { listRules } from "./capabilities/list-rules.js";
import { generateSkill } from "./skill/generate-skill.js";
import { knownClientKeys } from "./skill/client-paths.js";
import { ALL_TOOLS } from "./tools/index.js";
import { lockPath } from "./instance-discovery.js";
import { BATCH_TOOL_NAMES } from "./batch-spawn.js";

export interface RouteMeta {
  route: "live" | "batch";
  fallbackReason?: string;
}

function injectRouteMeta(
  result: CallToolResult,
  meta: RouteMeta,
): CallToolResult {
  if (result.content.length === 0) return result;
  const first = result.content[0];
  if (first.type !== "text") return result;
  try {
    const body = JSON.parse(first.text) as Record<string, unknown>;
    body._route = meta;
    return {
      ...result,
      content: [{ type: "text", text: JSON.stringify(body) }],
    };
  } catch {
    return result;
  }
}

export class ToolRouter implements Router {
  private readonly modelCache = new AssetModelCache();
  constructor(
    private live: LiveClient,
    private batch: BatchSpawn,
    private projectPath: string,
    private eventStream: BridgeEventStream,
  ) {}

  async route(
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
      return this.routePullEvents(args);
    }

    // capabilities — local capability-discovery surface (no live/batch hop).
    if (toolName === "unity_open_mcp_capabilities") {
      return this.routeCapabilities(args);
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

    // find_references — offline-first when no bridge is connected; live
    // ReferenceGraph remains available when the bridge is up.
    if (toolName === "unity_open_mcp_find_references") {
      return this.routeFindReferences(args);
    }

    // Compact drill-down reads: offline-first for text-serialized assets, fall
    // back to live bridge for binary formats. The compressible-router handles
    // the source selection internally.
    if (isCompressible(toolName)) {
      const result = await routeCompressible(
        toolName,
        args,
        this.live,
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
      const result = await this.live.route(toolName, args);
      return injectRouteMeta(result, { route: "live" });
    }

    const liveAvailable = await this.live.isLiveAvailable();

    if (liveAvailable) {
      console.error(`[unity-open-mcp] Route: ${toolName} -> live`);
      const result = await this.live.route(toolName, args);
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

    const errors = extractStructuredCompilerErrors(tail.content);
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            status: errors.length > 0 ? "compile_failed" : "no_errors_found",
            errorCount: errors.length,
            errors,
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
  ): Promise<CallToolResult> {
    const liveAvailable = await this.live.isLiveAvailable();
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
  ): Promise<CallToolResult> {
    const kind =
      args.kind === "tools" || args.kind === "rules" || args.kind === "fixes"
        ? args.kind
        : undefined;
    const includePlanned = args.include_planned !== false;

    const result = buildCapabilities(
      {
        tools: ALL_TOOLS,
        batchToolNames: BATCH_TOOL_NAMES,
        rules: RULE_CATALOG,
        fixes: FIX_CATALOG,
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

  private async routeFindReferences(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const liveAvailable = await this.live.isLiveAvailable();
    if (liveAvailable) {
      console.error("[unity-open-mcp] Route: find_references -> live");
      const result = await this.live.route("unity_open_mcp_find_references", args);
      return injectRouteMeta(result, { route: "live" });
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
        detail: typeof args.detail === "string" ? args.detail : "normal",
        maxResults: typeof args.max_results === "number" ? args.max_results : 100,
        maxPerFile: typeof args.max_per_file === "number" ? args.max_per_file : 5,
        patternThreshold: typeof args.pattern_threshold === "number" ? args.pattern_threshold : 0,
        projectRoot: this.projectPath,
      });
      return {
        content: [
          { type: "text", text: JSON.stringify({ ...result, _source: "offline" }) },
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
}
