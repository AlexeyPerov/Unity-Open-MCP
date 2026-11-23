import { randomUUID } from "node:crypto";
import { testRunOperation, projectCommandOperation, playmodeOperation, pollPlaymodeOperation, playmodeResult, toolOutput, toolResultBody, PLAYMODE_OPERATION_TOOLS, playmodeEstimateMs, playmodeFailure, playmodeTailMs, playmodeShouldHandOver, PlaymodeProgress, PLAYMODE_STILL_MS, type PlaymodeEstimate, editorWaitOperation } from "./jobs/adapters.js";
import {
  WAIT_TOOL, WAIT_DEFAULT_TIMEOUT_MS, WAIT_INLINE_LIMIT_MS, WAIT_MAX_TIMEOUT_MS, PENDING_COMPILE_GRACE_MS,
  readEditorSignals, viewEditor, statusFields, retryAfterMsFor, waitForEditor, describeWait, reloadingRefusal, waitCall, isTransitionState,
  type CompileReading, type WaitDeps, type WaitRequest, type WaitUntil, type EditorView,
} from "./editor-state.js";
import { bridgeCompileFailedBody, readBridgeLossEvidence, type BridgeLossEvidence } from "./bridge-loss.js";
import { COMPILE_RESULT_FIRST_ERRORS, compileToAwait, foldCompileResult, foldCompileJob } from "./compile-result.js";
import { lifecycleForCall } from "./capabilities/lifecycle.js";
import { PLAYMODE_ROUTES, PLAYMODE_INLINE_GRACE_MS } from "./playmode-routes.js";
import { routeGameView as settleGameView } from "./game-view-settle.js";
import { JobManager, JobManagerError, type JobOperation, type JobOwner } from "./jobs/job-manager.js";
import { handleJobs } from "./jobs/handler.js";
import { PROCESS_AGENT_ID, PROCESS_IDENTITY, type RequestIdentity } from "./agent-identity.js";
import { buildEditorSearch } from "./editor-search.js";
import { withSchemaDefaults } from "./schema-defaults.js";
import { validateSchema, wireArguments } from "./tool-contract.js";
import { discoverTools, compactDiscoverySchema } from "./capabilities/discovery.js";
import { PingCache } from "./ping-cache.js";
import { resolveProjectPath, validateUnityProjectRoot } from "./project-path.js";
import { resolvePort, resolveAuthToken } from "./instance-discovery.js";
import { skipsReloadProbe } from "./local-tools.js";
import { confirmBridgeReleaseTag, resolveLatestVersion, type BridgeTagConfirmation } from "./release-lookup.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import { LiveClient, type PingResponse } from "./live-client.js";
import type { BridgeEvent, BridgeEventStream } from "./event-stream.js";
import { AssetModelCache, isCompressible, routeCompressible } from "./compressible-router.js";
import { listAssetsOffline, findReferencesOffline, dependenciesOffline } from "./offline.js";
import { resolveEditorLogPath, readLogTail, DEFAULT_LOG_TAIL_BYTES, detectStaleLog, detectStaleAssembly, detectErrorsPredatingEdits, parseLogAuthorship, compareLogAuthorship, classifyLogCompileErrors, newestScriptAssemblyMtimeMs, statMtimeMs, type ClassifiedLogErrors, type HistoricalReason } from "./unity-log.js";
import { toolHintReference } from "./tool-hint.js";
import { scanForFdExhaustion } from "./editor-wedge.js";
import { summarizeProjectHealth, extractProjectHealthIssues } from "./project-health.js";
import { countProjectAsmdefs, countAssembliesWithErrors } from "./asmdef-discovery.js";
import { buildCapabilities } from "./capabilities/build-capabilities.js";
import { RULE_CATALOG, FIX_CATALOG } from "./capabilities/rule-catalog.js";
import {
  GROUP_IDS,
  TOOL_GROUPS,
  AUTO_ACTIVATE_GROUPS,
  groupToTools,
  getGroup,
  groupFor,
} from "./capabilities/tool-groups.js";
import { closeToolNames } from "./tool-name-suggest.js";
import { typedToolHints } from "./typed-tool-hints.js";
import {
  recommendGroups,
  type Recommendation,
  type RecommendedGroup,
} from "./capabilities/intent-groups.js";
import { listRules } from "./capabilities/list-rules.js";
import { viewCapabilities } from "./capabilities/capabilities-view.js";
import { generateSkill } from "./skill/generate-skill.js";
import { getKnownClientKeys } from "./skill/client-paths.js";
import { ALL_TOOLS } from "./tools/index.js";
import { lockPath, readInstanceLock, classifyInstance, isPidAlive, hasRecentPendingTestRun, findInstanceLockByPort, type InstanceLock } from "./instance-discovery.js";
import { PORT_ENV_VAR, EXPECTED_BRIDGE_WIRE_CONTRACT, BRIDGE_HOST_SAFE_TIMEOUT_CAP_MS } from "./constants.js";
import { findUnityForProject, readProcessCommandLine } from "./running-unity.js";
import { PlaytestRecorder, PlaytestError, PLAYTEST_SESSION_TOOL, type ToolCaller } from "./playtest/recorder.js";
import { secretEnvValues } from "./playtest/redact.js";
import { sessionsRoot, type SessionOwner } from "./playtest/session-store.js";
import { PLAYTEST_SESSION_ACTION_KEYS } from "./tools/playtest-session.js";
import { PLAYTEST_EXPLORE_TOOL } from "./tools/playtest-explore.js";
import { devicesOperation, exploreOperation, parseDevicesOptions, parseExploreOptions, runCheck } from "./playtest/explore-job.js";
import { PLAYTEST_DEVICES_TOOL } from "./tools/playtest-devices.js";
import { ExploreError } from "./playtest/explore.js";
import { PLAYTEST_REPLAY_ACTION_KEYS, PLAYTEST_REPLAY_TOOL } from "./tools/playtest-replay.js";
import { exportAction, listAction, prepareReplay, replayOperation, showAction } from "./playtest/replay-job.js";
import { ScenarioError } from "./playtest/scenario.js";

// M31-optimizations Plan 1 / L14 — opt-in route-logging gate. Resolved once at
// module load into a boolean; the per-dispatch log sites read the boolean
// (not process.env on every call) so the hot path stays a single branch.
// Default-off: silent in production; set UNITY_OPEN_MCP_DEBUG=1 (or pass
// --verbose to the server) to re-enable the [unity-open-mcp] Route: trace.
// Genuine error-path logging is unconditional and never gated by this flag.
//
// Exported (ROUTE_LOG_ENABLED + logRoute) so the test suite can pin both
// halves of the L14 contract: the env-resolution semantics (via
// resolveRouteLogEnabled, the pure helper) and the default-off posture (via
// the captured boolean, which reflects whatever the env was at module load).
export function resolveRouteLogEnabled(env: NodeJS.ProcessEnv = process.env): boolean {
  return env.UNITY_OPEN_MCP_DEBUG === "1" || env.UNITY_OPEN_MCP_VERBOSE === "1";
}
export const ROUTE_LOG_ENABLED = resolveRouteLogEnabled();

/** Log a happy-path route trace iff the opt-in env flag is set. No-op by
 *  default so high-throughput agents do not pay a console.error per dispatch. */
export function logRoute(message: string): void {
  if (ROUTE_LOG_ENABLED) console.error(message);
}
// M31 Plan 3 — Editor fd-exhaustion auto-recovery (restart_editor) + prediction
// (resource_pressure). Both act on the OS process, not the bridge, so they
// share the local route classification with bridge_status.
import {
  killEditorProcess,
  DEFAULT_KILL_GRACE_MS,
  MAX_KILL_WAIT_MS,
  type KillResult,
} from "./editor-process-control.js";
import {
  countFileDescriptors,
  computeFdHeadroom,
  analyzeFdTrend,
  LAUNCH_CONTEXT_CAVEAT,
  type FdCountResult,
} from "./process-diagnostics.js";
import { readFdCeiling } from "./project-settings.js";
import {
  listInstalledEditors,
  fetchAvailableReleases,
  openInstallDeepLink,
  getInstallPath,
  setInstallPath,
  type AvailableReleasesResult,
  type InstallPathResult,
  type OpenDeepLinkResult,
} from "./hub-control.js";

/**
 * Injectable Hub-control backend. Each method maps 1:1 to a hub_* route
 * handler; the default implementation calls the real hub-control functions
 * (filesystem discovery / Unity archive feed / unityhub:// deep link / Hub
 * CLI). Tests inject a fake to avoid real subprocess + network side effects
 * (matching how `live` / `batch` are injected via the constructor).
 */
export interface HubControlBackend {
  listInstalledEditors(): ReturnType<typeof listInstalledEditors>;
  fetchAvailableReleases(): Promise<AvailableReleasesResult>;
  openInstallDeepLink(
    version: string,
    changeset?: string | null,
  ): OpenDeepLinkResult;
  getInstallPath(): InstallPathResult;
  setInstallPath(path: string): ReturnType<typeof setInstallPath>;
}

/** Default Hub-control backend — calls the real hub-control functions. */
const defaultHubBackend: HubControlBackend = {
  listInstalledEditors: () => listInstalledEditors(),
  fetchAvailableReleases: () => fetchAvailableReleases(),
  openInstallDeepLink: (version, changeset) => openInstallDeepLink(version, changeset),
  getInstallPath: () => getInstallPath(),
  setInstallPath: (path) => setInstallPath(path),
};
import { BatchSpawn, BATCH_TOOL_NAMES, ALWAYS_BATCH_TOOLS, activeHeadlessRun, type HeadlessRunInfo } from "./batch-spawn.js";
import { filterVisibleTools, type ToolSessionState } from "./tool-session-state.js";
import {
  readProfileAndDetail,
  applyPaging,
  attachPagination,
  foldVerifyResult,
  parseResultBody,
  withResultBody,
  isOutputProfile,
  type OutputProfile,
} from "./output-profile.js";

export type SourceTag = "live" | "offline" | "local";

export interface RouteMeta {
  /**
   * Where the response came from: "live" (bridge), "batch" (headless spawn),
   * "offline" (disk parser / offline-first attempt, no bridge contact), or
   * "local" (caller-side validation / locally-computed result). The
   * compressible branch derives this from the payload's `_source` tag —
   * including error bodies — so the two tags never disagree about the origin.
   */
  route: "live" | "batch" | "offline" | "local";
  fallbackReason?: string;
  /**
   * feedback-fable-04-08 §7 — present only when a `live_unavailable` batch
   * fallback landed on `editor_instance_locked` AND a live PID holds the
   * project. The actionable truth for the agent is "the editor is busy
   * (TestRunner run / long import); retry later", not a lock conflict, so
   * `fallbackReason` is rewritten to `editor_busy` and the live PID is
   * surfaced here so the agent can correlate.
   */
  editorPid?: number;
}

/**
 * Single source of truth for the `_source` tag. Every route-tagging site goes
 * through one of the two helpers below so live / offline / local stay
 * symmetric — a new route adding `_source` inline (the omission that slipped
 * through before T6.1) is caught by the "no inline `_source` outside the
 * helper" rule and grep.
 *
 * - {@link withSource} stamps `_source` on a body object literal (the local /
 *   offline paths that build the JSON themselves).
 * - {@link tagSource} stamps `_source` on an already-built {@link
 *   CallToolResult} (the live / offline-result paths that wrap a bridge or
 *   disk result).
 */
function withSource<T extends Record<string, unknown>>(
  body: T,
  source: SourceTag,
): T & { _source: SourceTag } {
  return { ...body, _source: source };
}

/**
 * Tag a CallToolResult's JSON body with `_source` so callers switching on the
 * field see a defined value for live-routed drill-downs (find_references /
 * dependencies). A no-op when the first content block is not parseable JSON;
 * preserves `isError` and other content blocks.
 */
function tagSource(
  result: CallToolResult,
  source: SourceTag,
): CallToolResult {
  const body = parseResultBody(result);
  if (body === null) return result;
  return withResultBody(result, withSource(body, source));
}

/**
 * Build a single-block CallToolResult from a body object, tagging it with
 * `_source`. The common shape for local/offline routes that synthesize the
 * entire response in the MCP server (no bridge tool endpoint). Accepts the
 * typed result shapes (ListAssetsResult, CapabilitiesResult, …) returned by
 * the offline/local builders.
 */
function sourceResult(
  body: object,
  source: SourceTag,
  isError = false,
): CallToolResult {
  const tagged = withSource(body as Record<string, unknown>, source);
  return {
    content: [{ type: "text", text: JSON.stringify(tagged) }],
    isError,
  };
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

// ===========================================================================
// M31-optimizations Plan 1 / H2 — single parse+stringify for the live result
// post-processors (fold + _source + _route).
//
// The live find_references / dependencies paths previously chained THREE
// CallToolResult transforms — foldFindReferencesLive → tagSource →
// injectRouteMeta — each doing its own JSON.parse + JSON.stringify on the
// same payload (3 cycles per call). The helpers below collapse that to ONE
// parse and ONE stringify by operating on the parsed body object directly.
// `transformLiveResult` is the single entry point; it parses once, applies a
// caller-supplied body transform (fold / paging / nothing), stamps `_source`
// and `_route`, and stringifies exactly once.
//
// The legacy `tagSource` / `injectRouteMeta` helpers stay for the many call
// sites that only need ONE of the two stamps (so they incur just one
// parse+stringify each — not the triple-cycle chain the live routes had).
// ===========================================================================

/**
 * Apply a body-level transform pipeline to a {@link CallToolResult} in ONE
 * parse + ONE stringify cycle. Used by the live find_references /
 * dependencies / verify routes that previously chained
 * fold→tagSource→injectRouteMeta (three parse+stringify cycles each).
 *
 * Steps:
 *   1. Parse the first text content block ONCE.
 *   2. Hand the parsed body to `transform` (may return null to signal "leave
 *      the body as-is" — the error case; the transform has nothing useful to
 *      do on an error body).
 *   3. Stamp `_source` and `_route` on the (possibly transformed) body — also
 *      when the transform returned null, falling back to the original body.
 *      This preserves the pre-change chain's contract (`tagSource` +
 *      `injectRouteMeta` always stamped a parseable body, error or not), so
 *      error responses still carry `_source` / `_route` for agent routing.
 *   4. Stringify ONCE into the result's first text block; preserve isError
 *      and any other content blocks (e.g. an MCP image block).
 *
 * `transform` defaults to identity. When the body fails to parse, the result
 * is returned unchanged (no stamps — there is no JSON object to stamp).
 */
function transformLiveResult(
  result: CallToolResult,
  source: SourceTag,
  meta: RouteMeta,
  transform?: (
    body: Record<string, unknown>,
  ) => Record<string, unknown> | null,
): CallToolResult {
  const body = parseResultBody(result);
  // parseResultBody returns null when there is no text block OR the block is
  // not a JSON object. In either case there is nothing to stamp — return the
  // result verbatim, matching tagSource/injectRouteMeta's no-op behavior on
  // an unparsable body.
  if (body === null) return result;
  // A null transform signals "leave the body as-is" (e.g. the fold has
  // nothing to do on an error body). Fall through to identity so the stamps
  // still land — matching the pre-change chain, where tagSource/injectRouteMeta
  // stamped any parseable body regardless of an `error` field.
  const transformed = transform ? transform(body) : body;
  const stamped = {
    ...(transformed ?? body),
    _source: source,
    _route: meta,
  };
  return withResultBody(result, stamped);
}

// M26 Plan 2 — local-routed error result (used by the Hub control tools for
// missing-parameter refusals before any side effect runs). Tagged _source=local
// because it is resolved entirely in the MCP server.
function localError(code: string, message: string): CallToolResult {
  return sourceResult({ error: { code, message } }, "local", true);
}

/**
 * Release lookups behind `unity_open_mcp_upgrade`. Injectable so unit tests
 * never touch npm or GitHub; production uses the shared `release-lookup`
 * module with the global fetch.
 */
export interface ReleaseLookup {
  latest(): Promise<{ version: string }>;
  confirmBridgeTag(version: string): Promise<BridgeTagConfirmation>;
}

const defaultReleaseLookup: ReleaseLookup = {
  latest: () => resolveLatestVersion(globalThis.fetch.bind(globalThis)),
  confirmBridgeTag: (version) => confirmBridgeReleaseTag(version, globalThis.fetch.bind(globalThis)),
};
let releaseLookup: ReleaseLookup = defaultReleaseLookup;

/** Test seam for the upgrade route's network lookups. Returns a restore function. */
export function setReleaseLookupForTest(lookup: ReleaseLookup | null): () => void {
  const previous = releaseLookup;
  releaseLookup = lookup ?? defaultReleaseLookup;
  return () => { releaseLookup = previous; };
}

// M26 Plan 2 — gate-consistent envelope shape for the mutating Hub control
// tools (install_editor / install_modules / set_install_path). These are
// system-level ops, not project-asset mutations, so paths_hint is N/A and the
// gate is skipped (gate.skipped=true). The shape matches MutationEnvelope so
// agents parse it uniformly alongside bridge-routed mutating tools.
function hubMutationEnvelope(opts: {
  success: boolean;
  output: unknown;
  error: { code: string; message: string } | null;
  nextSteps: string[];
  isError: boolean;
}): CallToolResult {
  return sourceResult(
    {
      mutation: {
        success: opts.success,
        output: opts.output,
        error: opts.error,
      },
      gate: {
        mode: "off",
        skipped: true,
        validation: null,
        delta: null,
      },
      agentNextSteps: opts.nextSteps,
    },
    "local",
    opts.isError,
  );
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
 * Fold + page the LIVE find_references result body. The bridge returns a flat
 * `referencedBy` path list (no byKind/byFolder groupings, no locations); we
 * derive the compact grouping server-side and page when requested.
 *
 * M31-optimizations Plan 1 / H2 — operates on a parsed body object (not a
 * re-stringified {@link CallToolResult}) so it can be chained with
 * {@link tagSourceBody} / {@link injectRouteMetaBody} in a single
 * parse+stringify cycle via {@link transformLiveResult}.
 *
 * Returns null when the body should NOT be folded (it is not a parseable
 * object, or it carries a top-level `error`), signalling the caller to leave
 * the result untouched.
 */
function foldFindReferencesLiveBody(
  body: Record<string, unknown>,
  profile: OutputProfile | undefined,
  wantPaging: boolean,
  args: Record<string, unknown>,
): Record<string, unknown> | null {
  if (body.error) return null;

  // Bridge shape: { referencedBy: Array<{assetPath, guid}>, totalCount, ... } —
  // each entry is an object (FindReferencesTool emits {assetPath, guid}). Older
  // comments assumed a flat string[]; the live bridge has never emitted that.
  // `extractRefPath` normalizes both the object form and a legacy/defensive
  // bare-string form so a future shape change does not crash the compact
  // grouping (feedback-01-08-glm §3: a non-string element reached
  // `.lastIndexOf` and hard-crashed the tool).
  const rawList = Array.isArray(body.referencedBy) ? body.referencedBy : [];
  const pathList = rawList.map((entry) => extractRefPath(entry));
  const pageSize = typeof args.page_size === "number" && args.page_size > 0 ? (args.page_size as number) : 0;
  const cursor = typeof args.cursor === "string" ? (args.cursor as string) : undefined;

  if (profile === "compact") {
    // compact: counts + byKind grouping only — drop the per-asset list.
    const { referencedBy: _dropped, ...rest } = body;
    void _dropped;
    const byKind = groupLiveRefsByKind(pathList);
    return {
      ...rest,
      totalCount: pathList.length,
      byKind,
      referencedBy: [],
    };
  }

  // balanced / full: keep the path list (paged when requested).
  if (pageSize > 0) {
    const { page, block } = applyPaging(pathList, "find_references", { page_size: pageSize, cursor });
    return attachPagination({ ...body, referencedBy: page, totalCount: pathList.length }, block);
  }

  return { ...body, referencedBy: pathList, totalCount: pathList.length };
}

/** Coerce a `referencedBy` entry to its asset path. The live bridge emits
 *  `{assetPath, guid}` objects; accept a bare string defensively so a shape
 *  change never reaches the string-only `.lastIndexOf` in the grouping. */
function extractRefPath(entry: unknown): string {
  if (typeof entry === "string") return entry;
  if (entry && typeof entry === "object") {
    const obj = entry as Record<string, unknown>;
    if (typeof obj.assetPath === "string") return obj.assetPath;
    if (typeof obj.path === "string") return obj.path;
  }
  return "";
}

/** Derive a byKind grouping from flat referenced paths for the live compact view. */
function groupLiveRefsByKind(paths: string[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const path of paths) {
    if (!path) {
      counts.other = (counts.other ?? 0) + 1;
      continue;
    }
    const dot = path.lastIndexOf(".");
    const kind = dot > 0 ? path.slice(dot + 1).toLowerCase() : "other";
    counts[kind] = (counts[kind] ?? 0) + 1;
  }
  return counts;
}

// specs/feedback.md 2026-08-14 — the coarse bridge_status token set. `wedged`
// is the addition: the Editor process is alive and its listener answers, but
// the Editor cannot do the work an agent is about to ask of it. Two distinct
// reasons share the token (the `wedged.reason` field distinguishes them):
//
//   editor_fd_exhaustion — the build driver is dead; edits never compile and
//     execute_csharp silently runs the previous assembly. Only a restart
//     recovers. Previously reported as `running` / `healthy` in the same
//     minute read_compile_errors reported `unhealthy: true`.
//   main_thread_wedged   — a modal dialog (or another main-thread stall) is
//     blocking Unity's message pump: the heartbeat (written from
//     EditorApplication.update) has gone stale while /ping (served off the
//     listener thread) still answers. Previously collapsed into `dead_bridge`,
//     sending agents to hunt a compile failure that does not exist. Only an
//     operator can dismiss a modal — no tool can.
type BridgeStatusToken =
  | "running"
  | "compiling"
  | "stopped"
  | "dead_bridge"
  | "unreachable"
  | "wedged";

type WedgeReason = "editor_fd_exhaustion" | "main_thread_wedged";

interface BridgeWedge {
  reason: WedgeReason;
  /** One-line operator-facing explanation of what is stuck. */
  detail: string;
  /** Evidence trail: the log the fd signature was read from, when applicable. */
  logPath?: string;
  /** The matched log line, when applicable. */
  raw?: string;
}

// Operator-facing "what to do next" hint for each coarse status. Kept short
// and action-oriented so an operator UI can render it inline.
function bridgeStatusNextStep(
  status: BridgeStatusToken,
  wedge?: BridgeWedge | null,
): string {
  if (status === "wedged") {
    return wedge?.reason === "main_thread_wedged"
      ? "Unity's main thread is wedged — the HTTP listener still answers /ping, but the " +
          "editor heartbeat has stopped, which is the signature of a MODAL DIALOG blocking " +
          "the message pump (material/scene converters and several ExecuteMenuItem paths " +
          "open one). This is NOT a compile failure and NOT a dead bridge assembly — a dead " +
          "assembly has no listener at all. It cannot self-heal and no tool can dismiss the " +
          "dialog: an operator must dismiss it in the Unity UI, after which queued calls " +
          "resume. Prefer non-interactive APIs over unity_open_mcp_execute_menu for " +
          "converter-style menus."
      : "The Unity Editor hit the Bee build driver's file-descriptor exhaustion — the " +
          "process is alive and the listener answers, but it can no longer BUILD: " +
          "Library/ScriptAssemblies stops updating, so C# edits never take effect and " +
          "unity_open_mcp_execute_csharp silently runs the previous assembly. Do not trust " +
          "any result computed since. Call unity_open_mcp_read_compile_errors to confirm " +
          "the editor_fd_exhaustion issue, save scene work, then restart the Editor " +
          "(unity_open_mcp_restart_editor with confirm: true, or manually).";
  }
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
      return "Bridge listener is not responding but Unity is running — a domain reload in progress (the bridge tears down its HTTP socket for it; a large project's or a play-mode reload can stop the heartbeat for longer than 10 s), Unity still starting, or the bridge toggle off. Editor.log shows no compile failure. Wait with unity_open_mcp_editor_status {\"wait_until\":\"idle\"}; if it never settles, call unity_open_mcp_read_compile_errors.";
    case "dead_bridge":
      // M23 Plan 2 — safe-mode surfacing (feedback 2026-06-29). A dead_bridge
      // signature (live PID + stale heartbeat) almost always means Unity is
      // sitting in Safe Mode / showing compile errors; the generic "open the
      // bridge window" hint was misleading. Name safe mode explicitly and
      // point at the one channel that works in that state.
      return "Editor.log shows the bridge cannot come back on its own (see `evidence`): " +
        "Unity is in Safe Mode, or the last domain reload finished without the bridge " +
        "assembly loading. Call unity_open_mcp_read_compile_errors to retrieve the compiler " +
        "errors from Editor.log, fix the cited file/line, then trigger a recompile " +
        "(e.g. a no-op edit + focus Unity, or unity_open_mcp_compile_check once the " +
        "source compiles). If Unity is NOT in safe mode, the bridge toolbar toggle " +
        "may be off — open the bridge window (Unity menu: Tools/Unity Open MCP " +
        "Bridge) and confirm it is started.";
  }
}

// This server's own headless batch run holds the project. The Unity process
// scan deliberately skips that child (it is not an Editor the user owns), so
// Editor-ownership surfaces would otherwise report "no Unity running" and send
// the agent to relaunch via the Hub, which would collide with the run.
function describeHeadlessRun(run: HeadlessRunInfo, now = Date.now()): Record<string, unknown> {
  return {
    tool: run.tool,
    operation: run.operation,
    pid: run.pid,
    startedAt: new Date(run.startedAt).toISOString(),
    elapsedMs: Math.max(0, now - run.startedAt),
    timeoutMs: run.timeoutMs,
  };
}

function headlessRunSentence(run: HeadlessRunInfo, now = Date.now()): string {
  const elapsedS = Math.round(Math.max(0, now - run.startedAt) / 1000);
  const timeoutS = Math.round(run.timeoutMs / 1000);
  return (
    `The only Unity process for this project is this MCP server's own headless ` +
    `run for ${run.tool}` +
    (run.pid !== null ? ` (pid ${run.pid})` : "") +
    `, started ${elapsedS}s ago with a ${timeoutS}s timeout. It is not an ` +
    `interactive Editor, and it exits on its own when the operation finishes ` +
    `or the timeout elapses.`
  );
}

const HEADLESS_RUN_NEXT_STEPS: readonly string[] = [
  "Wait for the pending headless call to return; it releases the project " +
    "when it finishes or when its timeout elapses.",
  "Do not relaunch Unity for this project via the Hub meanwhile: Unity " +
    "allows one Editor per project, so a second instance collides with the " +
    "headless run's project lock.",
];

function headlessRunErrorResult(
  action: "restart_editor" | "resource_pressure",
  run: HeadlessRunInfo,
  projectPath: string,
): CallToolResult {
  const now = Date.now();
  const tail =
    action === "restart_editor"
      ? " restart_editor never targets the server's own headless run, so there is no Editor to restart."
      : " resource_pressure watches the interactive Editor's descriptor leak, which a short-lived headless run does not accumulate.";
  return sourceResult(
    {
      error: {
        code: "headless_run_in_progress",
        message: headlessRunSentence(run, now) + tail,
        projectPath,
        ...describeHeadlessRun(run, now),
      },
      agentNextSteps: [
        ...HEADLESS_RUN_NEXT_STEPS,
        action === "restart_editor"
          ? "Call restart_editor again only if an interactive Editor for this project hangs after the run completes."
          : "Once the run completes and an interactive Editor is open, call resource_pressure again; pass an explicit pid to sample the headless process anyway.",
      ],
    },
    "local",
    true,
  );
}

// M23 Plan 2 — structured recovery hint surfaced alongside `status`. Lets
// agents (and operator UIs) branch on a machine-readable signal
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

/**
 * specs/feedback.md 2026-08-24 — wire-contract staleness block.
 *
 * The field report: a blocker fixed and shipped on 2026-08-17 (registry tools
 * rejecting the transport envelope keys, which made `editor_status` uncallable
 * with default args) recurred against an installed bridge that still reported
 * `bridgeVersion "1.0.0"` — the same string the FIXED source reports, because
 * the package semver does not move for a wire-contract fix. "Your install is
 * stale" and "this regressed" were therefore indistinguishable at the call
 * site, and the agent had no way to pick between reinstalling and filing a bug.
 *
 * `BridgeSession.WireContract` is bumped for every observable change to the
 * request/response contract, so comparing it against
 * {@link EXPECTED_BRIDGE_WIRE_CONTRACT} answers that question directly. A
 * bridge that predates the field reports no `wireContract` at all — `bridge`
 * is then null and `stale` is true, which is the correct reading (it is older
 * than revision 1 by definition).
 *
 * Returns null when /ping was unreachable: there is no bridge to compare, and a
 * bogus "stale" on an offline bridge would be worse than silence.
 */
function wireContractBlock(
  reachable: boolean,
  reported: number | undefined,
): {
  bridge: number | null;
  expected: number;
  stale: boolean;
  note: string | null;
} | null {
  if (!reachable) return null;
  const bridge = typeof reported === "number" ? reported : null;
  const stale = bridge === null || bridge < EXPECTED_BRIDGE_WIRE_CONTRACT;
  return {
    bridge,
    expected: EXPECTED_BRIDGE_WIRE_CONTRACT,
    stale,
    note: stale
      ? "The installed Unity bridge package is OLDER than this MCP server's wire " +
        "contract" +
        (bridge === null
          ? " (it does not report `wireContract` at all, so it predates revision 1)"
          : ` (revision ${bridge} vs ${EXPECTED_BRIDGE_WIRE_CONTRACT})`) +
        ". A tool failing in a way the docs say is fixed is almost certainly " +
        "this staleness, not a regression: reinstall/update the bridge package " +
        "in Unity (Package Manager > the unity-open-mcp-bridge git URL) and " +
        "retry before filing anything. bridgeVersion alone cannot tell you " +
        "this — it does not move for a wire-contract fix."
      : null,
  };
}

function bridgeStatusRecoveryHint(
  status: BridgeStatusToken,
  wedge?: BridgeWedge | null,
): BridgeRecoveryHint | null {
  if (status === "wedged") {
    return wedge?.reason === "main_thread_wedged"
      ? {
          tool: "unity_open_mcp_editor_status",
          reason:
            "Main thread wedged (likely a modal dialog): /ping answers but the " +
            "editor heartbeat stopped. No tool can dismiss a modal — an operator " +
            "must close it in the Unity UI, then editor_status confirms recovery.",
        }
      : {
          tool: "unity_open_mcp_read_compile_errors",
          reason:
            "editor_fd_exhaustion in the freshest Editor.log — the Editor answers " +
            "HTTP but can no longer build, so C# edits never take effect. Confirm " +
            "with read_compile_errors, then restart the Editor.",
        };
  }
  if (status === "dead_bridge") {
    return {
      tool: "unity_open_mcp_read_compile_errors",
      reason:
        "Editor.log shows Safe Mode or a reload that finished without the bridge " +
        "— read_compile_errors reads Editor.log offline (the only channel that " +
        "works with the bridge assembly dead).",
    };
  }
  return null;
}

/**
 * Whether the instance lock says the Editor is not ready for a call: a
 * compile, domain reload or play-mode change in progress, or a compile a call
 * requested that has not started yet (within its grace window), also when its
 * heartbeat stopped mid-transition without Editor.log evidence of a dead
 * bridge. A missing lock, a dead process or a bridge that cannot come back is
 * not a transition — the normal route reports those.
 */
function lockShowsTransition(lock: InstanceLock | null, now = Date.now(), bridgeLoss: () => BridgeLossEvidence | null = () => null): boolean {
  if (!lock) return false;
  const classification = classifyInstance(lock, now);
  if (classification === "gone") return false;
  // A heartbeat that stopped inside a transition is a reload that runs long (a
  // large project, a play-mode reload, a test run's reload) unless Editor.log
  // shows the bridge cannot come back: wait for it like any reload.
  if (classification === "dead_bridge")
    return (isTransitionState(lock.state) || hasRecentPendingTestRun()) && bridgeLoss() === null;
  if (isTransitionState(lock.state)) return true;
  if (lock.compile?.pending !== true) return false;
  const requestedAt = Date.parse(lock.compile.requestedAt ?? "");
  return !Number.isFinite(requestedAt) || now - requestedAt < PENDING_COMPILE_GRACE_MS;
}

/** A refusal the bridge gave without running the call (editor_reloading, dispatched:false): safe to send again. */
function resendAfterReload(result: CallToolResult): boolean {
  if (!result.isError) return false;
  const body = toolResultBody(result);
  return body.error?.code === "editor_reloading" && body.dispatched === false;
}

/** /compile-state (or read_compile_errors' live summary) as a wait reading; null when it is not one. */
function compileReading(body: Record<string, unknown> | null): CompileReading | null {
  if (!body || typeof body.generation !== "number" || typeof body.status !== "string") return null;
  return {
    status: body.status,
    generation: body.generation,
    errorCount: typeof body.errorCount === "number" ? body.errorCount : Array.isArray(body.errors) ? body.errors.length : 0,
    ...(typeof body.sourceMatches === "boolean" ? { sourceMatches: body.sourceMatches } : {}),
    ...(typeof body.pending === "boolean" ? { pending: body.pending } : {}),
    ...(typeof body.errorSource === "string" ? { errorSource: body.errorSource } : {}),
    ...(typeof body.headline === "string" ? { headline: body.headline } : {}),
    ...(Array.isArray(body.errors) && body.errors.length > 0
      ? { errors: (body.errors as unknown[]).filter((e): e is Record<string, unknown> => typeof e === "object" && e !== null).slice(0, COMPILE_RESULT_FIRST_ERRORS) }
      : {}),
  };
}

/**
 * Editor phases in which a live Editor holds the project and its instance lock
 * is fresh: read_compile_errors answers from the lock's compile block then, not
 * from an old log. `gone` and `dead_bridge` are the genuinely unreachable ones.
 */
const LIVE_EDITOR_PHASES: ReadonlySet<string> = new Set(["settled", "transition", "wedged", "unreachable"]);

const HISTORICAL_REASON_TEXT: Record<HistoricalReason, string> = {
  superseded_by_later_build: "superseded by a later successful build",
  log_predates_assemblies: "the log predates the newest built assembly",
  source_missing: "the cited file no longer exists",
  source_changed: "the cited line is past the end of the current file",
};

/** " (its N error(s) are historical: …)" for a headline, or "" when none are. */
function historicalNote(classified: ClassifiedLogErrors): string {
  if (classified.historical.length === 0) return "";
  const counts = new Map<HistoricalReason, number>();
  for (const e of classified.historical) counts.set(e.historicalReason, (counts.get(e.historicalReason) ?? 0) + 1);
  const reasons = [...counts].map(([reason, n]) => `${n} ${HISTORICAL_REASON_TEXT[reason]}`).join("; ");
  return ` (its ${classified.historical.length} error(s) are historical: ${reasons})`;
}

/** How to get a determinate compile answer after an indeterminate offline read. */
function offlineCompileNextSteps(view: EditorView | null): string[] {
  if (view?.phase === "dead_bridge") {
    return [
      "Unity is running but its bridge did not come back (Safe Mode, or a compile failure in the bridge or a " +
        "package it depends on); raise tail_bytes if the errors scrolled out of the tail, check " +
        `unity_open_mcp_bridge_status, and once the bridge is back ${waitCall("idle")} reports the live compile state.`,
    ];
  }
  if (view && view.phase !== "gone") {
    return [`Wait for the live Editor with ${waitCall("idle")} — it reports the compile generation, status and error count — then re-read.`];
  }
  return [
    "No live Editor holds the project. Open it in Unity (its live compile state is authoritative), or with the " +
      `Editor closed run ${toolHintReference("unity_open_mcp_compile_check")} for a definitive headless compile.`,
  ];
}

const sleepMs = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms));

import { readReloadWaitMs } from "./retry-policy.js";
export { readReloadWaitMs, DEFAULT_RELOAD_WAIT_MS } from "./retry-policy.js";

/** Named-tool route: the call's live client, its arguments, and its request identity. */
type RouteHandler = (live: LiveClient, args: Record<string, unknown>, identity: RequestIdentity) => Promise<CallToolResult>;

export class ToolRouter implements Router {
  private jobManager?: JobManager;
  get jobs(): JobManager { return this.jobManager ??= new JobManager(); }
  closeJobs(): void { this.jobManager?.close(); }

  /** The jobs owner of a request: this project, the request's agent and its port override. */
  private jobOwner(identity: RequestIdentity): JobOwner {
    return { project: this.projectPath || "default", agent: identity.agent,
      ...(identity.port === undefined ? {} : { port: identity.port }) };
  }

  /**
   * The live client a job of `jobOwner` talks to. A port override names a
   * bridge, not a project: bind the job client to the Editor whose lock claims
   * that port (its bearer token and its lock diagnostics), exactly like a
   * direct override call. resolveAuthToken yields no token for an explicit
   * port, and the default project's lock would describe the wrong Editor.
   */
  private readonly jobClient = (jobOwner: JobOwner): LiveClient => jobOwner.port === undefined
    ? jobOwner.agent === PROCESS_AGENT_ID ? this.live : this.live.forAgent(jobOwner.agent)
    : LiveClient.forPortOverride(jobOwner.port, this.projectPath, jobOwner.agent);

  /** One generic jobs adapter for every tool on the bridge's shared play-mode scheduler. */
  private ensurePlaymodeJobs(name: string): void {
    if (!this.jobs.hasOperation(name)) this.jobs.register(name, playmodeOperation(name, PLAYMODE_OPERATION_TOOLS.get(name)!, this.jobClient));
  }

  async routeJobs(args: Record<string, unknown>, identity: RequestIdentity = PROCESS_IDENTITY): Promise<CallToolResult> {
    const definition = ALL_TOOLS.find(t => t.name === "unity_open_mcp_jobs")!;
    const errors = validateSchema(args, definition.inputSchema);
    if (errors.length) return localError("invalid_arguments", errors.join("; "));
    const owner = this.jobOwner(identity);
    try {
      const clientFor = this.jobClient;
      if (args.action === "start" && (args.tool_or_command === PLAYTEST_EXPLORE_TOOL || args.tool_or_command === PLAYTEST_DEVICES_TOOL || args.tool_or_command === PLAYTEST_REPLAY_TOOL))
        return localError("job_operation_unsupported", `Start it with ${String(args.tool_or_command)} itself: it checks the open session (and, for a crawl or a replay, isolation and play mode), then returns the job id.`);
      if (args.action === "start" && typeof args.tool_or_command === "string") {
        const name = args.tool_or_command, key = args.idempotency_key;
        if (name === "unity_senses_run_tests") {
          if (!this.jobs.hasOperation(name)) this.jobs.register(name, testRunOperation(clientFor));
        } else if (name === WAIT_TOOL) {
          // An observer: it holds no project slot, so a wait never queues behind (or blocks) a test run.
          if (!this.jobs.hasOperation(name)) this.jobs.register(name, editorWaitOperation(jobOwner => {
            const client = clientFor(jobOwner);
            const projectPath = jobOwner.port === undefined ? this.projectPath : findInstanceLockByPort(jobOwner.port)?.projectPath;
            return this.waitDeps(client, projectPath);
          }));
        } else if (PLAYMODE_OPERATION_TOOLS.has(name)) {
          this.ensurePlaymodeJobs(name);
        } else if (name.startsWith("project.") && !(typeof key === "string" && this.jobs.hasKey(owner, key))) {
          // A retry whose key already names a job gets that job (or
          // idempotency_conflict) from start() without the catalog, which may
          // be unreachable mid-reload or declare a contract changed since.
          // Re-read the catalog on every new start: a domain reload can change the
          // command's async/cancellable declaration, and an adapter frozen at
          // first registration would refuse the job for the rest of the session.
          const catalog = await clientFor(owner).projectCommands({ action: "describe", id: name });
          if (catalog.error) {
            // A transport/catalog failure (bridge down, reloading, too old) is
            // not "this command is unsupported": surface the catalog's own
            // code so the agent retries instead of abandoning the plan.
            const failure = catalog.error as { code?: string; message?: string };
            throw new JobManagerError(failure.code ?? "catalog_unavailable", failure.message ?? "Project command catalog is unavailable.");
          }
          const command = catalog.command as Record<string, unknown> | undefined;
          if (!command?.available || !command.async) throw new JobManagerError("job_operation_unsupported", "Target must be an available async project command.");
          const cancellable = command.cancellable === true;
          if (this.jobs.operation(name)?.cancellable !== cancellable)
            this.jobs.register(name, projectCommandOperation(name, cancellable, clientFor), true);
        }
      }
      return sourceResult(await handleJobs(this.jobs, owner, args) as Record<string, unknown>, "local");
    }
    catch (error) {
      return localError(error instanceof JobManagerError ? error.code : "invalid_arguments", error instanceof Error ? error.message : String(error));
    }
  }

  /**
   * Play-mode operations (`inputsim_gesture`, `playtest_wait`,
   * `capture_series`): the bridge starts the operation on its shared play-mode
   * scheduler and answers at once; this route polls POST /playmode-operations
   * until the terminal result. An operation whose worst case
   * (`playmodeEstimateMs`: last keyframe / capture, the wait's timeout, a
   * series' start_on wait + schedule + encoding) exceeds the inline window is
   * started as a `jobs` play-mode operation instead and the call returns that
   * job id. One that turns out slower than its schedule (gameplay time at a
   * low Time.timeScale, a slow frame rate) is never cancelled: as soon as its
   * observed rate says it will not end inside the inline limit — or at that
   * limit — it continues as a job under its own operation id, which observes
   * the running operation (nothing restarts), and the call answers
   * `status: "running"` with the job id, the progress so far and an estimate.
   * Only the bridge ends an operation that stopped moving (stalled).
   */
  private async routePlaymodeOperation(name: string, args: Record<string, unknown>, live: LiveClient, identity: RequestIdentity): Promise<CallToolResult> {
    const spec = PLAYMODE_ROUTES[name];
    const startedAt = Date.now();
    const estimateMs = playmodeEstimateMs(name, args);
    // The CLI (no host timeout; a job would end with its process) waits inline throughout.
    const windowMs = Math.max(spec.inlineLimitMs, this.waitInlineLimitMs);
    if (estimateMs > windowMs) {
      // The job owns the bridge operation id; a caller's operation_id keeps
      // its retry meaning as the job's idempotency key.
      const { operation_id: retryKey, ...jobArgs } = args;
      const started = await this.routeJobs({ action: "start", tool_or_command: name, args: jobArgs,
        ...(typeof retryKey === "string" ? { idempotency_key: retryKey } : {}) }, identity);
      const body = toolResultBody(started);
      if (started.isError || typeof body.job_id !== "string") return started;
      return sourceResult({ ...body, note: `${spec.label} can take ${Math.round(estimateMs)} ms (> ${windowMs} ms inline budget); it runs as a job.`,
        agentNextSteps: [`Call unity_open_mcp_jobs {"action":"wait","job_id":"${body.job_id}","timeout_ms":30000} until the state is terminal; the result holds ${spec.resultHint}.`] }, "local");
    }
    const operationId = typeof args.operation_id === "string" ? args.operation_id : randomUUID();
    const start = await live.route(name, { ...args, operation_id: operationId });
    const started = toolOutput(start);
    if (start.isError || started.error || !started.operation) return injectRouteMeta(start, { route: "live" });
    // Inline up to the host-safe limit; past it (or once the rate says so) the job takes over.
    const limitMs = Number.isFinite(windowMs) ? Math.min(windowMs + PLAYMODE_INLINE_GRACE_MS, BRIDGE_HOST_SAFE_TIMEOUT_CAP_MS - 5_000) : Infinity;
    const progress = new PlaymodeProgress(playmodeTailMs(name, args));
    const outcome = await pollPlaymodeOperation(live, operationId, {
      deadlineMs: Infinity,
      pollIntervalMs: estimateMs < 2_000 ? 50 : 100,
      unreachableMs: 10_000,
      detach: snapshot => playmodeShouldHandOver(progress.observe(snapshot), Date.now() - startedAt, limitMs),
    });
    const meta = spec.startMeta(started);
    switch (outcome.kind) {
      case "detached":
        return await this.continuePlaymodeAsJob(name, args, operationId, outcome.snapshot, progress.last, identity);
      case "lost":
        return sourceResult({ error: { code: "operation_lost", message: `The bridge lost the ${spec.noun} (domain reload or Editor restart); its cleanup ${spec.cleanup}.` }, operationId }, "live", true);
      case "unreachable":
        return sourceResult({ error: { code: "job_status_unavailable", message: outcome.error }, operationId }, "live", true);
      case "deadline":
        throw new Error("unreachable play-mode poll outcome: the inline poll has no deadline");
    }
    const snapshot = outcome.snapshot;
    const result = { ...meta, ...playmodeResult(snapshot) };
    if (snapshot.state === "succeeded") return sourceResult({ status: "ok", ...result }, "live");
    const error = playmodeFailure(snapshot, spec.failedCode, spec.noun);
    const steps = spec.failureNextSteps(error.code, error.reason);
    // The operation's own `error` (a message string) becomes error.message.
    return sourceResult({ ...result, error, ...(steps ? { agentNextSteps: steps } : {}) }, "live", true);
  }

  /**
   * Hand a running play-mode operation to a job: the job takes the operation id
   * as its job id and observes the operation (it holds no execution slot and
   * starts nothing). The answer carries the progress so far — for a series the
   * frames captured — and when to expect the result.
   */
  private async continuePlaymodeAsJob(name: string, args: Record<string, unknown>, operationId: string, snapshot: Record<string, any>,
    estimate: PlaymodeEstimate | null, identity: RequestIdentity): Promise<CallToolResult> {
    const spec = PLAYMODE_ROUTES[name];
    const { operation_id: _ownedByTheJob, ...jobArgs } = args;
    let job: Record<string, unknown>;
    try {
      this.ensurePlaymodeJobs(name);
      const owner = this.jobOwner(identity);
      const started = this.jobs.start(owner, name, jobArgs, undefined, { continueId: operationId });
      // A continuation holds no slot: it is running by the next turn of the loop.
      await new Promise<void>(resolve => setImmediate(resolve));
      job = this.jobs.status(owner, started.job_id) as unknown as Record<string, unknown>;
    } catch (error) {
      // No job to continue in: the operation keeps running on the bridge; say how to observe it.
      return sourceResult({ error: { code: error instanceof JobManagerError ? error.code : "job_start_failed",
        message: `The ${spec.noun} is still running but could not continue as a job: ${error instanceof Error ? error.message : String(error)}` },
        operationId, operation: snapshot }, "local", true);
    }
    const series = snapshot.progress?.series as Record<string, unknown> | undefined;
    const eta = estimate?.etaMs ?? null;
    const still = estimate !== null && estimate.stalledMs >= PLAYMODE_STILL_MS;
    const pace = still ? ` but has not moved for ${Math.round(estimate!.stalledMs / 1000)} s (the bridge ends it as stalled after 15 s without progress)`
      : estimate?.rate != null && estimate.clock === "gameplay" ? ` at ${estimate.rate}× real time` : "";
    const done = estimate ? `${estimate.clock ? `${estimate.clock} ` : ""}${Math.round(estimate.elapsedMs)} of ${Math.round(estimate.expectedMs)} ms` : "";
    const captured = series ? `${done ? ", " : ""}${series.captured} of ${series.requested} frames captured` : "";
    return sourceResult({
      status: "running",
      job_id: job.job_id,
      operationId,
      job: { state: job.state, operation: job.operation },
      progress: {
        phase: snapshot.phase,
        ...(estimate ? { clock: estimate.clock, elapsed_ms: estimate.elapsedMs, expected_ms: estimate.expectedMs, rate: estimate.rate, stalled_ms: estimate.stalledMs } : {}),
        eta_ms: eta,
      },
      ...(series ? { series } : {}),
      operation: snapshot,
      note: `The ${spec.noun} is ${still ? "running" : "progressing"}${pace} (${done}${captured}); it continues as job ${job.job_id}` +
        (eta !== null && !still ? ` and should end in about ${Math.max(1, Math.round(eta / 1000))} s.` : "."),
      agentNextSteps: [
        `Call unity_open_mcp_jobs {"action":"wait","job_id":"${job.job_id}","timeout_ms":30000} until the state is terminal; the result holds ${spec.resultHint}. ` +
        `Do not start the ${spec.noun} again: it is running, and the job is its result.`,
      ],
    }, "local");
  }

  /**
   * `game_view`: set_resolution / set_device in play mode return once the game
   * reports the new size — the bridge starts a settle watch on its play-mode
   * scheduler and this route polls it (then re-reads `get`), or answers
   * `not_settled`. get / list_devices and edit-mode changes pass through.
   */
  private async routeGameView(args: Record<string, unknown>, live: LiveClient): Promise<CallToolResult> {
    const outcome = await settleGameView(live, args);
    if (outcome.kind === "passthrough") return injectRouteMeta(outcome.result, { route: "live" });
    return sourceResult(outcome.body, "live", outcome.isError);
  }

  private readonly modelCache = new AssetModelCache();

  /**
   * M28-refactoring Plan 3 (T3.2) — named-tool dispatch map. Replaces the
   * former ~70-block `if (toolName === "...")` chain in routeCore. Each entry
   * binds the tool name to its route-handler method. The map is built lazily on
   * first use (the handlers are bound instance methods, so it cannot be a
   * static field). Adding a new named-tool route is now a map entry, not a new
   * `if` block. Tools that share a handler (validate_edit + scan_paths) or are
   * reached via a predicate (isCompressible) are handled below the lookup — the
   * map covers only exact-name dispatch.
   */
  private routeHandlers?: Map<string, RouteHandler>;

  private getRouteHandlers(): Map<string, RouteHandler> {
    if (this.routeHandlers) return this.routeHandlers;
    const handlers: Map<string, RouteHandler> = new Map([
      // Disk reads and live compiler evidence with an offline log fallback.
      ["unity_open_mcp_list_assets", (_l, a) => this.routeListAssets(a)],
      ["unity_open_mcp_read_compile_errors", (l, a) => this.routeReadCompileErrors(a, l)],
      // Local-routed tools (server-only; no live/batch hop).
      ["unity_senses_pull_events", (l, a) => this.routePullEvents(a, l)],
      ["unity_open_mcp_capabilities", (l, a) => this.routeCapabilities(a, l)],
      ["unity_open_mcp_list_rules", (_l, a) => this.routeListRules(a)],
      ["unity_open_mcp_generate_skill", (_l, a) => this.routeGenerateSkill(a)],
      ["unity_open_mcp_manage_tools", (l, a, id) => this.routeManageTools(a, l, id)],
      ["unity_open_mcp_project_commands", (l, a) => this.routeProjectCommands(a, l)],
      // Playtest session recorder: the log lives here (the server sees every
      // call); bridge reads go through the normal route as the same owner.
      [PLAYTEST_SESSION_TOOL, (l, a, id) => this.routePlaytestSession(a, l, id)],
      // UI crawler: a server-side job driving the playtest tools through this
      // same dispatch as the session owner, recording into the open session.
      [PLAYTEST_EXPLORE_TOOL, (l, a, id) => this.routePlaytestExplore(a, l, id)],
      // Device matrix: the same kind of job, switching Device Simulator devices.
      [PLAYTEST_DEVICES_TOOL, (l, a, id) => this.routePlaytestDevices(a, l, id)],
      // Scenario replay: export / list / show are file actions; run is the same kind of job.
      [PLAYTEST_REPLAY_TOOL, (l, a, id) => this.routePlaytestReplay(a, l, id)],
      // Live tool whose release lookups (npm latest, GitHub tag) run HERE so
      // the Editor main thread never blocks on the network.
      ["unity_open_mcp_upgrade", (l, a) => this.routeUpgrade(a, l)],
      // The owner is the request identity (agent id + per-request port
      // override), never the live client's: the default client always sends
      // the process agent id, whatever _meta.agentId the call carried. Direct,
      // manage_tools(invoke) and port-override calls therefore share one owner.
      ["unity_open_mcp_jobs", (_l, a, id) => this.routeJobs(a, id)],
      // Gestures, waits and capture series run on the bridge's play-mode
      // scheduler: start, then poll the operation to its terminal result here
      // (long ones become a job).
      ["unity_open_mcp_inputsim_gesture", (l, a, id) => this.routePlaymodeOperation("unity_open_mcp_inputsim_gesture", a, l, id)],
      ["unity_open_mcp_playtest_wait", (l, a, id) => this.routePlaymodeOperation("unity_open_mcp_playtest_wait", a, l, id)],
      ["unity_senses_capture_series", (l, a, id) => this.routePlaymodeOperation("unity_senses_capture_series", a, l, id)],
      // Game view / Simulator changes settle over play-mode frames: poll the
      // bridge's settle watch here so set_* returns once the game sees the size.
      ["unity_open_mcp_game_view", (l, a) => this.routeGameView(a, l)],
      ["unity_open_mcp_bridge_status", (l, a) => this.routeBridgeStatus(a, l)],
      // The status tool always answers (from the lock + /ping during a
      // reload) and is the one wait primitive (wait_until).
      [WAIT_TOOL, (l, a, id) => this.routeEditorStatus(a, l, id)],
      // Compile-type calls: the compile they started is the answer, so the
      // route waits for it (inline, then as a job) and reports its result.
      ["unity_open_mcp_recompile_scripts", (l, a, id) => this.routeCompileTool("unity_open_mcp_recompile_scripts", a, l, id)],
      ["unity_open_mcp_assets_refresh", (l, a, id) => this.routeCompileTool("unity_open_mcp_assets_refresh", a, l, id)],
      // M31 Plan 3 — Editor fd-exhaustion operator surfaces. restart_editor
      // is the reactive kill half (acts AFTER the Editor is hung);
      // resource_pressure is the proactive prediction half (samples fd usage
      // BEFORE exhaustion). Both are local-routed: they act on the OS process,
      // not the bridge, like bridge_status.
      ["unity_open_mcp_restart_editor", (l, a) => this.routeRestartEditor(a, l)],
      ["unity_open_mcp_resource_pressure", (_l, a) => this.routeResourcePressure(a)],
      // Hub control tools (local-routed; system-level ops).
      ["unity_open_mcp_hub_list_editors", () => this.routeHubListEditors()],
      ["unity_open_mcp_hub_available_releases", () => this.routeHubAvailableReleases()],
      ["unity_open_mcp_hub_install_editor", (_l, a) => this.routeHubInstallEditor(a)],
      ["unity_open_mcp_hub_install_modules", (_l, a) => this.routeHubInstallModules(a)],
      ["unity_open_mcp_hub_get_install_path", () => this.routeHubGetInstallPath()],
      ["unity_open_mcp_hub_set_install_path", (_l, a) => this.routeHubSetInstallPath(a)],
      // Offline-first reference tools (fall back to live when the bridge is up).
      ["unity_open_mcp_find_references", (l, a) => this.routeFindReferences(a, l)],
      ["unity_open_mcp_dependencies", (l, a) => this.routeDependencies(a, l)],
      // Verify tools — share a single handler (toolName carried through).
      ["unity_open_mcp_validate_edit", (l, a) => this.routeVerifyResult("unity_open_mcp_validate_edit", a, l)],
      ["unity_open_mcp_scan_paths", (l, a) => this.routeVerifyResult("unity_open_mcp_scan_paths", a, l)],
      // Scene data — output-profile + paging applied server-side.
      ["unity_open_mcp_scene_get_data", (l, a) => this.routeSceneGetData(a, l)],
    ]);
    this.routeHandlers = handlers;
    return handlers;
  }

  constructor(
    private live: LiveClient,
    private batch: BatchSpawn,
    private projectPath: string,
    private eventStream: BridgeEventStream,
    private sessionState: ToolSessionState,
    private onToolListChanged?: () => void | Promise<void>,
    // M26 Plan 2 — injectable Hub-control backend. Omit in production (the
    // default calls the real hub-control functions); tests pass a fake to
    // avoid real subprocess + network side effects.
    private hubBackend: HubControlBackend = defaultHubBackend,
    // feedback-fable-04-08 §2b — injectable global Editor.log path for tests.
    // Production leaves this undefined so resolveEditorLogPath computes the
    // real platform global path; tests pass a path under their tmp sandbox so
    // the freshness comparison does not consult the host machine's real
    // ~/Library/Logs/Unity/Editor.log (which is newer than the artificial
    // 1970-era mtimes the tests plant and would win the comparison, reading
    // the wrong file).
    private globalLogPathOverride?: string,
  ) {}

  async route(
    toolName: string,
    args: Record<string, unknown>,
    identity: RequestIdentity = PROCESS_IDENTITY,
  ): Promise<CallToolResult> {
    return this.routeCore(this.live, toolName, args, identity);
  }

  /**
   * `capabilities` / `bridge_status` with an explicit project_path, from any
   * entry point (direct, manage_tools invoke, port override). The probe router
   * gets the arguments without project_path, so it dispatches normally. A
   * per-request port override is refused rather than dropped or mixed in:
   * project_path brings its own port/auth discovery, and pinging the override
   * port would present another bridge's answer as the probed project's.
   */
  private async routeProjectProbe(toolName: string, args: Record<string, unknown>, identity: RequestIdentity): Promise<CallToolResult> {
    if (typeof args.project_path !== "string" || !args.project_path.trim())
      return localError("invalid_project_path", "project_path must name a Unity project root.");
    if (identity.port !== undefined)
      return localError("invalid_arguments", "project_path discovers that project's own bridge port; drop the _meta.port (or port) override for this call.");
    const selected = resolveProjectPath({ flagPath: args.project_path }).absolute;
    if (!validateUnityProjectRoot(selected).valid)
      return localError("invalid_project_path", "project_path must contain Assets, Packages, and ProjectSettings.");
    const { project_path: _selected, ...rest } = args;
    return this.projectProbeRouter(selected).route(toolName, rest, identity);
  }

  /**
   * Router for a read-only probe of another project: its own port/auth
   * discovery and a throwaway copy of this session. Discovery still reconciles
   * auto-activation against the probed project's inventory, so the probe
   * reports that project's view, but only the copy changes — this session's
   * active groups and tools/list stay put, and no list_changed is wired. The
   * probe tools are local-only and never spawn Unity; the probe's own batch
   * router only answers headless availability for that project's editor.
   */
  private projectProbeRouter(projectPath: string): ToolRouter {
    const live = new LiveClient(resolvePort(projectPath), new PingCache(), resolveAuthToken(projectPath), projectPath);
    return new ToolRouter(live, this.projectProbeBatch(projectPath), projectPath, this.eventStream, this.sessionState.clone());
  }

  // Batch routers of probed projects, keyed by project path and bounded to
  // the most recently probed few. Constructing a BatchSpawn walks every Hub
  // install root synchronously; a polled probe must not pay that on every
  // call, and the router keeps its own editor-resolution cache (revalidated
  // when the project version or the env inputs change).
  private static readonly PROBE_BATCH_CACHE_SIZE = 8;
  private readonly probeBatchByProject = new Map<string, BatchSpawn>();

  private projectProbeBatch(projectPath: string): BatchSpawn {
    const cached = this.probeBatchByProject.get(projectPath);
    if (cached) {
      // Re-insert so the map keeps insertion order as recency order.
      this.probeBatchByProject.delete(projectPath);
      this.probeBatchByProject.set(projectPath, cached);
      return cached;
    }
    const batch = new BatchSpawn({ projectPath });
    this.probeBatchByProject.set(projectPath, batch);
    while (this.probeBatchByProject.size > ToolRouter.PROBE_BATCH_CACHE_SIZE) {
      const oldest = this.probeBatchByProject.keys().next().value;
      if (oldest === undefined) break;
      this.probeBatchByProject.delete(oldest);
    }
    return batch;
  }

  /**
   * M23 Plan 3 — route with a per-call live-client override. Used when a
   * per-request `_meta.port` override targets a different bridge instance than
   * the default. The override client bypasses shared session state (it is a
   * fresh LiveClient aimed at the override port); all other routing logic is
   * identical to {@link route}. `identity` carries the override port and the
   * request's agent id.
   */
  async routeOverride(
    toolName: string,
    args: Record<string, unknown>,
    overrideLive: LiveClient,
    identity: RequestIdentity,
  ): Promise<CallToolResult> {
    return this.routeCore(overrideLive, toolName, args, identity);
  }

  /**
   * Every entry point funnels through here (direct, manage_tools invoke, port
   * override). While the caller's agent owner has an open playtest session, a
   * recorded call (input / wait / capture / project command / play-mode and
   * Game view changes) is logged around its dispatch; everything else — and
   * every call when no session is open — goes straight through. The recorder
   * never changes a result and never fails a call.
   */
  private async routeCore(
    live: LiveClient,
    toolName: string,
    args: Record<string, unknown>,
    identity: RequestIdentity,
  ): Promise<CallToolResult> {
    const recorder = this.playtestRecorder;
    if (!recorder || recorder.openCount === 0 || toolName === PLAYTEST_SESSION_TOOL) return this.routeDispatch(live, toolName, args, identity);
    const owner = this.ownerOf(identity);
    if (!recorder.isRecording(owner)) return this.routeDispatch(live, toolName, args, identity);
    if (toolName === "unity_open_mcp_jobs" && (args.action === "wait" || args.action === "status")) {
      const observed = await this.routeDispatch(live, toolName, args, identity);
      try { recorder.observeJob(owner, args, observed); } catch (error) { console.error("[unity-open-mcp] playtest recorder:", error); }
      return observed;
    }
    let handle: ReturnType<PlaytestRecorder["begin"]> = null;
    try { handle = recorder.begin(owner, toolName, args); } catch (error) { console.error("[unity-open-mcp] playtest recorder:", error); }
    if (!handle) return this.routeDispatch(live, toolName, args, identity);
    (this.eventStream as Partial<BridgeEventStream>).reconnectNow?.call(this.eventStream);
    let result: CallToolResult;
    try {
      result = await this.routeDispatch(live, toolName, args, identity);
    } catch (error) {
      try { recorder.finish(handle, args, null, error); } catch (inner) { console.error("[unity-open-mcp] playtest recorder:", inner); }
      throw error;
    }
    try { recorder.finish(handle, args, result); } catch (error) { console.error("[unity-open-mcp] playtest recorder:", error); }
    return result;
  }

  /** The owner a request files sessions (and jobs) under. */
  private ownerOf(identity: RequestIdentity): SessionOwner {
    return { project: this.projectPath || "default", agent: identity.agent, ...(identity.port === undefined ? {} : { port: identity.port }) };
  }

  private playtestRecorder?: PlaytestRecorder;

  /** The session recorder, created on first use and fed by the bridge event stream. */
  get playtest(): PlaytestRecorder {
    if (!this.playtestRecorder) {
      this.usePlaytestRecorder(new PlaytestRecorder({
        root: sessionsRoot(),
        projectPath: this.projectPath || "default",
        knownSecrets: [this.projectPath ? resolveAuthToken(this.projectPath) : undefined, ...secretEnvValues()],
        streamConnected: () => (this.eventStream as Partial<BridgeEventStream>).isConnected === true,
      }));
    }
    return this.playtestRecorder!;
  }

  /** Install a recorder (tests use a temporary root). */
  usePlaytestRecorder(recorder: PlaytestRecorder): void {
    this.playtestRecorder = recorder;
    (this.eventStream as Partial<BridgeEventStream>).addListener?.call(this.eventStream, event => recorder.onEvent(event));
  }

  /**
   * `unity_open_mcp_playtest_session` — local route. Bridge reads the session
   * needs (company / product, the Game view state, a player-state snapshot or
   * diff) go through the normal route as the same owner, and only when the
   * bridge answers /ping; otherwise the recorder falls back to disk.
   */
  private async routePlaytestSession(args: Record<string, unknown>, live: LiveClient, identity: RequestIdentity): Promise<CallToolResult> {
    const action = String(args.action ?? "") as keyof typeof PLAYTEST_SESSION_ACTION_KEYS;
    const allowed = PLAYTEST_SESSION_ACTION_KEYS[action];
    if (!allowed) return localError("invalid_arguments", `Unknown action '${action}'.`);
    const extra = Object.keys(args).filter(key => key !== "action" && !allowed.includes(key));
    if (extra.length) return localError("invalid_arguments", `Keys not used by ${action}: ${extra.join(", ")}.`);
    const recorder = this.playtest;
    const owner = this.ownerOf(identity);
    let available: boolean | undefined;
    const call: ToolCaller = async (tool, toolArgs) => {
      available ??= await live.isLiveAvailable(1_500);
      if (!available) return localError("bridge_unavailable", "The Unity bridge is not reachable.");
      const definition = ALL_TOOLS.find(t => t.name === tool);
      return this.routeDispatch(live, tool, definition ? withSchemaDefaults(definition, toolArgs) : toolArgs, identity);
    };
    const sessionArg = typeof args.session === "string" ? args.session : undefined;
    try {
      if (action !== "status" || recorder.isRecording(owner) === false) await recorder.prepare(call);
      if (action === "start" || action === "resume" || action === "end")
        (this.eventStream as Partial<BridgeEventStream>).ensureSubscription?.call(this.eventStream);
      let body: Record<string, unknown>;
      switch (action) {
        case "start": body = await recorder.start(owner, args, call); break;
        case "mark": body = recorder.mark(owner, args, sessionArg); break;
        case "check": body = await runCheck(recorder, owner, args, call); break;
        case "status": body = recorder.status(owner); break;
        case "resume": body = recorder.resume(owner, sessionArg); break;
        case "end": body = await recorder.end(owner, args, call); break;
        default: body = recorder.list(owner, typeof args.limit === "number" ? args.limit : 10);
      }
      return sourceResult(body, "local");
    } catch (error) {
      if (error instanceof PlaytestError)
        return sourceResult({ error: { code: error.code, message: error.message }, ...error.detail }, "local", true);
      return localError("internal_error", error instanceof Error ? error.message : String(error));
    }
  }

  /**
   * `unity_open_mcp_playtest_explore` — local route that starts a UI crawl as a
   * job. It needs the caller's open playtest session (the crawl records into
   * it), an isolated save (the session's player-state snapshot or the project's
   * playtest.begin hook, unless allow_unsandboxed) and play mode; the job then
   * drives the bridge through the normal dispatch as the same owner.
   */
  private async routePlaytestExplore(args: Record<string, unknown>, live: LiveClient, identity: RequestIdentity): Promise<CallToolResult> {
    const recorder = this.playtest;
    const owner = this.ownerOf(identity);
    const isolation = recorder.isolation(owner);
    if (!isolation)
      return sourceResult({ error: { code: "no_open_session", message: "The crawler records into your playtest session: start one first." },
        agentNextSteps: ["playtest_session {\"action\":\"start\",\"name\":\"Explore\",\"snapshot\":true} (or invoke the project's playtest.begin), then call playtest_explore again."] }, "local", true);
    if (!isolation.snapshot && !isolation.hook && args.allow_unsandboxed !== true)
      return sourceResult({ error: { code: "not_isolated", message: "The crawler taps through the game for real (progress, currency, saves change), and this session has no player-state snapshot and no playtest.begin hook run." },
        session: isolation.session,
        agentNextSteps: ["Invoke the project's playtest.begin command (project_commands list query playtest), or end this session and start one with snapshot:true — or pass allow_unsandboxed:true if the save may change."] }, "local", true);
    let options;
    try { options = parseExploreOptions(args); }
    catch (error) {
      return localError(error instanceof ExploreError ? error.code : "invalid_arguments", error instanceof Error ? error.message : String(error));
    }
    const call = async (tool: string, toolArgs: Record<string, unknown>) => {
      const definition = ALL_TOOLS.find(t => t.name === tool);
      return this.routeDispatch(live, tool, definition ? withSchemaDefaults(definition, toolArgs) : toolArgs, identity);
    };
    // Fail fast on what the job would refuse anyway: no uGUI reader, or edit mode.
    const probe = await call("unity_open_mcp_ui_snapshot", { profile: "compact", page_size: 1, visible_only: true });
    const probed = toolOutput(probe);
    if (probe.isError || probed.error) return probe;
    if (probed.mode !== "play")
      return sourceResult({ error: { code: "play_mode_required", message: "The crawler explores the running game; the Editor is in edit mode." },
        agentNextSteps: ["editor_set_state {\"state\":\"play\"}, editor_status {\"wait_until\":\"playing\"}, boot to the first real screen, then call playtest_explore again."] }, "local", true);
    return this.startPlaytestJob(PLAYTEST_EXPLORE_TOOL, exploreOperation({ options, call, recorder, owner, session: isolation.session }), args, identity, {
      session: isolation.session,
      isolation: { snapshot: isolation.snapshot, hook: isolation.hook, unsandboxed: !isolation.snapshot && !isolation.hook },
      budgets: { max_steps: options.maxSteps, max_minutes: options.maxMinutes, max_screens: options.maxScreens, restart: options.restart, max_restarts: options.maxRestarts },
    }, "The crawl is running.", "Do not drive the game yourself while it runs: your taps would mix into its screens.");
  }

  /**
   * `unity_open_mcp_playtest_devices` — local route that starts a device-matrix
   * run as a job: the current screen (or a crawl) on each Device Simulator
   * device, recorded into the caller's open session, the view put back after.
   * A crawl per device needs what playtest_explore needs (isolation, play mode).
   */
  private async routePlaytestDevices(args: Record<string, unknown>, live: LiveClient, identity: RequestIdentity): Promise<CallToolResult> {
    const recorder = this.playtest;
    const owner = this.ownerOf(identity);
    const isolation = recorder.isolation(owner);
    if (!isolation)
      return sourceResult({ error: { code: "no_open_session", message: "The device checks are recorded into your playtest session: start one first." },
        agentNextSteps: ["playtest_session {\"action\":\"start\",\"name\":\"Devices\"} (snapshot:true when the run crawls), then call playtest_devices again."] }, "local", true);
    let options;
    try { options = parseDevicesOptions(args); }
    catch (error) {
      return localError(error instanceof ExploreError ? error.code : "invalid_arguments", error instanceof Error ? error.message : String(error));
    }
    const call = async (tool: string, toolArgs: Record<string, unknown>) => {
      const definition = ALL_TOOLS.find(t => t.name === tool);
      return this.routeDispatch(live, tool, definition ? withSchemaDefaults(definition, toolArgs) : toolArgs, identity);
    };
    if (options.screens === "explore") {
      if (!isolation.snapshot && !isolation.hook && args.allow_unsandboxed !== true)
        return sourceResult({ error: { code: "not_isolated", message: "A crawl per device taps through the game for real, and this session has no player-state snapshot and no playtest.begin hook run." },
          session: isolation.session,
          agentNextSteps: ["Invoke the project's playtest.begin, or start the session with snapshot:true — or pass allow_unsandboxed:true if the save may change."] }, "local", true);
      const probe = await call("unity_open_mcp_ui_snapshot", { profile: "compact", page_size: 1, visible_only: true });
      const probed = toolOutput(probe);
      if (probe.isError || probed.error) return probe;
      if (probed.mode !== "play")
        return sourceResult({ error: { code: "play_mode_required", message: "A crawl per device explores the running game; the Editor is in edit mode." },
          agentNextSteps: ["editor_set_state {\"state\":\"play\"}, editor_status {\"wait_until\":\"playing\"}, boot to the first real screen, then call playtest_devices again."] }, "local", true);
    }
    return this.startPlaytestJob(PLAYTEST_DEVICES_TOOL, devicesOperation({ options, call, recorder, owner, session: isolation.session }), args, identity, {
      session: isolation.session,
      plan: { devices: options.devices ?? `preset ${options.preset}`, orientations: options.orientations, screens: options.screens, max_devices: options.maxDevices },
    }, "The device run is going.", "Do not change the Game view or drive the game while it runs; the view it found is put back at the end.");
  }

  /**
   * `unity_open_mcp_playtest_replay` — local route. export / list / show read
   * and write scenario files in the project (Playtests/Scenarios); run starts a
   * scenario replay as a job, under the same conditions as a crawl (the
   * caller's open session, an isolated save, play mode unless the run restarts
   * play mode), with the bridge event stream feeding it console errors.
   */
  private async routePlaytestReplay(args: Record<string, unknown>, live: LiveClient, identity: RequestIdentity): Promise<CallToolResult> {
    const action = String(args.action ?? "run") as keyof typeof PLAYTEST_REPLAY_ACTION_KEYS;
    const allowed = PLAYTEST_REPLAY_ACTION_KEYS[action];
    if (!allowed) return localError("invalid_arguments", `Unknown action '${action}'.`);
    const extra = Object.keys(args).filter(key => key !== "action" && !allowed.includes(key));
    if (extra.length) return localError("invalid_arguments", `Keys not used by ${action}: ${extra.join(", ")}.`);
    if (!this.projectPath) return localError("project_unknown", "Scenarios live in the Unity project, and this server has no project path (set UNITY_PROJECT_PATH or start it in the project).");
    const recorder = this.playtest;
    const owner = this.ownerOf(identity);
    const fail = (error: unknown): CallToolResult => {
      if (error instanceof ScenarioError || error instanceof PlaytestError)
        return sourceResult({ error: { code: error.code, message: error.message }, ...error.detail }, "local", true);
      return localError("internal_error", error instanceof Error ? error.message : String(error));
    };
    try {
      if (action === "list") return sourceResult(listAction(this.projectPath), "local");
      if (action === "show") return sourceResult(showAction(this.projectPath, args), "local");
      if (action === "export") return sourceResult(exportAction(recorder, owner, this.projectPath, args), "local");
    } catch (error) { return fail(error); }

    const isolation = recorder.isolation(owner);
    if (!isolation)
      return sourceResult({ error: { code: "no_open_session", message: "The replay records into your playtest session: start one first." },
        agentNextSteps: ["playtest_session {\"action\":\"start\",\"name\":\"Replay\",\"snapshot\":true} (or invoke the project's playtest.begin), then call playtest_replay again."] }, "local", true);
    if (!isolation.snapshot && !isolation.hook && args.allow_unsandboxed !== true)
      return sourceResult({ error: { code: "not_isolated", message: "The replay taps through the game for real (progress, currency, saves change), and this session has no player-state snapshot and no playtest.begin hook run." },
        session: isolation.session,
        agentNextSteps: ["Invoke the project's playtest.begin command, or end this session and start one with snapshot:true — or pass allow_unsandboxed:true if the save may change."] }, "local", true);
    let prepared;
    try { prepared = prepareReplay(this.projectPath, args); } catch (error) { return fail(error); }
    const call = async (tool: string, toolArgs: Record<string, unknown>) => {
      const definition = ALL_TOOLS.find(t => t.name === tool);
      return this.routeDispatch(live, tool, definition ? withSchemaDefaults(definition, toolArgs) : toolArgs, identity);
    };
    const probe = await call("unity_open_mcp_ui_snapshot", { profile: "compact", page_size: 1, visible_only: true });
    const probed = toolOutput(probe);
    if (probe.isError || probed.error) return probe;
    const restartsFromEdit = prepared.options.from === "start" && prepared.scenario.start.restart === "play_mode";
    if (probed.mode !== "play" && !restartsFromEdit)
      return sourceResult({ error: { code: "play_mode_required", message: "The replay runs on the running game; the Editor is in edit mode (and this run does not restart play mode)." },
        agentNextSteps: ["editor_set_state {\"state\":\"play\"}, editor_status {\"wait_until\":\"playing\"}, then call playtest_replay again — or replay from: \"start\" a scenario whose start restarts play mode."] }, "local", true);
    (this.eventStream as Partial<BridgeEventStream>).ensureSubscription?.call(this.eventStream);
    const stream = this.eventStream as Partial<BridgeEventStream>;
    const subscribe = (feed: { onEvent(event: BridgeEvent): void }) => stream.addListener?.call(this.eventStream, event => feed.onEvent(event)) ?? (() => {});
    const { scenario, options } = prepared;
    return this.startPlaytestJob(PLAYTEST_REPLAY_TOOL, replayOperation({ ...prepared, projectPath: this.projectPath, call, recorder, owner, session: isolation.session, subscribe }), args, identity, {
      session: isolation.session,
      scenario: { name: scenario.name, steps: scenario.steps.length, start: scenario.start.restart, unbaselined: scenario.steps.filter(s => !s.expect?.screen).length },
      plan: { from: options.from, retries: options.record ? 0 : options.retries, record: options.record, strict: options.strict, capture: options.capture },
      isolation: { snapshot: isolation.snapshot, hook: isolation.hook, unsandboxed: !isolation.snapshot && !isolation.hook },
    }, options.record ? "The baseline run is going." : "The replay is running.", "Do not drive the game yourself while it runs: your taps would mix into its steps.");
  }

  /** Register a per-call playtest job operation, start it, and answer with its id once it got (or queued for) its slot. */
  private async startPlaytestJob(name: string, operation: JobOperation, args: Record<string, unknown>, identity: RequestIdentity,
    extra: Record<string, unknown>, running: string, hands: string): Promise<CallToolResult> {
    const jobOwner = this.jobOwner(identity);
    try {
      // Registered per call: the operation closes over this owner's route, and a
      // job keeps the operation it started with.
      this.jobs.register(name, operation, true);
      const started = this.jobs.start(jobOwner, name, args);
      // The job is scheduled on the next turn of the loop; report whether it got its slot.
      await new Promise<void>(resolve => setImmediate(resolve));
      const job = this.jobs.status(jobOwner, started.job_id);
      return sourceResult({
        status: job.state === "queued" ? "queued" : "running",
        job_id: job.job_id,
        ...extra,
        note: job.state === "queued" ? "Another job of this project is running; this one starts when it ends." : running,
        agentNextSteps: [
          `Call unity_open_mcp_jobs {"action":"wait","job_id":"${job.job_id}","timeout_ms":30000} until the state is terminal (progress shows how far it got); jobs cancel stops it and keeps the result so far.`,
          hands,
        ],
      }, "local");
    } catch (error) {
      return localError(error instanceof JobManagerError ? error.code : "job_start_failed", error instanceof Error ? error.message : String(error));
    }
  }

  private async routeDispatch(
    live: LiveClient,
    toolName: string,
    args: Record<string, unknown>,
    identity: RequestIdentity,
  ): Promise<CallToolResult> {
    // Every entry point funnels through here, so an explicit project_path is
    // retargeted before validation and wiring wherever the call came from.
    if ((toolName === "unity_open_mcp_capabilities" || toolName === "unity_open_mcp_bridge_status") && args.project_path !== undefined)
      return this.routeProjectProbe(toolName, args, identity);
    const definition = ALL_TOOLS.find(t => t.name === toolName);
    if (definition) {
      const errors = validateSchema(args, definition.inputSchema);
      if (errors.length) return sourceResult({ error: { code: "invalid_arguments", message: errors.join("; ") }, errors, _route: { route: "local" } }, "local", true);
      const notes: string[] = [];
      const wire = wireArguments(args, definition.inputSchema, notes);
      if (toolName === "unity_open_mcp_batch_execute" && Array.isArray(wire.commands)) {
        wire.commands = (wire.commands as Array<{ tool?: string; params?: unknown }>).map(command => {
          const nested = ALL_TOOLS.find(t => t.name === command.tool);
          return nested
            ? { ...command, params: wireArguments(command.params ?? {}, nested.inputSchema, notes) }
            : command;
        });
      }
      // Recursive dispatch only after validation; alias notes are attached to the normal result.
      return this.routeValidated(live, toolName, wire, notes, identity);
    }
    return this.routeValidated(live, toolName, args, [], identity);
  }

  private async routeValidated(live: LiveClient, toolName: string, args: Record<string, unknown>, notes: string[], identity: RequestIdentity): Promise<CallToolResult> {
    const result = await this.routeUnchecked(live, toolName, args, identity);
    if (notes.length) result.content.push({ type: "text", text: JSON.stringify({ deprecations: [...new Set(notes)] }) });
    if (toolName === "unity_open_mcp_execute_csharp") attachTypedToolHints(result, typedToolHints(args.code));
    return result;
  }

  private async routeUnchecked(live: LiveClient, toolName: string, args: Record<string, unknown>, identity: RequestIdentity): Promise<CallToolResult> {
    // Mid-reload policy. A call that arrives while the Editor is compiling,
    // reloading or changing play mode (or right after a call left a compile
    // due) waits for it to settle — bounded by reloadWaitMs so it stays inside
    // a host's request timeout — and only then dispatches; past the budget it
    // is refused with a stable editor_reloading + retryAfterMs and the wait
    // call to make. Local tools, offline-capable reads and the catalog probe
    // answer without the bridge; editor_status and ping answer for themselves.
    const exempt = skipsReloadProbe(toolName) || toolName === WAIT_TOOL || toolName === "unity_open_mcp_ping";
    if (exempt) return this.routeHandled(live, toolName, args, identity);
    const lock = this.projectPath ? readInstanceLock(this.projectPath) : null;
    // The lock describes the default project's Editor. A per-request port
    // override aimed elsewhere has no lock of its own (override clients never
    // read locks) and its LiveClient handles that bridge's readiness, so the
    // default Editor's reload must not hold it. An override naming the
    // default Editor's own port still targets the reloading Editor.
    const targetsLockedEditor = identity.port === undefined || identity.port === lock?.port;
    if (!targetsLockedEditor) return this.routeHandled(live, toolName, args, identity);
    // One wait budget for the whole call: this pre-dispatch wait, the client's
    // readiness / re-send waits (it gets the same deadline) and the one resend
    // below all draw from it, so they cannot stack past it.
    const deadline = Date.now() + this.reloadWaitMs;
    if (lockShowsTransition(lock, Date.now(), () => this.bridgeLossFor(lock))) {
      // No headless fallback can acquire this Editor's project: wait, never batch.
      const settled = await this.waitForDispatch(live, toolName, deadline - Date.now());
      if (settled.refusal) return settled.refusal;
    }
    const result = await this.routeHandled(live, toolName, args, identity, deadline);
    // The bridge refused the call (nothing ran) even after the client's own
    // wait: wait once more within what is left of the budget and send it
    // again. A call that may have run (dispatched true / null) is never resent.
    if (deadline - Date.now() > 0 && resendAfterReload(result)) {
      const settled = await this.waitForDispatch(live, toolName, deadline - Date.now());
      if (settled.refusal) return result;
      return this.routeHandled(live, toolName, args, identity, deadline);
    }
    return result;
  }

  /** Editor.log evidence that this lock's bridge cannot come back (null: none, or the heartbeat still beats). */
  private bridgeLossFor(lock: InstanceLock | null): BridgeLossEvidence | null {
    if (!lock || classifyInstance(lock) !== "dead_bridge") return null;
    return readBridgeLossEvidence(this.projectPath, { livePid: lock.pid, globalLogPathOverride: this.globalLogPathOverride });
  }

  /**
   * How long an editor_status wait runs inside one call before it continues as
   * a job. The stdio server keeps it under a host's request timeout; the CLI
   * (no host, and a job would end with its process) waits inline throughout.
   */
  waitInlineLimitMs = WAIT_INLINE_LIMIT_MS;

  /** How long a call that meets a transition waits before it is refused (env: UNITY_OPEN_MCP_RELOAD_WAIT_MS). */
  private readonly reloadWaitMs = readReloadWaitMs();

  /**
   * Wait (bounded) until the Editor is settled enough to take a call. A dead
   * bridge or a missing Editor is not waited out: the normal route reports it
   * (bridge_compile_failed / bridge_offline).
   */
  private async waitForDispatch(live: LiveClient, toolName: string, budgetMs: number): Promise<{ waitedMs: number; refusal?: CallToolResult }> {
    const outcome = await waitForEditor({ until: "idle", timeoutMs: Math.max(0, budgetMs) }, this.waitDeps(live, this.projectPath, false));
    if (outcome.reached || outcome.reason === "dead_bridge" || outcome.reason === "gone") return { waitedMs: outcome.waitedMs };
    return { waitedMs: outcome.waitedMs, refusal: reloadingRefusal(toolName, outcome.view, outcome.waitedMs) };
  }

  /**
   * What the wait primitive reads: lock + /ping, /compile-state, and (for the
   * final answer) the same compile summary read_compile_errors gives, so the
   * wait's error count agrees with it.
   */
  private waitDeps(live: LiveClient, projectPath: string | undefined, withSummary = true): WaitDeps {
    return {
      signals: () => readEditorSignals(projectPath, live, 2_000, this.globalLogPathOverride),
      compileState: async () => compileReading(await live.readCompileState?.() ?? null),
      ...(withSummary ? {
        compileSummary: async () => {
          const summary = toolResultBody(await this.routeReadCompileErrors({}, live));
          return summary._source === "live" ? compileReading(summary) : compileReading(await live.readCompileState?.() ?? null);
        },
      } : {}),
      pendingTestRun: () => hasRecentPendingTestRun(),
    };
  }

  private async routeHandled(live: LiveClient, toolName: string, args: Record<string, unknown>, identity: RequestIdentity, waitDeadline?: number): Promise<CallToolResult> {
    // Named-tool dispatch (T3.2): the handler map covers every exact-name
    // route. The per-tool policy rationale (offline / local / hub / offline-
    // first / verify-fold / scene-paging) lives in each routeXxx method's
    // header comment.
    const handler = this.getRouteHandlers().get(toolName);
    if (handler) {
      return handler(live, args, identity);
    }

    // Compact drill-down reads: offline-first for text-serialized assets, fall
    // back to live bridge for binary formats. The compressible-router handles
    // the source selection internally and tags the payload `_source`
    // ("offline" when it was served from the disk parser without touching the
    // bridge; error results tag their origin too — "local" for caller-side
    // validation, "offline" for a failed offline-first attempt with the
    // bridge down). Derive `_route` from that tag: stamping route:"live"
    // unconditionally mislabeled offline-served reads as bridge responses —
    // contradictory metadata that pointed an agent's diagnostics at the
    // bridge for data it never produced.
    if (isCompressible(toolName)) {
      const result = await routeCompressible(
        toolName,
        args,
        live,
        this.modelCache,
        this.projectPath,
      );
      const source = parseResultBody(result)?._source;
      const meta: RouteMeta =
        source === "offline" || source === "local"
          ? { route: source }
          : { route: "live" };
      return injectRouteMeta(result, meta);
    }

    const canBatch = this.batch.isBatchTool(toolName);
    const isPing = toolName === "unity_open_mcp_ping";

    // compile_check always routes to batch — even when the live bridge is up.
    // The whole point is to spawn a FRESH Unity that recompiles from scratch;
    // running it against a live Editor that already compiled successfully
    // would trivially report "compile_passed" and never surface the broken
    // state the tool exists to diagnose.
    //
    // Verify-family tools (scan_all / baseline_create / regression_check) are
    // batch-only: they require the headless verify package and are NOT
    // registered on the live bridge. Routing them live (the previous default
    // when the bridge was up) yielded a bare `tool_not_found`. An open Editor
    // holding the project lock surfaces as `editor_instance_locked` from the
    // batch spawn (the expected degraded outcome — agents should read the lock
    // code, not chase a live registration miss).
    //
    // validate_edit / scan_paths are intentionally excluded: those ARE
    // registered on the live bridge and stay live-first (routeVerifyResult
    // below handles their output-profile folding). The always-batch policy is
    // declared once in ALWAYS_BATCH_TOOLS (tool name → reason); do not add a
    // second pinned branch for a new always-batch tool — extend the map.
    const alwaysBatchReason = ALWAYS_BATCH_TOOLS.get(toolName);
    if (alwaysBatchReason !== undefined) {
      logRoute(
        `[unity-open-mcp] Route: ${toolName} -> batch (always-batch: ${alwaysBatchReason})`,
      );
      const result = await this.batch.route(toolName, args);
      return injectRouteMeta(result, {
        route: "batch",
        fallbackReason: alwaysBatchReason,
      });
    }

    if (!canBatch && !isPing) {
      const result = await live.route(toolName, args, undefined, { waitDeadline });
      return injectRouteMeta(result, { route: "live" });
    }

    const liveAvailable = await live.isLiveAvailable();

    if (liveAvailable) {
      logRoute(`[unity-open-mcp] Route: ${toolName} -> live`);
      const result = await live.route(toolName, args, undefined, { waitDeadline });
      // ping answered 503 (the listener is back, the session not yet): the
      // Editor is still coming out of a reload — say so, not an error.
      if (isPing && result.isError) return (await this.lockDerivedPing(live)) ?? injectRouteMeta(result, { route: "live" });
      return injectRouteMeta(result, { route: "live" });
    }

    logRoute(
      `[unity-open-mcp] Route: ${toolName} -> batch (live bridge unavailable)`,
    );

    if (isPing) {
      // The listener is down: a fresh lock still says what the Editor is doing
      // (compiling, reloading, changing play mode). Answer with that instead
      // of an error, like bridge_status and editor_status do.
      return (await this.lockDerivedPing(live)) ?? this.batchPingResult();
    }

    const result = await this.batch.route(toolName, args);
    // feedback-fable-04-08 §7 — when the batch fallback hit a live editor's
    // project lock, the actionable signal is editor_busy, not live_unavailable.
    const meta = this.rewriteEditorBusy(result, {
      route: "batch",
      fallbackReason: "live_unavailable",
    });
    return injectRouteMeta(result, meta);
  }

  /** ping while the listener is away but the instance lock shows a live Editor; null otherwise. */
  private async lockDerivedPing(live: LiveClient): Promise<CallToolResult | null> {
    if (!this.projectPath) return null;
    const view = viewEditor(await readEditorSignals(this.projectPath, live, 1_000, this.globalLogPathOverride), hasRecentPendingTestRun());
    if (view.phase !== "transition" && view.phase !== "unreachable" && view.phase !== "wedged") return null;
    return sourceResult({
      connected: false,
      projectPath: this.projectPath,
      mode: "live",
      compiling: view.isCompiling,
      isPlaying: view.isPlaying,
      phase: view.phase,
      ...statusFields(view),
      retryAfterMs: retryAfterMsFor(view),
      source: "lock",
      note: "The bridge listener is not answering while the Editor " +
        (view.transition ? `is ${view.transition.replace(/_/g, " ")}` : "is unreachable") +
        "; this answer is read from the instance lock.",
      agentNextSteps: [`Wait for the Editor with ${waitCall("idle")} before live calls.`],
      _route: { route: "local", fallbackReason: "live_unavailable" },
    }, "local");
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
            // feedback 2026-08-17 — provenance marker distinguishing this
            // synthesized fallback (no probe was possible; the live route was
            // unavailable) from the live route's verified probe answer
            // (source:"probe" + asOf).
            source: "unprobed",
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

  /**
   * feedback-fable-04-08 §7 — when a `live_unavailable` batch fallback lands
   * on `editor_instance_locked`, the message describes the BATCH SPAWNER's
   * problem ("a live editor holds the project lock, so no headless spawn"),
   * but the agent's actionable truth is "the editor is busy (TestRunner run /
   * long import); retry later", not a lock conflict. Detect this case by
   * inspecting the batch result for `error.code === "editor_instance_locked"`
   * AND confirming a live PID still holds the project (a stale lock with no
   * live PID is a genuine lock conflict — leave it as `live_unavailable`).
   *
   * Returns the rewritten meta (and the result unchanged — the lock error body
   * is real and must stay intact) so the caller stamps it via injectRouteMeta.
   * The live PID is surfaced as `editorPid` for agent correlation. Falls
   * through unchanged when the code differs, no live PID exists, or the body
   * is unparseable.
   */
  private rewriteEditorBusy(
    result: CallToolResult,
    meta: RouteMeta,
  ): RouteMeta {
    if (meta.fallbackReason !== "live_unavailable") return meta;
    const body = parseResultBody(result);
    if (body === null) return meta;
    const err = body.error;
    const code =
      typeof err === "object" && err !== null
        ? (err as { code?: unknown }).code
        : undefined;
    if (code !== "editor_instance_locked") return meta;
    // Confirm a live editor actually holds the project. The batch pre-spawn
    // lock check already read the lock, but a race (the editor quit between
    // the batch check and here) or a corrupt lock must NOT be misreported as
    // "busy" — fall through to the generic live_unavailable in that case.
    try {
      const lock = readInstanceLock(this.projectPath);
      if (!lock?.pid || !isPidAlive(lock.pid)) return meta;
      return {
        route: "batch",
        fallbackReason: "editor_busy",
        editorPid: lock.pid,
      };
    } catch {
      return meta;
    }
  }

  private async routeReadCompileErrors(
    args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const tailBytes =
      typeof args.tail_bytes === "number" && args.tail_bytes >= 4096
        ? Math.min(Math.floor(args.tail_bytes), 1048576)
        : DEFAULT_LOG_TAIL_BYTES;

    // Resolve the authoritative Editor.log. Unity 6000.5+ writes a
    // project-relative log (<project>/Logs/Editor.log); the global per-user
    // log is stale there. resolveEditorLogPath prefers the project-relative
    // log when it exists and falls back to the global log for older Unity.
    //
    // feedback-fable-31-07 §5 — when a live editor holds the project AND the
    // resolved Editor.log looks frozen (rotated by a batch spawn that failed
    // on the project lock), fall back to Editor-prev.log, where the live
    // editor keeps writing. Pass the live PID from the instance lock so the
    // resolver can make the strong-signal decision.
    const livePid = this.resolveLiveEditorPid();
    const resolvedLog = resolveEditorLogPath(this.projectPath, undefined, livePid, this.globalLogPathOverride);
    const logPath = resolvedLog.path;
    const tail = readLogTail(logPath, tailBytes);

    // The log's errors split into current and historical (classifyLogCompileErrors),
    // anchored on the newest built assembly. Computed only off the live happy
    // path: a determinate live answer needs it just for a failure the bridge
    // did not observe.
    let classifiedMemo: ClassifiedLogErrors | undefined;
    const classify = (): ClassifiedLogErrors => classifiedMemo ??= classifyLogCompileErrors(tail.content, {
      projectRoot: this.projectPath,
      logMtimeMs: tail.exists && !tail.error && statMtimeMs(logPath) > 0 ? statMtimeMs(logPath) : undefined,
      dllMtimeMs: newestScriptAssemblyMtimeMs(this.projectPath),
    });

    const { compile, view } = await this.readLiveCompileState(live);
    if (compile && compile.status !== "indeterminate") {
      const historical = summarizeProjectHealth(tail.content, this.projectPath);
      const failed = compile.status === "compile_failed";
      const pipelineErrors = failed && Array.isArray(compile.errors) ? compile.errors : [];
      // Unity can report a failed compile the bridge never observed (it ran
      // before the bridge domain loaded, e.g. the Editor-startup compile), so
      // the pipeline holds no diagnostics for it. Editor.log does: promote its
      // current errors (not the historical ones) instead of answering "failed"
      // with nothing to fix.
      const fromLog = failed && pipelineErrors.length === 0;
      const currentErrors = fromLog ? classify().current : pipelineErrors;
      const generation = typeof compile.generation === "number" ? compile.generation : null;
      const due = compile.status === "currently_compiling" || compile.pending === true;
      // A running compile already carries its own generation (it is counted
      // when it starts): wait for idle. A requested one is past the last.
      const wait = compile.status === "currently_compiling" ? waitCall("idle") : waitCall("idle", generation);
      return sourceResult({
        ...compile, errorCount: currentErrors.length, errors: currentErrors,
        errorSource: fromLog ? "Editor.log" : "CompilationPipeline",
        determinacy: "current",
        unhealthy: failed,
        staleAssembly: compile.status === "assembly_stale",
        headline: compile.status === "currently_compiling" ? `Compilation is active (generation ${generation}); wait for it with ${wait}.`
          : compile.pending === true ? `A compile was requested and has not started yet; the last completed generation is ${generation}. Wait for it with ${wait}.`
          : compile.status === "no_errors_found" ? "Current compile is clean; source content matches the completed generation."
          : compile.status === "assembly_stale" ? "Source content changed after the last compile; recompile before trusting assemblies."
          : !fromLog ? "Current compilation failed. Fix the compiler diagnostics and recompile."
          : currentErrors.length > 0
            ? "Current compilation failed in a compile the bridge did not observe (e.g. at Editor " +
              `startup); errors are the ${currentErrors.length} compiler diagnostic(s) from Editor.log. ` +
              "Fix them and recompile."
            : "Current compilation failed in a compile the bridge did not observe (e.g. at Editor " +
              "startup), and the Editor.log tail holds no current compiler diagnostics. Raise tail_bytes or " +
              `recompile (${toolHintReference("unity_open_mcp_recompile_scripts")}) to capture them.`,
        ...(due ? { agentNextSteps: [`Wait for the compile with ${wait}: it returns once it ran, with its status and error count.`] } : {}),
        compileSource: "CompilationPipeline", logPath, logSource: resolvedLog.reason,
        logAuthorship: parseLogAuthorship(tail.content),
        historicalLogErrors: fromLog ? classify().historical : historical.compilerErrors,
        historicalLogIssues: historical.issues,
        logReadError: tail.error ?? null,
      }, "live");
    }

    // The live Editor is there (its instance lock is fresh) but did not give
    // a determinate compile state: it is compiling or reloading, its main
    // thread did not answer in time, or its source fingerprint is still
    // warming after a reload. The lock still carries the last generation and
    // whether it failed — that is the answer, not an old log read as current.
    if (view?.compile && LIVE_EDITOR_PHASES.has(view.phase)) {
      return this.lockCompileAnswer(view, compile, classify(), tail, logPath, resolvedLog.reason);
    }
    const classified = classify();

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
              determinacy: "indeterminate",
              errorCount: 0,
              errors: [],
              message: `No Editor.log found at ${logPath}. The Unity Editor ` +
                "may not have written errors yet, or it is running with a " +
                "custom -logFile path that this tool does not resolve.",
              agentNextSteps: offlineCompileNextSteps(view),
              logPath,
            }),
          },
        ],
        isError: false,
      };
    }

    // Build the full health summary: the CURRENT CSxxxx compiler errors (the
    // historical ones — superseded by a later successful build, or citing
    // source that no longer matches — are reported apart and never drive the
    // verdict) PLUS package / assembly-level red flags (unresolved-assembly
    // Cecil failures, package deprecation, Package Manager errors, failed
    // package installs) from the SAME log tail.
    const errors = classified.current;
    const health = summarizeProjectHealth(tail.content, this.projectPath, errors);

    // specs/feedback.md 2026-07-05 — stale-log detection. When an assembly is
    // stuck in a failed-compile state, AssetDatabase.Refresh no-ops and the
    // log's most-recent CSxxxx block can reference on-disk source that has
    // ALREADY been fixed. Compare each cited source file's mtime against the
    // log's mtime; if any source file is newer, attach staleLogSuspected so
    // the agent knows to force a recompile (reimport_package / compile_check)
    // before trusting the errors. Only run this when there are errors to
    // check — a clean log is by definition not stale.
    let staleLog: ReturnType<typeof detectStaleLog> | undefined;
    if (errors.length > 0) {
      const stale = detectStaleLog(
        logPath,
        errors.map((e) => e.file),
        this.projectPath,
      );
      if (stale.staleLogSuspected) staleLog = stale;
    }

    // feedback-01-08-glm §5b — stale-ASSEMBLY detection. Runs UNCONDITIONALLY
    // offline: after a C# edit, AssetDatabase.Refresh often no-ops, the DLL is
    // never rebuilt, and the log carries no fresh CSxxxx — so a clean log
    // against a stale assembly misleads the agent into trusting code that has
    // not actually been rebuilt. Cross-check the newest
    // Library/ScriptAssemblies/*.dll mtime against Assets/**/*.cs source mtimes.
    const staleAsm = detectStaleAssembly(this.projectPath);

    // specs/feedback.md 2026-08-14 — "these errors may predate your edits".
    // Three consecutive reads returned the same error block for code that had
    // already been fixed, with no staleness signal: Editor.log's FILE mtime
    // was fresh (asset imports keep appending) while the error BLOCK inside it
    // was old, so the staleLog heuristic could not fire. The built-assembly
    // mtime is the anchor that works — a cited source newer than the newest
    // built DLL proves no compile has COMPLETED since the edit.
    const errorsPredateEdits =
      errors.length > 0
        ? detectErrorsPredatingEdits(
            errors.map((e) => e.file),
            this.projectPath,
            staleAsm.dllMtimeMs,
          )
        : null;

    // feedback-04-08-opus §2 — log-authorship cross-check. A batch run of a
    // NEWER Unity writes a log whose errors (e.g. an API deprecation that is
    // only an error on that version) CANNOT occur in the live editor. Parse the
    // log header's version + batch-mode + date, compare against the live bridge
    // version, and surface a mismatch so the agent does not "fix" 31 unrelated
    // files or refuse to continue on errors that cannot occur here. The fields
    // are always emitted when the header was parseable (so an agent can see the
    // log was a batch run even when versions agree); the warning hint is added
    // only on a real mismatch or a batch-mode log.
    const authorship = parseLogAuthorship(tail.content);
    const liveUnityVersion = this.resolveLiveUnityVersion();
    const authorshipCheck = compareLogAuthorship(authorship, liveUnityVersion);

    // feedback-fable-04-08 §4 — downgrade the top-level status when the errors
    // may not apply to the running editor. An agent that trusts a
    // `compile_failed` + `unhealthy: true` headline will start "fixing" 31
    // phantom errors from a stale / batch-authored log. When EITHER the log is
    // stale (a cited source is newer than the log) OR a version-mismatch is
    // detected (a different Unity wrote the log), the verdict becomes
    // `stale_log` — the errors[] are still attached as evidence, but the
    // headline + status make clear they MUST NOT be acted on until a genuine
    // recompile confirms them. The raw verdict (what the log itself says) is
    // preserved in `logVerdict` for agents that want the literal signal.
    const logVerdict = health.unhealthy
      ? errors.length > 0
        ? "compile_failed"
        : "project_unhealthy"
      : "no_errors_found";
    const errorsMayNotApply =
      health.unhealthy &&
      (!!staleLog || authorshipCheck.versionMismatch);
    // A clean offline read is never a clean bill of health: no compile
    // generation backs it, and the log can predate the edits, come from
    // another Editor session or miss a compile still running (an agent once
    // reported "compilation is clean" on exactly this shape while the live
    // Editor was busy). A no-errors read — including one from the ROTATED
    // Editor-prev.log (feedback 2026-08-17) — is `indeterminate`, with the
    // raw verdict in `logVerdict` and the way to a determinate answer in
    // agentNextSteps.
    const prevLogRead = resolvedLog.reason.startsWith("prev_log_");
    const status = errorsMayNotApply
      ? "stale_log"
      : logVerdict === "no_errors_found"
        ? "indeterminate"
        : logVerdict;
    const indeterminateSteps = status === "indeterminate" ? offlineCompileNextSteps(view) : [];

    // specs/feedback.md 2026-08-12 — partial-compile signal. Unity compiles
    // assemblies in dependency order; when an early assembly fails the pipeline
    // may not reach dependent ones, so the reported errors can be a SUBSET of
    // the true error set. When there are errors AND the project has multiple
    // compile assemblies, surface asmdefCount + assembliesWithErrors +
    // partialCompileLikely so an agent does not treat `errorCount` as final
    // (this is exactly the trap that masked 6 errors behind 1 during the
    // input-simulation review).
    // Only walk for asmdefs when there are errors — the signal is only
    // meaningful with errors, and countProjectAsmdefs stats the whole Assets/
    // tree synchronously (memoized per project, but still skip it when unused).
    const hasErrors = errors.length > 0;
    const asmdefs = hasErrors
      ? countProjectAsmdefs(this.projectPath)
      : { count: 0, sample: [] as string[] };
    const partialCompileLikely = hasErrors && asmdefs.count > 1;
    const assembliesWithErrors = partialCompileLikely
      ? countAssembliesWithErrors(errors.map((e) => e.file), this.projectPath)
      : 0;

    // Headline assembly. Precedence (most severe first):
    //   1. errorsMayNotApply — stale log / version mismatch: the errors may
    //      not apply at all (feedback-fable-04-08 §4).
    //   2. errorsPredateEdits — errors + a stale assembly: the error block
    //      was written by a compile that predates the on-disk edits (no
    //      compile has COMPLETED since: every built DLL is older than a cited
    //      source). The log-mtime staleness heuristic cannot see this — asset
    //      imports keep appending to Editor.log, so the FILE is fresh while
    //      the error BLOCK inside it is old. Say so in the headline rather
    //      than letting the agent re-read code it has already fixed
    //      (specs/feedback.md 2026-08-14).
    //   3. feedback 2026-08-17 — errors + staleAssembly WITHOUT the
    //      cited-files proof: the whole-Assets scan still shows a source
    //      newer than the newest built DLL, so the error block may come from
    //      an earlier compile. The 2026-08-14 fix covered only the
    //      errorsPredateEdits shape; this extends the headline caveat to the
    //      staleAssembly shape (the field report's 12 stale CS errors had
    //      staleAssembly:true but the headline stated them as fact).
    //   4. indeterminate — no current errors: not a verified clean compile
    //      (the ROTATED-log case says which file was read, feedback 2026-08-17).
    //   5. the plain health headline.
    const headline: string = errorsMayNotApply
      ? `${errors.length} error(s) in Editor.log, but the log appears ` +
        "STALE or authored by a different Unity — these errors may NOT " +
        "apply to the running editor. Do NOT act on them until a genuine " +
        `recompile (${toolHintReference("unity_open_mcp_recompile_scripts")}) ` +
        "confirms them."
      : errorsPredateEdits
        ? `${health.headline} NOTE: these errors may PREDATE your edits — ` +
          `${errorsPredateEdits.files.length} cited source file(s) are newer ` +
          "than the newest built assembly, so no compile has completed since " +
          "you changed them. Force a recompile " +
          `(${toolHintReference("unity_open_mcp_recompile_scripts")}) and ` +
          "re-read before acting on them."
        : hasErrors && staleAsm.staleAssembly
          ? `${health.headline} NOTE: the error block may PREDATE the current ` +
            `sources — ${staleAsm.newerSources.length} Assets/**/*.cs source(s) ` +
            "are newer than the newest built assembly, so these errors may come " +
            "from an earlier compile. Force a recompile " +
            `(${toolHintReference("unity_open_mcp_recompile_scripts")}) and ` +
            "re-read before acting on them."
          : status === "indeterminate" && prevLogRead
            ? "No errors found, but this is NOT a verified clean bill of " +
              `health: the read came from the ROTATED log (${resolvedLog.reason === "prev_log_live_editor" ? "Editor-prev.log chosen because a live editor holds the project" : "Editor-prev.log fallback"}) ` +
              "while a live Editor may be writing a different log. Confirm via " +
              "the live bridge (unity_open_mcp_editor_status / a live tool " +
              "probe) or re-read after the Editor settles."
            : status === "indeterminate"
              ? "No current compiler errors in Editor.log" + historicalNote(classified) +
                ", but this is NOT a verified clean compile: an offline read has no " +
                "compile generation behind it, and the log can predate your edits, " +
                "come from another Editor session or miss a compile still running. " +
                indeterminateSteps[0]
              : health.headline;

    return sourceResult(
      {
        status,
        // When the errors may not apply (stale log / version mismatch), the
        // top-level verdict is downgraded to "stale_log" and unhealthy is
        // suppressed so an agent branching on `unhealthy` does not treat
        // phantom errors as a hard failure (feedback-fable-04-08 §4). The raw
        // log verdict is preserved in `logVerdict` for callers that want the
        // literal signal regardless of freshness.
        unhealthy: errorsMayNotApply ? false : health.unhealthy,
        headline,
        // How far the verdict can be trusted: `log_evidence` (errors or issues
        // the log still holds as current — no compile generation confirms them)
        // or `indeterminate` (nothing current either way). Only the live
        // CompilationPipeline answer is `current`.
        determinacy: status === "compile_failed" || status === "project_unhealthy" ? "log_evidence" : "indeterminate",
        // Raw verdict preserved whenever the top-level status was rewritten
        // (stale_log downgrade OR an indeterminate clean read), so callers can
        // still read what the log literally said.
        ...(errorsMayNotApply || status === "indeterminate" ? { logVerdict } : {}),
        ...(indeterminateSteps.length ? { agentNextSteps: indeterminateSteps } : {}),
        errorCount: errors.length,
        errors,
        // Errors the log holds that cannot describe the current compile:
        // superseded by a later successful build, a log older than the newest
        // built assembly, or a cited file/line that no longer exists. Kept as
        // evidence; they never drive `status`.
        historicalLogErrors: classified.historical,
        lastLogBuild: classified.lastBuild,
        ...(view && view.phase !== "gone" ? { editorPhase: view.phase } : {}),
        // specs/feedback.md 2026-08-12 — present only when there are errors AND
        // the project has multiple compile assemblies. Unity's dependency-ordered
        // pipeline means these errors may be a subset; fixing them lets dependent
        // assemblies compile and may surface more. Re-check until errorCount is 0.
        ...(partialCompileLikely
          ? {
              partialCompileLikely: true,
              asmdefCount: asmdefs.count,
              // Distinct assemblies the reported errors span (null when none of
              // the cited files mapped to an asmdef — e.g. global/package code).
              assembliesWithErrors: assembliesWithErrors > 0 ? assembliesWithErrors : null,
              partialCompileHint:
                "Unity compiles assemblies in dependency order; the reported errors " +
                "may be a SUBSET — once these are fixed, dependent assemblies that " +
                "were not reached compile next and may surface additional errors. " +
                "Re-check read_compile_errors after each fix until errorCount is 0.",
            }
          : {}),
        // Package / assembly issues from the same log tail. Empty when the
        // only red flags are compiler errors (the common case).
        issues: health.issues,
        issueCount: health.issues.length,
        logPath,
        // Why this log file was chosen (feedback-fable-31-07 §5). Non-default
        // values signal a log-rotation fallback: the live editor is writing to
        // Editor-prev.log after a batch spawn rotated Editor.log.
        logSource: resolvedLog.reason,
        tailBytes: tail.bytes,
        // Present ONLY when at least one cited source file is newer than
        // Editor.log — the log's most-recent error block may be stale and
        // the agent should force a recompile before trusting the errors.
        ...(staleLog
          ? {
              staleLogSuspected: true,
              staleLogHint: staleLog.hint,
              staleLogNewerFiles: staleLog.newerFiles,
            }
          : {}),
        // Present ONLY when at least one Assets/**/*.cs source is newer than
        // the newest built DLL — the running assembly predates the latest
        // source, so a no_errors_found signal cannot be trusted until the
        // assembly is rebuilt (feedback-01-08-glm §5b).
        ...(staleAsm.staleAssembly
          ? {
              staleAssembly: true,
              staleAssemblyHint: staleAsm.hint,
              staleAssemblyNewerSources: staleAsm.newerSources,
            }
          : {}),
        // specs/feedback.md 2026-08-14 — machine-readable sibling of the
        // headline caveat above. Present ONLY when errors were returned AND at
        // least one file they cite is newer than the newest built assembly, so
        // the error block cannot reflect the current source. Distinct from
        // `staleLogSuspected` (log-file mtime) and `staleAssembly` (whole
        // Assets/ scan): this names the cited files the agent is about to
        // re-read.
        ...(errorsPredateEdits
          ? {
              errorsMayPredateEdits: true,
              errorsMayPredateEditsFiles: errorsPredateEdits.files,
              errorsMayPredateEditsHint:
                "These errors were parsed from a compile that ran BEFORE the " +
                "listed files were last edited (each is newer than the newest " +
                "Library/ScriptAssemblies/*.dll, so no compile has completed " +
                "since). Do not re-read or re-fix that code on the strength of " +
                "this block — force a recompile " +
                `(${toolHintReference("unity_open_mcp_recompile_scripts")}) ` +
                "and read again.",
            }
          : {}),
        // feedback-04-08-opus §2 — log authorship. Emitted whenever the header
        // was parseable (the live editor's long tail usually has no header, so
        // these stay absent in the common case). On a version mismatch the hint
        // says the errors CANNOT all occur in the live editor; on a batch-mode
        // log it reminds the agent the errors are from a headless run.
        ...(authorship.logUnityVersion !== null || authorship.logBatchMode !== null
          ? {
              logUnityVersion: authorship.logUnityVersion,
              liveUnityVersion: authorshipCheck.liveUnityVersion,
              logBatchMode: authorship.logBatchMode,
              logDate: authorship.logDate,
              ...(authorshipCheck.hint
                ? {
                    logAuthorshipMismatch: authorshipCheck.versionMismatch,
                    logAuthorshipHint: authorshipCheck.hint,
                  }
                : {}),
            }
          : {}),
      },
      "offline",
    );
  }

  /**
   * How long read_compile_errors keeps asking a settled Editor whose
   * /compile-state did not answer (main thread busy: an unfocused Editor can
   * take ~1.5 s per main-thread hop) or answered `indeterminate` (its source
   * fingerprint is recomputed in the background right after a reload).
   */
  compileStateRetryMs = 3_000;

  /**
   * The live compile state, or — when it is not determinate — what the live
   * Editor looks like from outside (instance lock + /ping). A settled Editor
   * is asked again within {@link compileStateRetryMs}; an Editor mid-compile,
   * mid-reload or blocked is not waited for (the caller says so instead).
   * `view` is null when the state was determinate or there is no project.
   */
  private async readLiveCompileState(live: LiveClient): Promise<{ compile: Record<string, unknown> | null; view: EditorView | null }> {
    const canRead = typeof live.readCompileState === "function";
    let compile = canRead ? await live.readCompileState() : null;
    if (compile && compile.status !== "indeterminate") return { compile, view: null };
    if (!this.projectPath) return { compile, view: null };
    const look = async () => viewEditor(await readEditorSignals(this.projectPath, live, 1_000, this.globalLogPathOverride), hasRecentPendingTestRun());
    let view = await look();
    const deadline = Date.now() + this.compileStateRetryMs;
    while (canRead && view.phase === "settled" && Date.now() < deadline) {
      await sleepMs(Math.min(500, Math.max(0, deadline - Date.now())));
      compile = (await live.readCompileState()) ?? compile;
      if (compile && compile.status !== "indeterminate") return { compile, view: null };
      view = await look();
    }
    return { compile, view };
  }

  /**
   * read_compile_errors while the live Editor holds the project but gave no
   * determinate compile state: the instance lock's compile block (generation,
   * running / pending, last result) is the answer. A compile running or due is
   * `currently_compiling`; a last compile that failed stays `compile_failed`
   * (current until the next compile finishes) with the log's current
   * diagnostics; anything else is `indeterminate` — never a clean verdict.
   */
  private lockCompileAnswer(
    view: EditorView,
    compile: Record<string, unknown> | null,
    classified: ClassifiedLogErrors,
    tail: { content: string; error?: string },
    logPath: string,
    logSource: string,
  ): CallToolResult {
    const lock = view.compile!;
    const generation = lock.generation;
    const running = view.isCompiling || view.transition === "compiling" || lock.running || compile?.status === "currently_compiling";
    const pending = !running && (lock.pending || compile?.pending === true);
    const failed = !running && !pending && lock.completed && lock.failed;
    const status = running || pending ? "currently_compiling" : failed ? "compile_failed" : "indeterminate";
    const errors = failed ? classified.current : [];
    const health = summarizeProjectHealth(tail.content, this.projectPath, errors);
    const why = view.phase === "transition" ? `the Editor is ${(view.transition ?? "reloading").replace(/_/g, " ")}`
      : view.phase === "wedged" ? "its main thread is blocked (a modal dialog or a long synchronous import)"
      : view.phase === "unreachable" ? "its bridge listener is not answering"
      : compile ? "its source fingerprint is still being computed after a reload"
      : "its main thread did not answer the compile-state probe in time";
    const wait = waitCall("idle", pending ? generation : null);
    const lastCompile = lock.completed ? (lock.failed ? `failed with ${lock.errorCount} error(s)` : "succeeded") : "has not completed in this Editor session";
    const headline = running
      ? `Compilation is active (${why}); the compile state comes from the live Editor's instance lock (last generation ${generation}). Wait for it with ${wait}: it returns once the compile ran, with its status and error count.`
      : pending
        ? `A compile was requested and has not started yet (last generation ${generation}). Wait for it with ${wait}: it returns once that compile ran, with its status and error count.`
        : failed
          ? `The live Editor's last compile (generation ${generation}) failed with ${lock.errorCount} error(s); its compile state was read from the instance lock because ${why}, so the errors are the ${errors.length} current diagnostic(s) in Editor.log. Fix them and recompile.`
          : `Compile state indeterminate: the live Editor holds the project but ${why}. Its last compile (generation ${generation}) ${lastCompile}, but whether the sources changed since is unverified — this is NOT a verified clean compile. Wait with ${waitCall("idle")} (it reports the compile status and error count), then re-read.`;
    const steps = view.phase === "wedged"
      ? ["The Editor's main thread is blocked — usually a modal dialog an operator must close in the Unity UI. Check unity_open_mcp_bridge_status, then re-read."]
      : failed
        ? [`Fix the errors, then recompile (${toolHintReference("unity_open_mcp_recompile_scripts")}); it waits for the compile and reports the result.`]
        : [`Wait with ${wait}, then re-read if you need the diagnostics.`];
    return sourceResult({
      status,
      determinacy: status === "indeterminate" ? "indeterminate" : "current",
      compileSource: "instance_lock",
      generation,
      pending,
      running,
      lastCompileFailed: lock.completed ? lock.failed : null,
      unhealthy: failed,
      headline,
      agentNextSteps: steps,
      errorCount: errors.length,
      errors,
      errorSource: "Editor.log",
      historicalLogErrors: failed ? classified.historical : [...classified.current, ...classified.historical],
      issues: health.issues,
      issueCount: health.issues.length,
      editorPhase: view.phase,
      transition: view.transition,
      liveProbe: compile ? "indeterminate" : "unanswered",
      logPath,
      logSource,
      lastLogBuild: classified.lastBuild,
      logReadError: tail.error ?? null,
    }, "local");
  }

  // Resolve the live Unity Editor PID for this project from the instance lock,
  // or undefined when no live editor is known. Used by routeReadCompileErrors
  // to feed the log-rotation fallback in resolveEditorLogPath
  // (feedback-fable-31-07 §5): a frozen Editor.log + a live PID is the
  // signature of a batch spawn that rotated the log while the live editor
  // keeps writing to Editor-prev.log.
  private resolveLiveEditorPid(): number | undefined {
    if (!this.projectPath) return undefined;
    try {
      const lock = readInstanceLock(this.projectPath);
      if (lock && lock.pid && isPidAlive(lock.pid)) return lock.pid;
    } catch {
      // Unreadable/missing lock → no live PID known.
    }
    return undefined;
  }

  // feedback-04-08-opus §2 — resolve the live bridge's Unity version from the
  // instance lock, so read_compile_errors can cross-check the LOG's version
  // header against the version of the editor that is actually running. A batch
  // run of a NEWER Unity leaves a log whose errors cannot occur in the live
  // editor; without this check the tool would surface them as current. Returns
  // null when no live editor / no version recorded (the benign case — no
  // version to compare against, so no false mismatch fires).
  private resolveLiveUnityVersion(): string | null {
    if (!this.projectPath) return null;
    try {
      const lock = readInstanceLock(this.projectPath);
      if (lock && lock.pid && isPidAlive(lock.pid) && lock.unityVersion) {
        return lock.unityVersion;
      }
    } catch {
      // Unreadable/missing lock → no live version known.
    }
    return null;
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
      return sourceResult(result, "offline");
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
                  `for the live port/pid, or set ${PORT_ENV_VAR}.`,
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

    // feedback #8 — forward the caller-supplied subscriber id so each agent
    // session gets its own read cursor (a fresh subscriber starts at the
    // current high-water mark and does not replay the shared queue's backlog).
    const subscriber =
      typeof args.subscriber === "string" && args.subscriber.length > 0
        ? args.subscriber
        : undefined;

    const result = this.eventStream.pull(maxEvents, subscriber);
    return {
      content: [{ type: "text", text: JSON.stringify(result) }],
      isError: false,
    };
  }

  private async routeCapabilities(
    args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    if (["tool_name", "query", "group", "tag", "available", "active", "route", "mutating"].some(k => args[k] !== undefined)) {
      if (args.kind !== undefined && args.kind !== "tools") return localError("invalid_arguments", "Tool discovery filters require kind: tools (or omit kind).");
      const inventory = await this.fetchInventoryAndReconcile(live);
      const found = discoverTools(ALL_TOOLS, BATCH_TOOL_NAMES, this.sessionState, inventory?.tools, args, this.batch.headlessAvailable());
      return sourceResult(found, "local", !!found.error);
    }
    const kind =
      args.kind === "tools" || args.kind === "rules" || args.kind === "fixes"
        ? args.kind
        : undefined;
    const includePlanned = args.include_planned !== false;
    // specs/feedback.md 2026-08-24 — the tool surface defaults to `compact`
    // (the unfolded catalog is ~513 KB, which overflows a typical harness
    // per-result ceiling). `buildCapabilities` itself keeps returning the full
    // shape so its other caller (generate_skill) is unaffected; the fold is a
    // view applied here.
    const profile = isOutputProfile(args.profile) ? args.profile : "compact";

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
    return sourceResult(
      viewCapabilities(result, {
        profile,
        page_size: typeof args.page_size === "number" ? args.page_size : undefined,
        cursor: typeof args.cursor === "string" ? args.cursor : undefined,
      }),
      "local",
    );
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
    return sourceResult(result, "local");
  }

  private async routeGenerateSkill(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const write = args.write === true;
    // include_workflow defaults to true; explicitly passing false
    // opts out of the template+inventory merge.
    const includeWorkflow = args.include_workflow !== false;
    const rawClients = Array.isArray(args.clients) ? args.clients : [];
    // Client allowlist comes from the single-source manifest at
    // `skills/client-paths.json`. Do not add a literal union here.
    // M31-optimizations Plan 4 / T4.4 (L6) — reuse the module-level
    // `KNOWN_CLIENT_KEYS` singleton instead of allocating a fresh Set per
    // call. The Set is built once on first access and re-derived only when
    // the manifest cache is reset (tests).
    const allowedClients = getKnownClientKeys();
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
        includeWorkflow,
      });
      return sourceResult(
        {
          skill: result.skill,
          project: result.project,
          written: result.written,
        },
        "local",
      );
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
    identity: RequestIdentity,
  ): Promise<CallToolResult> {
    const action = typeof args.action === "string" ? args.action : "";
    const group = typeof args.group === "string" ? args.group.trim() : "";

    const actionKeys: Record<string, string[]> = {
      list_groups: [], reset: [], activate: ["group"], deactivate: ["group"],
      suggest: ["intent", "tags"], activate_for: ["intent", "tags"],
      invoke: ["tool_name", "arguments"], editor_search: ["search_text", "asset_type", "open_ui"],
    };
    const ignored = Object.keys(args).filter(k => k !== "action" && actionKeys[action] && !actionKeys[action].includes(k));
    if (ignored.length) return this.manageToolsError("invalid_arguments", `Keys not used by ${action}: ${ignored.join(", ")}`);
    if (action === "editor_search") {
      const query = buildEditorSearch(String(args.search_text ?? ""), args.asset_type as string | undefined);
      if (args.open_ui !== true) return sourceResult({ query, opened: false, purpose: "Human collaboration; use search_assets/find_references for structured results." }, "local");
      const result = await this.routeCore(live, "unity_open_mcp_execute_csharp", {
        code: `UnityEditor.Search.SearchService.ShowWindow(UnityEditor.Search.SearchService.CreateContext(${JSON.stringify(query)})); return ${JSON.stringify(query)};`,
        read_only: true,
      }, identity);
      // The Search window opens through a read_only snippet, which a running
      // project job refuses (the bridge cannot verify a snippet writes
      // nothing). The handoff is optional UI, so report the query instead of
      // surfacing the refusal as a failed search.
      const body = result.isError ? parseResultBody(result) : null;
      const mutation = body?.mutation as { error?: { code?: unknown; job?: unknown } | null } | undefined;
      const refusal = mutation?.error ?? (body?.error as { code?: unknown; job?: unknown } | undefined);
      if (refusal?.code === "job_busy") {
        return sourceResult({
          query, opened: false, reason: "job_busy",
          ...(refusal.job ? { job: refusal.job } : {}),
          message: "Unity Search cannot be opened while an asynchronous project command job runs. " +
            "Paste the query into Unity Search manually, or retry open_ui once the job reaches a terminal state.",
        }, "local");
      }
      result.content.push({ type: "text", text: JSON.stringify({ query, opened: !result.isError }) });
      return result;
    }
    if (action === "invoke") {
      const name = typeof args.tool_name === "string" ? args.tool_name : "";
      const refusal = this.invokeTargetRefusal(name);
      if (refusal) return refusal;
      return this.routeCore(live, name, withSchemaDefaults(ALL_TOOLS.find(t => t.name === name)!, (args.arguments ?? {}) as Record<string, unknown>), identity);
    }
    if (action === "list_groups") {
      return this.manageToolsListGroups(live);
    }

    // M31 Plan 2 / T31.2 — intent-driven actions. `suggest` is the read-only
    // path (returns recommendations without changing state); `activate_for`
    // activates the recommended set in one call. Both share the recommend
    // engine + the per-group compiled-state availability, so an agent sees
    // the same honesty about unavailable groups as `list_groups` (the
    // recommendation lists them as `unavailable` with `availableReason`
    // instead of pretending activation succeeded).
    if (action === "suggest" || action === "activate_for") {
      const intent =
        typeof args.intent === "string" ? args.intent : "";
      const tags = Array.isArray(args.tags)
        ? args.tags.filter((t): t is string => typeof t === "string")
        : [];
      // Recommend against an empty input is a structured empty result, not an
      // error — the agent gets the `list_groups` hint and can retry. This
      // matches the spec ("unmatched intent returns a structured empty
      // recommendation + hint to list_groups (no hallucinated groups)").
      const rec = recommendGroups({ intent, tags });

      if (action === "suggest") {
        return this.manageToolsSuggest(rec, live);
      }
      return this.manageToolsActivateFor(rec, live);
    }

    if (action === "reset") {
      const before = this.sessionState.activeGroups();
      this.sessionState.reset();
      await this.maybeNotifyToolListChanged(before);
      return this.manageToolsResult({
        reset: true,
        activeGroups: this.sessionState.activeGroups(),
        message:
          "Tool-group visibility restored to the default-on groups (core + " +
          "gate-and-verify). MCP clients that support listChanged will refresh " +
          "ListTools automatically.",
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
      `Unknown action '${action}'. Valid actions: list_groups, activate, ` +
        `deactivate, reset, suggest, activate_for, invoke, editor_search.`,
    );
  }

  /**
   * `unity_open_mcp_upgrade` is a live tool, but its network lookups run HERE
   * rather than inside the bridge: the bridge tool executes on the Editor main
   * thread, where a 10 s registry timeout would freeze the Editor and every
   * queued bridge request. The bridge only ever receives a resolved
   * `target_version`, and an apply that will re-pin through UPM is forwarded
   * only after the `bridge-v<version>` release tag was confirmed to exist —
   * the same preview → confirm → apply order the bridge window follows.
   */
  private async routeUpgrade(args: Record<string, unknown>, live: LiveClient): Promise<CallToolResult> {
    const forwarded: Record<string, unknown> = { ...args };
    if (forwarded.target_version === undefined) {
      try {
        forwarded.target_version = (await releaseLookup.latest()).version;
      } catch (error) {
        const detail = error instanceof Error ? error.message : String(error);
        return injectRouteMeta(
          localError("latest_version_failed", `Could not resolve the latest published version: ${detail}. Pass target_version explicitly.`),
          { route: "local" },
        );
      }
    }
    if (forwarded.dry_run === false && forwarded.update_upm !== false) {
      // Preview first: only a plan that really re-pins through UPM needs the
      // GitHub tag check (an embedded or file: bridge install skips UPM, so it
      // must also work offline).
      const preview = await live.route("unity_open_mcp_upgrade", { ...forwarded, dry_run: true });
      const output = toolOutput(preview);
      if (preview.isError || output.error !== undefined) {
        return injectRouteMeta(preview, { route: "live" });
      }
      if (output.upmEnabled === true) {
        const version = String(forwarded.target_version);
        const tag = await releaseLookup.confirmBridgeTag(version);
        if (!tag.confirmed) {
          return injectRouteMeta(
            localError("release_tag_unavailable", tag.error ?? `Release tag bridge-v${version} was not found.`),
            { route: "local" },
          );
        }
      }
    }
    return injectRouteMeta(await live.route("unity_open_mcp_upgrade", forwarded), { route: "live" });
  }

  private async routeProjectCommands(args: Record<string, unknown>, live: LiveClient): Promise<CallToolResult> {
    // `id` (describe) and `command_id` (invoke) name the same thing; agents
    // routinely send the other one. Accept it when it is the only one given,
    // with a note, and refuse two different values.
    const locatorNotes: string[] = [];
    const primary = args.action === "invoke" ? "command_id" : "id";
    const other = primary === "id" ? "command_id" : "id";
    if (args.action === "invoke" || args.action === "describe") {
      if (args[other] !== undefined && args[primary] !== undefined && args[other] !== args[primary])
        return sourceResult({ error: { code: "invalid_arguments", message: `${args.action} takes ${primary}; ${other} names a different command (${JSON.stringify(args[other])} vs ${JSON.stringify(args[primary])}). Send ${primary} only.` } }, "local", true);
      if (args[other] !== undefined) {
        if (args[primary] === undefined) locatorNotes.push(`${other} is accepted for ${args.action}; use ${primary}.`);
        args = { ...args, [primary]: args[primary] ?? args[other] };
        delete args[other];
      }
    }
    if (args.action === "invoke") {
      const allowed = ["action", "command_id", "args", "schema_version", "paths_hint", "gate", "ignore_scene_dirty", "confirm_bypass", "timeout_ms"];
      let identity: Record<string, unknown> = { id: args.command_id };
      const fail = (code: string, message: string) => injectRouteMeta(sourceResult({ projectCommand: identity, error: { code, message } }, "local", true), { route: "local" });
      const unknown = Object.keys(args).filter(k => !allowed.includes(k));
      if (!args.command_id || unknown.length)
        return fail("invalid_arguments", "invoke requires command_id and accepts command args plus transport safety options only" +
          (unknown.length ? ` (unknown: ${unknown.join(", ")}; put command parameters inside args)` : "") + ".");
      const catalog = await live.projectCommands({ action: "describe", id: args.command_id });
      if (catalog.error) return injectRouteMeta(sourceResult(catalog, "live", true), { route: "live" });
      const command = catalog.command as Record<string, any> | undefined;
      if (!command?.available || !command.invocationSupported || !command.inputSchema || !command.schemaVersion)
        return fail("command_unavailable", "Command is unavailable or this bridge does not support invocation.");
      identity = { id: command.id, schemaVersion: command.schemaVersion, declaringAssembly: command.declaringAssembly,
        declaringType: command.declaringType, lifecycle: command.lifecycle };
      if (args.schema_version !== undefined && args.schema_version !== command.schemaVersion)
        return fail("command_schema_changed", "Describe the command again before invoking its changed contract.");
      const errors = validateSchema(args.args ?? {}, command.inputSchema);
      if (errors.length) return fail("invalid_arguments",
        `Project command '${String(command.id)}' rejected its own arguments: ${errors.join("; ")}. ` +
        "These keys are the command's parameters from describe (inputSchema), passed inside args — not the transport's id/command_id.");
      // Deprecated parameter aliases are published like built-in aliases.
      const notes: string[] = [];
      const wire = wireArguments(args.args ?? {}, command.inputSchema, notes);
      const result = await live.route("unity_open_mcp_project_commands", { ...args, args: wire, schema_version: command.schemaVersion }, command.lifecycle);
      // Reload/transport failures have no bridge envelope; retain the exact catalog identity.
      result.content = result.content.map(item => {
        if (item.type !== "text") return item;
        try {
          const body = JSON.parse(item.text);
          if (body && typeof body === "object" && !Array.isArray(body) && !body.projectCommand)
            return { ...item, text: JSON.stringify({ ...body, projectCommand: identity }) };
        } catch { /* Preserve non-JSON transport diagnostics. */ }
        return item;
      });
      notes.push(...locatorNotes);
      if (notes.length) result.content.push({ type: "text", text: JSON.stringify({ deprecations: [...new Set(notes)] }) });
      return injectRouteMeta(result, { route: "live" });
    }
    const allowed = args.action === "describe" ? ["action", "id"] : ["action", "query", "tags", "group", "package", "offset", "limit"];
    if (Object.keys(args).some(k => !allowed.includes(k)) || (args.action === "describe" && !args.id))
      return sourceResult({ error: { code: "invalid_arguments", message: "describe requires an exact id only; list accepts filters and paging only." } }, "local", true);
    const body = await live.projectCommands(args);
    const result = injectRouteMeta(sourceResult(body, "live", !!body.error), { route: "live" });
    if (locatorNotes.length) result.content.push({ type: "text", text: JSON.stringify({ deprecations: locatorNotes }) });
    return result;
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

    return sourceResult(
      {
        groups,
        activeGroups: this.sessionState.activeGroups(),
        note:
          "Activate a group to add its tools to your ListTools surface; " +
          "deactivate to hide them. State is per-session and ephemeral — " +
          "it resets to the default-on groups (core + gate-and-verify) when " +
          "the MCP server restarts. Compiled-state availability (the " +
          "`available` field) reflects whether the Unity domain dependency is " +
          "compiled in; use unity_open_mcp_capabilities for the authoritative " +
          "compiled-state report.",
      },
      "local",
    );
  }

  // M31 Plan 2 / T31.2 — `suggest` handler. Read-only: returns the
  // recommendation with per-group compiled-state availability so the agent
  // sees the same honesty as `list_groups` (a recommended group whose domain
  // dependency is missing surfaces with `unavailable: true` +
  // `availableReason`, never as a silent success). Reconciles auto-activation
  // first so the `alreadyActive` flags reflect packages that came online.
  private async manageToolsSuggest(
    rec: Recommendation,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const inventory = await this.fetchInventoryAndReconcile(live);
    const compiledAvailability = this.computeCompiledAvailability(
      inventory,
      groupToTools(),
    );

    const groups = rec.groups.map((g) =>
      this.augmentRecommendation(g, compiledAvailability),
    );

    return sourceResult(
      {
        action: "suggest",
        intent: rec,
        groups,
        activeGroups: this.sessionState.activeGroups(),
        // `empty` surfaces at the top level too so agents branching on a
        // miss don't have to dig into `intent.empty`.
        empty: rec.empty,
        ...(rec.empty ? { hint: rec.hint } : {}),
        note:
          "Read-only recommendation. Pass the same intent/tags to " +
          "action=\"activate_for\" to activate the recommended groups in one " +
          "call, or activate individual ids with action=\"activate\".",
      },
      "local",
    );
  }

  // M31 Plan 2 / T31.2 — `activate_for` handler. Activates the recommended
  // set (union with the current active groups — never deactivates anything).
  // `unavailable` recommended groups are reported honestly: today's session
  // model lets an agent activate a group whose domain dependency is missing
  // (the tools appear in ListTools but error at call time), so we DO activate
  // them but the response calls out the gap via `unavailable` + the standard
  // `availableReason`. Emits `notifications/tools/list_changed` exactly once
  // when the visible set changed (idempotent when already active).
  private async manageToolsActivateFor(
    rec: Recommendation,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const groupsBeforeInventory = new Set(this.sessionState.activeGroups());
    const inventory = await this.fetchInventoryAndReconcile(live);
    const compiledAvailability = this.computeCompiledAvailability(
      inventory,
      groupToTools(),
    );

    const before = this.sessionState.activeGroups();
    const activated: string[] = [];
    const skipped: string[] = [];
    const unavailable: Array<{ id: string; reason: string | null }> = [];
    for (const g of rec.groups) {
      const changed = this.sessionState.activate(g.id);
      if (changed) {
        activated.push(g.id);
      } else {
        skipped.push(g.id);
      }
      const avail = compiledAvailability.get(g.id);
      if (avail && (avail.available === false || avail.available === null)) {
        unavailable.push({ id: g.id, reason: avail.reason });
      }
    }
    await this.maybeNotifyToolListChanged(before);

    const groups = rec.groups.map((g) =>
      this.augmentRecommendation(g, compiledAvailability),
    );

    const anyChange = activated.length > 0;
    const message = rec.empty
      ? rec.hint
      : anyChange
        ? `Activated ${activated.length} group(s) for the intent: ` +
          `${activated.join(", ")}. MCP clients that support listChanged ` +
          `will refresh ListTools automatically.`
        : `All recommended groups were already active.`;

    return sourceResult(
      {
        action: "activate_for",
        intent: rec,
        groups,
        activated,
        tools: compactDiscoverySchema(discoverTools(ALL_TOOLS, BATCH_TOOL_NAMES, this.sessionState, inventory?.tools, { groups: this.sessionState.activeGroups().filter(g => !groupsBeforeInventory.has(g)), profile: "full", page_size: 1000 }, this.batch.headlessAvailable()).tools),
        skipped,
        unavailable,
        activeGroups: this.sessionState.activeGroups(),
        changed: anyChange,
        empty: rec.empty,
        message,
        ...(unavailable.length > 0
          ? {
              unavailableNote:
                "One or more recommended groups have a missing Unity domain " +
                "dependency — their tools will appear in ListTools but error " +
                "at call time. Use unity_open_mcp_capabilities for the " +
                "authoritative compiled-state report, or install the cited " +
                "Unity package and reconcile via list_groups.",
            }
          : {}),
      },
      "local",
    );
  }

  /**
   * Fetch the live bridge inventory (or null when offline) and reconcile auto-
   * activation against it. Shared by `suggest` / `activate_for` so both see the
   * same compiled-state + auto-activated snapshot as `list_groups` without
   * duplicating the GET /tools hop.
   */
  private async fetchInventoryAndReconcile(live: LiveClient): Promise<{
    tools: ReadonlySet<string>;
  } | null> {
    const liveAvailable = await live.isLiveAvailable();
    const inventory = liveAvailable ? await live.listBridgeTools() : null;
    await this.reconcileAutoActivation(
      inventory ? { tools: inventory.tools } : null,
    );
    return inventory;
  }

  /**
   * Augment a {@link RecommendedGroup} with the session + compiled-state
   * fields the agent needs to act on it: whether it is already active, its
   * availability, and its catalog description / Unity package dependency.
   */
  private augmentRecommendation(
    g: RecommendedGroup,
    compiledAvailability: Map<string, { available: boolean | null; reason: string | null }>,
  ) {
    const entry = getGroup(g.id);
    const avail = compiledAvailability.get(g.id);
    return {
      id: g.id,
      reason: g.reason,
      matchedTags: g.matchedTags,
      ...(g.fromMutationSignal ? { fromMutationSignal: true } : {}),
      description: entry?.description ?? null,
      alreadyActive: this.sessionState.isGroupActive(g.id),
      available: avail?.available ?? null,
      availableReason: avail?.reason ?? null,
      unityPackage: entry?.unityPackage ?? null,
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
    groupsList: Record<string, readonly string[]>,
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
        // specs/feedback.md 2026-07-03 — when the bridge is reachable but the
        // group's tools are absent, EITHER the Unity package is not installed
        // OR the installed bridge binary was not built with the domain
        // extension pack. Mention both so an operator whose package IS
        // installed lands on the bridge-build fix, not a false install loop.
        reason: anyCompiledIn
          ? null
          : `Group tools are not compiled into the running bridge. Either the ` +
            `Unity package '${g.unityPackage}' is not installed, OR the ` +
            `installed bridge binary was built without the ${g.domainDefine} ` +
            `domain extension pack.`,
      });
    }
    return out;
  }

  private manageToolsResult(
    body: Record<string, unknown>,
  ): CallToolResult {
    return sourceResult(body, "local");
  }

  private manageToolsError(code: string, message: string, extra: Record<string, unknown> = {}): CallToolResult {
    return sourceResult({ error: { code, message }, ...extra }, "local", true);
  }

  /**
   * Why `manage_tools(action:"invoke")` cannot reach `name`, or null when it
   * can. Three different causes get three different answers: recursion into
   * manage_tools itself, a name that is not a tool at all (with close
   * matches), and a real tool whose group is not active (naming the group and
   * the exact activation call).
   */
  private invokeTargetRefusal(name: string): CallToolResult | null {
    if (name === "unity_open_mcp_manage_tools")
      return this.manageToolsError("invalid_arguments", "manage_tools cannot invoke itself; call unity_open_mcp_manage_tools directly with the inner action.");
    const tool = ALL_TOOLS.find(t => t.name === name);
    if (!tool) {
      const matches = closeToolNames(name, ALL_TOOLS.map(t => t.name));
      const hint = matches.length ? ` Did you mean ${matches.join(", ")}?` : " List tools with unity_open_mcp_capabilities {\"kind\":\"tools\",\"query\":\"…\"}.";
      return this.manageToolsError("unknown_tool", `'${name}' is not a tool.${hint}`, matches.length ? { closeMatches: matches } : {});
    }
    if (filterVisibleTools(ALL_TOOLS, this.sessionState).some(t => t.name === name)) return null;
    const group = groupFor(name);
    if (!group)
      return this.manageToolsError("tool_not_active", `'${name}' is not visible in this session.`);
    const activate = `unity_open_mcp_manage_tools {"action":"activate","group":"${group}"}`;
    return this.manageToolsError(
      "tool_not_active",
      `'${name}' belongs to the inactive group '${group}'. Activate it with ${activate}, then repeat this invoke.`,
      { group, agentNextSteps: [`Call ${activate}, then repeat the same manage_tools invoke.`] },
    );
  }

  /**
   * `editor_status` — the status tool, and the one wait primitive.
   *
   * Without `wait_until` it always answers: a settled bridge is asked once
   * (play / pause / compile state, active scene, dirty scenes); while the
   * Editor compiles, reloads or changes play mode — when the bridge cannot
   * answer — the status comes from the instance lock and /ping (the same
   * reader bridge_status uses) with `reloading: true`, the `transition`, the
   * last compile generation and `retryAfterMs`. Only a missing Editor or a
   * dead bridge is an error.
   *
   * With `wait_until` it blocks until the Editor is settled in that mode with
   * no compile due (see editor-state.ts#waitForEditor), inline for up to
   * WAIT_INLINE_LIMIT_MS; a longer `timeout_ms` continues as a `jobs` wait.
   */
  private async routeEditorStatus(args: Record<string, unknown>, live: LiveClient, identity: RequestIdentity): Promise<CallToolResult> {
    const until = args.wait_until as WaitUntil | undefined;
    if (until === undefined) {
      if (args.after_generation !== undefined)
        return localError("invalid_arguments", "after_generation applies to a wait: pass wait_until (\"idle\", \"playing\" or \"edit\") with it.");
      return this.editorStatusNow(args, live);
    }
    const timeoutMs = typeof args.timeout_ms === "number" ? args.timeout_ms : WAIT_DEFAULT_TIMEOUT_MS;
    const request: WaitRequest = {
      until,
      ...(typeof args.after_generation === "number" ? { afterGeneration: args.after_generation } : {}),
      timeoutMs,
    };
    const outcome = await waitForEditor({ ...request, timeoutMs: Math.min(timeoutMs, this.waitInlineLimitMs) }, this.waitDeps(live, this.projectPath));
    if (outcome.reason === "timeout" && timeoutMs > outcome.waitedMs + 1_000) {
      // Past the host-safe inline window the wait continues as a job, the
      // way a long playtest_wait does; the job's result is this wait's answer.
      const remaining = Math.max(1_000, timeoutMs - outcome.waitedMs);
      const started = await this.routeJobs({ action: "start", tool_or_command: WAIT_TOOL,
        args: { wait_until: until, ...(request.afterGeneration !== undefined ? { after_generation: request.afterGeneration } : {}), timeout_ms: remaining } }, identity);
      const job = toolResultBody(started);
      if (started.isError || typeof job.job_id !== "string") return started;
      const { body } = describeWait(request, outcome);
      delete body.error;
      return sourceResult({
        ...body,
        status: "waiting",
        job_id: job.job_id,
        job: { state: job.state, operation: job.operation },
        note: `Not ${until} after ${outcome.waitedMs} ms; the wait continues as job ${job.job_id} for up to ${remaining} ms more.`,
        agentNextSteps: [`Call unity_open_mcp_jobs {"action":"wait","job_id":"${job.job_id}","timeout_ms":30000} until the job is terminal; its result is this wait's answer (state, compile status and error count).`],
      }, "local");
    }
    const { body, isError } = describeWait(request, outcome);
    return sourceResult(body, "local", isError);
  }

  /**
   * `recompile_scripts`, and `assets_refresh` when it started a compile: the
   * compile result is the call's answer (compile-result.ts). The call runs on
   * the bridge as usual; then the editor_status wait (keyed to the call's
   * `compile.generationAtStart`) waits out the compile and its domain reload
   * — inline within what is left of the host-safe window, then as a `jobs`
   * wait — and the outcome is folded into the answer. A call that left no
   * compile, or was refused, is returned unchanged.
   */
  private async routeCompileTool(toolName: string, args: Record<string, unknown>, live: LiveClient, identity: RequestIdentity): Promise<CallToolResult> {
    const startedAt = Date.now();
    const result = injectRouteMeta(await live.route(toolName, args), { route: "live" });
    const body = toolResultBody(result);
    const due = compileToAwait(toolName, body, result.isError === true);
    if (!due) return result;
    const request: WaitRequest = {
      until: "idle",
      ...(due.generationAtStart !== undefined ? { afterGeneration: due.generationAtStart } : {}),
      timeoutMs: WAIT_MAX_TIMEOUT_MS,
    };
    // What is left of the inline window after the bridge call (at least a few
    // seconds, so a slow call still gets a short wait before the job).
    const inlineMs = Math.max(Math.min(5_000, this.waitInlineLimitMs), this.waitInlineLimitMs - (Date.now() - startedAt));
    const outcome = await waitForEditor({ ...request, timeoutMs: Math.min(request.timeoutMs, inlineMs) }, this.waitDeps(live, this.projectPath));
    if (outcome.reason === "timeout" && request.timeoutMs > outcome.waitedMs + 1_000) {
      const remaining = Math.max(1_000, request.timeoutMs - outcome.waitedMs);
      const started = await this.routeJobs({ action: "start", tool_or_command: WAIT_TOOL,
        args: { wait_until: "idle", ...(request.afterGeneration !== undefined ? { after_generation: request.afterGeneration } : {}), timeout_ms: remaining } }, identity);
      const job = toolResultBody(started);
      if (!started.isError && typeof job.job_id === "string")
        return { ...withResultBody(result, foldCompileJob(body, request, outcome, job as { job_id: string }, remaining)), isError: false };
    }
    const folded = foldCompileResult(toolName, body, request, outcome);
    return { ...withResultBody(result, folded.body), isError: folded.isError };
  }

  /** editor_status without a wait: the bridge's answer when it can give one, else the lock's. */
  private async editorStatusNow(args: Record<string, unknown>, live: LiveClient): Promise<CallToolResult> {
    const signals = await readEditorSignals(this.projectPath, live, 2_000, this.globalLogPathOverride);
    const view = viewEditor(signals, hasRecentPendingTestRun());
    const settling = view.transition !== null || view.phase !== "settled";
    const waitHint = settling ? {
      retryAfterMs: retryAfterMsFor(view),
      agentNextSteps: [`The Editor is ${view.transition ? view.transition.replace(/_/g, " ") : view.phase}. Wait for it with ${waitCall(view.transition === "entering_playmode" ? "playing" : view.transition === "exiting_playmode" ? "edit" : "idle")} instead of polling.`],
    } : {};
    if (signals.ping.reachable && signals.ping.httpStatus === 200 && signals.ping.body?.connected !== false && view.phase !== "wedged") {
      const timeoutMs = typeof args.timeout_ms === "number" ? Math.min(args.timeout_ms, 5_000) : 5_000;
      const output = await live.readEditorStatus(timeoutMs);
      if (output) {
        return sourceResult({ ...output, ...statusFields(view), source: "live", ...waitHint, _route: { route: "live" } }, "live");
      }
    }
    if (view.phase === "dead_bridge" && view.bridgeLoss) {
      // Only with Editor.log evidence (viewEditor): a stopped heartbeat alone
      // answers from the lock below as a transition / unreachable listener.
      return sourceResult({ ...bridgeCompileFailedBody(view.bridgeLoss, statusFields(view)), _route: { route: "local" } }, "local", true);
    }
    if (view.phase === "gone") {
      return sourceResult({
        error: { code: "bridge_offline", message: `No live Unity Editor for ${this.projectPath || "this project"} (no instance lock with a running process).` },
        ...statusFields(view),
        agentNextSteps: ["Open the project in Unity, then call unity_open_mcp_bridge_status to confirm the bridge is running."],
        _route: { route: "local" },
      }, "local", true);
    }
    return sourceResult({
      isPlaying: view.isPlaying,
      isCompiling: view.isCompiling,
      phase: view.phase,
      ...statusFields(view),
      source: "lock",
      note: "The bridge is not answering (" + (view.transition ? view.transition.replace(/_/g, " ") : view.phase.replace(/_/g, " ")) +
        "); this status is read from the instance lock and /ping. Scene and pause fields return once the Editor settles.",
      ...(settling ? waitHint : { retryAfterMs: 2_000 }),
      _route: { route: "local" },
    }, "local");
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
  //   no lock + /ping UNreachable + Unity proc → "dead_bridge" (M27 Plan 1,
  //                                              cold Safe Mode — reuses the
  //                                              dead_bridge status token +
  //                                              recoveryHint; the instance
  //                                              sub-object carries
  //                                              `unityProcessPid`)
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
    // The shared editor-state reader (lock + one /ping probe) — the same one
    // editor_status, ping and the wait use, so the tools cannot disagree. A
    // 503 /ping (listener up, session still initializing after a reload)
    // reads as reachable + compiling.
    const signals = await readEditorSignals(this.projectPath, live, 2_000, this.globalLogPathOverride);
    const lock: InstanceLock | null = signals.lock;
    const classification = signals.classification;
    const view = viewEditor(signals, hasRecentPendingTestRun());
    const pingBody: Partial<PingResponse> | null = signals.ping.reachable
      ? (signals.ping.body ?? (signals.ping.httpStatus === 503 ? { connected: false, compiling: true } : null))
      : null;
    const pingReachable = pingBody !== null;
    const compiling = pingReachable === true && (pingBody?.compiling === true || signals.ping.httpStatus === 503);
    const connected = pingReachable === true && pingBody?.connected === true;

    // T-fix-3 — when the ping is unreachable, distinguish "Unity is running but
    // the listener is momentarily down" (lock PID alive / reload window) from a
    // genuine "nothing there" stop. classification "reloading" covers the
    // domain-reload window; a live-PID "healthy"/"gone"-with-alive-pid lock
    // also indicates Unity is up.
    const lockPidAlive = lock !== null && isPidAlive(lock.pid);

    // M27 Plan 1 — cold Safe Mode detection. When the lock is absent
    // (classification "gone") AND the ping is unreachable, the bridge's
    // [InitializeOnLoad] may have never run at all — Unity launched straight
    // into Safe Mode (bridge assembly failed to compile from a cold start) and
    // no lock was ever written. Before this fix that state folded into the
    // generic "stopped", hiding the recovery path (read_compile_errors works
    // even with the bridge dead — it reads Editor.log offline). Scan for a live
    // Unity process whose -projectPath references this project; a match is a
    // strong positive signal of cold Safe Mode and reuses the existing
    // dead_bridge status token + recoveryHint (no new status). The scan only
    // runs in this narrow branch (no lock + unreachable) so the happy path and
    // the reload-window path never pay the ps/PowerShell cost.
    let unityProcessPid: number | null = null;
    if (classification === "gone" && !pingReachable) {
      const proc = findUnityForProject(this.projectPath);
      if (proc) unityProcessPid = proc.pid;
    }

    // specs/feedback.md 2026-08-14 — wedge detection, evaluated BEFORE the
    // existing token ladder because both wedges masquerade as one of its
    // healthy-looking outcomes.
    //
    // (a) main_thread_wedged. `dead_bridge` means "live PID + stale
    //     heartbeat". A modal dialog produces exactly that (the heartbeat is
    //     written from EditorApplication.update, which a blocked pump stops
    //     running) — but a dead bridge ASSEMBLY cannot answer /ping at all,
    //     because the listener lives in the assembly that failed to compile.
    //     So a reachable /ping alongside a stale heartbeat rules out the
    //     safe-mode diagnosis and identifies a stalled main thread instead.
    // (b) editor_fd_exhaustion. Process, heartbeat and /ping all survive a
    //     dead build driver, so nothing in the existing ladder can see it.
    //     One regex over the freshest log tail can (the same scan
    //     read_compile_errors runs), and it only runs when the ladder would
    //     otherwise report a healthy editor — the offline/stopped paths have
    //     no editor to be wedged.
    let wedge: BridgeWedge | null = null;
    if (classification === "dead_bridge" && pingReachable) {
      wedge = {
        reason: "main_thread_wedged",
        detail:
          "The instance lock's heartbeat is stale (the editor main thread stopped " +
          "ticking) but the bridge listener still answers /ping. A failed bridge " +
          "assembly would take the listener down with it, so this is a blocked main " +
          "thread — almost always a modal dialog — not Safe Mode.",
      };
    } else if (pingReachable && connected) {
      const fd = scanForFdExhaustion(
        this.projectPath,
        this.resolveLiveEditorPid(),
        this.globalLogPathOverride,
      );
      if (fd.present) {
        wedge = {
          reason: "editor_fd_exhaustion",
          detail:
            "The freshest Editor.log carries the Bee build-driver file-descriptor " +
            "exhaustion signature. The Editor answers HTTP but cannot build: " +
            "Library/ScriptAssemblies stops updating, so C# edits never take effect " +
            "and execute_csharp keeps running the previous assembly.",
          ...(fd.logPath ? { logPath: fd.logPath } : {}),
          ...(fd.raw ? { raw: fd.raw } : {}),
        };
      }
    }

    // `dead_bridge` needs Editor.log evidence that the bridge cannot come
    // back (bridge-loss.ts). A stopped heartbeat alone — a reload that runs
    // long, a test run's reload — is `unreachable` (retry), never a compile
    // failure; `classification` keeps mirroring the lock verbatim.
    const coldEvidence = !pingReachable && unityProcessPid !== null
      ? readBridgeLossEvidence(this.projectPath, { livePid: unityProcessPid, cold: true, globalLogPathOverride: this.globalLogPathOverride })
      : null;
    const bridgeLoss = view.bridgeLoss ?? coldEvidence;
    let status: BridgeStatusToken;
    if (wedge !== null) {
      status = "wedged";
    } else if (view.phase === "dead_bridge") {
      status = "dead_bridge";
    } else if (compiling) {
      status = "compiling";
    } else if (connected) {
      status = "running";
    } else if (coldEvidence !== null) {
      // M27 Plan 1 — cold Safe Mode: no lock (bridge never compiled) + a live
      // Unity process for this project + Editor.log evidence (Safe Mode, or
      // the startup compile's errors). Reuse the dead_bridge status token so
      // the existing recoveryHint + nextStep (read_compile_errors) applies
      // unchanged. Operators get a `unityProcessPid` to confirm the diagnosis.
      status = "dead_bridge";
    } else if (!pingReachable && (classification === "reloading" || classification === "dead_bridge" || lockPidAlive || unityProcessPid !== null)) {
      // Unity is running but the listener didn't respond — transient (reload
      // window) or toolbar-off. Surface as unreachable so it isn't masked by
      // a clean "stopped".
      status = "unreachable";
    } else {
      status = "stopped";
    }

    // A "stopped" bridge while this server's own headless run holds the
    // project: the process scan skips that child, so without this hint the
    // generic nextStep would send the agent to launch Unity into the run's
    // project lock.
    const headlessRun = status === "stopped" ? activeHeadlessRun(this.projectPath) : null;

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
      // What the Editor is doing (compiling / reloading / entering_playmode /
      // exiting_playmode, or null) and the last compile generation — the same
      // fields editor_status reports.
      transition: view.transition,
      compileGeneration: view.compile?.generation ?? null,
      compilePending: view.compile?.pending ?? null,
      recoveryHint: bridgeStatusRecoveryHint(status, wedge),
      // specs/feedback.md 2026-08-14 — present ONLY when the editor is wedged.
      // `classification` keeps mirroring the instance lock verbatim (healthy |
      // reloading | dead_bridge | gone) so that contract is unchanged; the
      // wedge is carried by the tool's own `status` token plus this block, and
      // `ready` is false either way.
      ...(wedge !== null ? { wedged: wedge } : {}),
      // Why the bridge cannot come back (Editor.log), present only on a
      // dead_bridge status; a stopped heartbeat without it reads unreachable.
      ...(status === "dead_bridge" && bridgeLoss ? { evidence: bridgeLoss } : {}),
      ...(view.stalledMs !== null ? { heartbeatStalledMs: view.stalledMs } : {}),
      instance: {
        lockPath: lockOnDisk,
        classification,
        lock: lock ? summarizeBridgeStatusLock(lock) : null,
        // M27 Plan 1 — PID of the live Unity process for this project, when
        // the cold-Safe-Mode scan matched one. Omitted (absent) when no scan
        // ran (happy path / reload window) or when the scan found no match.
        ...(unityProcessPid !== null ? { unityProcessPid } : {}),
      },
      ping: pingReachable
        ? {
            reachable: true,
            connected: pingBody?.connected ?? null,
            compiling: pingBody?.compiling ?? null,
            isPlaying: pingBody?.isPlaying ?? null,
            unityVersion: pingBody?.unityVersion ?? null,
            bridgeVersion: pingBody?.bridgeVersion ?? null,
            wireContract: pingBody?.wireContract ?? null,
            mode: pingBody?.mode ?? null,
            transition: pingBody?.transition ?? null,
          }
        : { reachable: false },
      // specs/feedback.md 2026-08-24 — null when /ping was unreachable; a
      // `stale: true` block is the signal to reinstall the Unity package
      // rather than file a regression.
      wireContract: wireContractBlock(pingReachable, pingBody?.wireContract),
      ...(headlessRun !== null ? { headlessRun: describeHeadlessRun(headlessRun) } : {}),
      nextStep:
        headlessRun !== null
          ? headlessRunSentence(headlessRun) +
            " The bridge stays offline until then. Wait for the pending headless call to return; " +
            "do not launch Unity for this project meanwhile."
          : bridgeStatusNextStep(status, wedge),
    };

    // bridge_status never reports an error — even a stopped bridge is a
    // successful status read. _source=local because the synthesis happens in
    // the MCP server (no bridge tool endpoint, no batch Unity).
    return sourceResult(body, "local");
  }

  // ── M31 Plan 3 — Editor fd-exhaustion operator surfaces (local-routed) ──
  //
  // restart_editor (T31.3, reactive kill) + resource_pressure (T31.4,
  // proactive prediction). Both resolve entirely inside the MCP server: they
  // act on the OS process (kill / lsof / /proc), never on the bridge. The
  // bridge is the thing that dies on fd-exhaustion, so neither tool may
  // depend on it for its primary path. restart_editor still consults the
  // bridge opportunistically for the active-scene-dirty signal — the Editor
  // is hung, but the HTTP listener might still be up long enough to answer
  // a read-only status call before the kill.

  /**
   * T31.3 — terminate the hung Unity Editor after confirming the
   * `editor_fd_exhaustion` signature is present in Editor.log. Requires
   * explicit `confirm: true`. The relaunch half is intentionally deferred
   * (the Hub owns the interactive-Editor launch recipe); the response tells
   * the operator/agent to relaunch via the Hub.
   *
   * Safety contract (the plan's "explicit confirmation" requirement):
   *   1. Refuses unless `confirm: true` is passed. A dry-run call (confirm
   *      false or absent) returns the PID + diagnosis it would act on, no
   *      side effect.
   *   2. Refuses when the fd-exhaustion signature is ABSENT from Editor.log.
   *      Never restart on a plain `dead_bridge` (usually a fixable compile
   *      failure) or a merely offline bridge.
   *   3. Surfaces unsaved-scene risk when the bridge is still reachable
   *      enough to report dirty scenes. Does NOT block the kill — the Editor
   *      is hung — but makes the risk visible in the response.
   */
  private async routeRestartEditor(
    args: Record<string, unknown>,
    live: LiveClient,
  ): Promise<CallToolResult> {
    const confirm = args.confirm === true;
    const graceMs =
      typeof args.kill_grace_ms === "number" &&
      Number.isInteger(args.kill_grace_ms) &&
      args.kill_grace_ms >= 0 &&
      args.kill_grace_ms <= MAX_KILL_WAIT_MS
        ? args.kill_grace_ms
        : DEFAULT_KILL_GRACE_MS;

    // 1. Resolve the fd-exhaustion signature from Editor.log FIRST. This is
    //    the gate: the tool must NEVER restart on a plain dead_bridge / a
    //    fixable compile failure. We reuse the same offline log read +
    //    health-issue extractor as read_compile_errors so the diagnosis is
    //    identical to what the agent already saw.
    const logPath = resolveEditorLogPath(this.projectPath, undefined, this.resolveLiveEditorPid(), this.globalLogPathOverride).path;
    const tail = readLogTail(logPath, DEFAULT_LOG_TAIL_BYTES);
    let signaturePresent = false;
    let signatureSource: "editor_log" | "live_console" | null = null;
    let logWarning: string | null = null;
    if (tail.error || !tail.exists) {
      // Without the log we cannot confirm the signature from the file. The
      // live-console fallback below may still recover it.
      logWarning = tail.error
        ? `Editor.log unreadable at ${logPath}: ${tail.error}`
        : `No Editor.log found at ${logPath}.`;
    } else {
      const issues = extractProjectHealthIssues(tail.content);
      signaturePresent = issues.some((i) => i.kind === "editor_fd_exhaustion");
      if (signaturePresent) {
        signatureSource = "editor_log";
      } else {
        logWarning =
          "The editor_fd_exhaustion signature is NOT present in the recent " +
          "Editor.log tail. The Editor may be slow/stuck for a different " +
          "reason (a fixable compile failure, a modal, an unresponsive main " +
          "thread). Re-run unity_open_mcp_read_compile_errors to confirm " +
          "the diagnosis before killing the Editor.";
      }
    }

    // feedback-fable-04-08 §2b — live-console signature fallback. When the log
    // file did not carry the signature (it may be the wrong/stale log for the
    // running session, or the bridge is still up and the Bee exception is only
    // in the live console via LogEntries), probe the bridge's console read as a
    // secondary signature source. The fd-exhaustion exception is raised into
    // the live Unity console independently of whichever Editor.log file the
    // resolver picked, so this catches the case the file read missed. The
    // bridge is usually down by full fd-exhaustion, so this is opportunistic —
    // a failure here leaves the file-based verdict standing.
    if (!signaturePresent) {
      try {
        const liveAvailable = await live.isLiveAvailable();
        if (liveAvailable) {
          const consoleResult = await live.route(
            "unity_senses_read_console",
            { max_entries: 200, type: "error", detail: "verbose" },
          );
          const consoleBody = parseResultBody(consoleResult);
          const text = this.serializeConsoleEntries(consoleBody);
          if (text) {
            const liveIssues = extractProjectHealthIssues(text);
            if (liveIssues.some((i) => i.kind === "editor_fd_exhaustion")) {
              signaturePresent = true;
              signatureSource = "live_console";
              logWarning = null;
            }
          }
        }
      } catch {
        // Bridge unreachable (expected on fd-exhaustion). The file-based
        // verdict above is the authoritative fallback.
      }
    }

    // 2. Resolve the live Unity PID. feedback #2 (2026-08-06): the instance
    //    lock's `pid` is authoritative — the bridge only writes it from the
    //    main interactive editor (it early-returns in worker/batch processes),
    //    while the process scan could return an AssetImportWorker child that
    //    shares the project path. Prefer the lock PID when it is live; fall
    //    back to the scan (which now also excludes AssetImportWorker children)
    //    only when no live lock PID exists.
    let pid: number | null = null;
    let pidSource: "instance_lock" | "process_scan" | null = null;
    const lockPid = this.resolveLiveEditorPid();
    if (lockPid !== undefined) {
      pid = lockPid;
      pidSource = "instance_lock";
    } else {
      try {
        const proc = findUnityForProject(this.projectPath);
        if (proc) {
          pid = proc.pid;
          pidSource = "process_scan";
        }
      } catch {
        pid = null;
      }
    }

    // No interactive Editor, but this server's own headless run holds the
    // project: say so (both dry-run and confirmed) instead of the misleading
    // "relaunch via the Hub" of unity_process_not_found. The scan never
    // returns that child, so it is never a kill target.
    if (pid === null) {
      const headlessRun = activeHeadlessRun(this.projectPath);
      if (headlessRun !== null) {
        return headlessRunErrorResult("restart_editor", headlessRun, this.projectPath);
      }
    }

    // 3. Dry-run branch: confirm was false/absent. Return the diagnosis +
    //    the PID the tool WOULD kill, with no side effect. Lets an agent
    //    preview before committing. feedback #2: include the PID source and
    //    the resolved process's command line so a misattribution (e.g. an
    //    AssetImportWorker child) is visible before confirm.
    if (!confirm) {
      const processCommandLine =
        pid !== null ? readProcessCommandLine(pid) : null;
      return sourceResult(
        {
          action: "restart_editor",
          confirm: false,
          dryRun: true,
          signaturePresent,
          ...(signatureSource !== null ? { signatureSource } : {}),
          ...(pid !== null ? { wouldKillPid: pid } : {}),
          ...(pidSource !== null ? { pidSource } : {}),
          ...(processCommandLine !== null ? { processCommandLine } : {}),
          ...(logWarning !== null ? { warning: logWarning } : {}),
          projectPath: this.projectPath,
          message:
            "Dry-run preview. Pass confirm: true to actually terminate the " +
            "hung Unity Editor. Only do so after read_compile_errors " +
            "confirms an editor_fd_exhaustion issue — killing the Editor " +
            "can destroy unsaved scene work and in-flight asset imports. " +
            "Verify processCommandLine targets the main editor, not an " +
            "AssetImportWorker child.",
        },
        "local",
      );
    }

    // 4. Confirmed-path refusals. Each guard surfaces a structured code so
    //    an agent can branch on why the kill did not happen.
    if (logWarning !== null && !signaturePresent) {
      return sourceResult(
        {
          error: {
            code: "restart_signature_absent",
            message: logWarning,
            logPath,
          },
        },
        "local",
        true,
      );
    }
    if (pid === null) {
      return sourceResult(
        {
          error: {
            code: "unity_process_not_found",
            message:
              "No live Unity process matches this project's path. The " +
              "Editor may have already exited, or it was launched without " +
              "-projectPath (the scanner cannot match a bare editor " +
              "launch). Relaunch Unity for this project via the Hub.",
            projectPath: this.projectPath,
          },
        },
        "local",
        true,
      );
    }

    // 5. Opportunistic active-scene-dirty check. The Editor is hung, but the
    //    bridge HTTP listener might still be up long enough to answer a
    //    read-only status call. If so, surface dirtyScenes[] as a warning so
    //    the operator knows what unsaved work the kill will lose. This does
    //    NOT block the kill — the Editor is hung, saving is not an option.
    let dirtyScenesWarning: Array<{ name: string; path: string; isDirty: boolean }> | null = null;
    try {
      const liveAvailable = await live.isLiveAvailable();
      if (liveAvailable) {
        const dirtyResult = await live.route(
          "unity_open_mcp_scene_get_dirty_summary",
          {},
        );
        const dirtyBody = parseResultBody(dirtyResult);
        if (dirtyBody && Array.isArray(dirtyBody.scenes)) {
          const dirty = (dirtyBody.scenes as Array<Record<string, unknown>>)
            .filter((s) => s.isDirty === true)
            .map((s) => ({
              name: typeof s.name === "string" ? s.name : "",
              path: typeof s.path === "string" ? s.path : "",
              isDirty: true,
            }));
          if (dirty.length > 0) dirtyScenesWarning = dirty;
        }
      }
    } catch {
      // The bridge is unreachable (expected on fd-exhaustion). Swallow — the
      // dirty-scene signal is opportunistic, not load-bearing.
    }

    // 6. Kill the Editor.
    const kill = await killEditorProcess(pid, graceMs);
    return this.restartEditorKillResponse(pid, graceMs, kill, dirtyScenesWarning, signatureSource);
  }

  /** Build the confirmed-kill response from a {@link KillResult}. */
  private restartEditorKillResponse(
    pid: number,
    graceMs: number,
    kill: KillResult,
    dirtyScenesWarning: Array<{ name: string; path: string; isDirty: boolean }> | null,
    signatureSource: "editor_log" | "live_console" | null,
  ): CallToolResult {
    if (kill.terminated) {
      return sourceResult(
        {
          action: "restart_editor",
          confirm: true,
          killed: true,
          pid: kill.pid,
          method: kill.method,
          elapsedMs: kill.elapsedMs,
          graceMs,
          ...(signatureSource !== null ? { signatureSource } : {}),
          ...(dirtyScenesWarning !== null
            ? {
                dirtyScenesWarning,
                dirtyScenesNote:
                  "These scenes had unsaved changes when the Editor was " +
                  "killed — that work is lost. Saving is not an option when " +
                  "the Editor is hung mid-build; surface this to the operator.",
              }
            : {}),
          nextSteps: [
            "Editor terminated. Relaunch Unity for this project via the Hub " +
              "(the MCP server does not own the interactive-Editor launch " +
              "recipe — the flags the Hub used at original launch are not " +
              "knowable from here).",
            "After relaunch, poll unity_open_mcp_bridge_status until it " +
              "returns status: \"running\" to confirm the bridge reconnected " +
              "and wrote a fresh instance lock.",
            "Once running, call unity_open_mcp_resource_pressure to sample " +
              "the fresh Editor's fd baseline — the leak accumulates across " +
              "domain reloads, so a fresh process starts back near zero.",
          ],
        },
        "local",
      );
    }
    // Kill failed — the Editor is still alive (or its state is unknown).
    return sourceResult(
      {
        action: "restart_editor",
        confirm: true,
        killed: false,
        pid: kill.pid,
        reason: kill.reason,
        message: kill.message,
        graceMs,
        nextSteps: [
          "The automated kill did not terminate the Editor. The operator " +
            "must force-quit Unity manually (Activity Monitor / Task " +
            "Manager / `kill -9 <pid>`).",
          kill.reason === "not_found"
            ? "The PID vanished between the scan and the kill — the Editor " +
              "may have exited on its own. Re-run unity_open_mcp_bridge_status " +
              "to confirm state before retrying."
            : "After a manual force-quit, relaunch Unity via the Hub and " +
              "poll unity_open_mcp_bridge_status until running.",
        ],
      },
      "local",
      true,
    );
  }

  /**
   * Flatten a unity_senses_read_console result body into a single string the
   * health-issue extractor can scan for the fd-exhaustion signature. The
   * console result is a JSON object with an `entries[]` array of
   * `{ message, stack, ... }`; we join message + stack so the Bee driver's
   * `Could not register to wait for file descriptor N` frame (which lives in
   * the stack trace) is reachable by {@link extractProjectHealthIssues}.
   * Returns "" when the body has no entries.
   */
  private serializeConsoleEntries(body: unknown): string {
    if (!body || typeof body !== "object") return "";
    const entries = (body as { entries?: unknown }).entries;
    if (!Array.isArray(entries)) return "";
    const parts: string[] = [];
    for (const ent of entries) {
      if (!ent || typeof ent !== "object") continue;
      const e = ent as { message?: unknown; stack?: unknown };
      if (typeof e.message === "string") parts.push(e.message);
      if (typeof e.stack === "string") parts.push(e.stack);
    }
    return parts.join("\n");
  }

  /**
   * T31.4 — sample the live Unity process's file-descriptor usage and report
   * headroom against Mono's ~1024 fd ceiling (the real trip point for the Bee
   * build-driver hang). Proactive counterpart to restart_editor: lets an agent
   * warn the operator to save + restart BEFORE the Editor hangs, while the
   * bridge is still healthy. The probe runs server-side (lsof / /proc / Handle
   * Count) and does NOT require the bridge to be reachable — the bridge is
   * what dies on fd-exhaustion.
   *
   * Records the sample in the session-scoped in-memory ring so successive
   * calls build a trend (monotonic climb = leak in progress). No disk cache.
   */
  private async routeResourcePressure(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    // 1. Resolve the PID. An explicit pid arg wins; otherwise prefer the
    //    instance-lock PID (authoritative main editor — feedback #2) and fall
    //    back to the process scan (which excludes AssetImportWorker children).
    //    The probe must NOT depend on the bridge.
    let pid: number | null = null;
    if (typeof args.pid === "number" && Number.isInteger(args.pid) && args.pid > 0) {
      pid = args.pid;
    } else {
      const lockPid = this.resolveLiveEditorPid();
      if (lockPid !== undefined) {
        pid = lockPid;
      } else {
        try {
          const proc = findUnityForProject(this.projectPath);
          pid = proc ? proc.pid : null;
        } catch {
          pid = null;
        }
      }
    }
    if (pid === null) {
      const headlessRun = activeHeadlessRun(this.projectPath);
      if (headlessRun !== null) {
        return headlessRunErrorResult("resource_pressure", headlessRun, this.projectPath);
      }
      return sourceResult(
        {
          error: {
            code: "unity_process_not_found",
            message:
              "No live Unity process matches this project's path, and no " +
              "explicit pid was supplied. resource_pressure samples the OS " +
              "process directly (it does not depend on the bridge) — open " +
              "Unity for this project first, or pass an explicit pid.",
            projectPath: this.projectPath,
          },
        },
        "local",
        true,
      );
    }

    // 2. Resolve the configured fd ceiling (default 1024, overridable per
    //    project via `.unity-open-mcp/settings.json` for runtimes whose
    //    internal limit differs from Mono's classic 1024). Read fresh —
    //    resource_pressure is called rarely, and this lets an operator change
    //    the setting without restarting the MCP server.
    const { ceiling, source: ceilingSource } = readFdCeiling(this.projectPath);

    // 3. Probe + record the sample. A failed probe still records a sample
    //    (count: null) so the trend detector sees the gap and does not
    //    interpolate across it.
    const probe = countFileDescriptors(pid);
    const count = probe.count;
    const approximate = "approximate" in probe ? probe.approximate : false;
    const partial = "partial" in probe ? probe.partial === true : false;
    // The threshold softening is about WHAT the metric counts, not about how
    // precise the number is. Only Windows HandleCount counts more than fds; a
    // timed-out lsof is imprecise but still counts real fds (a lower bound),
    // so it takes the normal warn/critical thresholds — softening it made a
    // partial 900/1024 report `ok` on the very platform this tool targets.
    const broaderThanFds = probe.method === "handle_count";
    const headroom = computeFdHeadroom(count, broaderThanFds, ceiling);
    const ts = Date.now();
    this.sessionState.recordFdSample({ ts, pid, count });
    const samples = this.sessionState.fdSamplesSnapshot();
    const trend = analyzeFdTrend(samples, ceiling);
    // Samples are per-PID (as the tool description states): after an Editor
    // restart the ring still holds the previous process's entries — report
    // only this PID's series. The trend already analyzes the latest PID only.
    const pidSamples = samples.filter((s) => s.pid === pid);

    // 4. Decide whether to raise an operator warning. Alarm on:
    //    - a real fd count at/past the ceiling (`over_ceiling` from lsof//proc):
    //      POSIX allocates the lowest free descriptor number, so every NEW
    //      descriptor now lands above the ceiling and the next IOSelector
    //      registration (any async pipe/socket — e.g. the Bee build driver)
    //      can hang the Editor. A fresh healthy Unity 6 editor sits at ~160
    //      fds; real counts past 1024 are never legitimate now that
    //      countLsofFdRows excludes the cwd/txt/mmap rows that once inflated
    //      the count.
    //    - a monotonic leak (the actionable trend signal), or
    //    - approaching the ceiling from below (warn/critical).
    //    The one downgraded case is a Windows HandleCount probe over the
    //    ceiling: handle counts cover kernel/GDI/user objects and routinely
    //    exceed 1024 on a healthy process, so it gets a `pressureNote` and
    //    alarms only on the trend. `rising` is any net-positive delta that is
    //    NOT a qualifying leak — it fires on ordinary drift, so it never
    //    alarms on its own.
    const fdMethod = probe.method;
    const probeReason = "reason" in probe ? probe.reason : null;
    const probeMessage = "message" in probe ? probe.message : null;

    // A partial (timed-out) lsof listing is a LOWER BOUND on real fds, so an
    // over-ceiling verdict from it still stands; only the Windows HandleCount
    // metric measures something broader than fds. Same predicate that picked
    // the headroom thresholds above, so the two can never disagree.
    const overCeilingRealFds = headroom.state === "over_ceiling" && !broaderThanFds;

    const shouldWarn =
      overCeilingRealFds ||
      trend.state === "leaking" ||
      headroom.state === "warn" ||
      headroom.state === "critical";

    const warningState = shouldWarn
      ? {
          level:
            headroom.state === "critical" || overCeilingRealFds
              ? "critical"
              : trend.state === "leaking"
                ? "leaking"
                : "warn",
          message: overCeilingRealFds
            ? `Editor fd count (${count}) is at or above the ${ceiling}-descriptor ` +
              `Mono IOSelector ceiling. Every newly allocated descriptor number now ` +
              `lands above the ceiling, so the next async pipe/socket registration ` +
              `(e.g. the Bee build driver's IPC) can hang the Editor mid-build` +
              (trend.state === "leaking"
                ? ` — and the count is still climbing (trend delta ${trend.delta} ` +
                  `over ${trend.sampleCount} sample(s))`
                : "") +
              `. Save scene work and restart Unity via the Hub now.`
            : headroom.state === "critical"
              ? broaderThanFds
                ? `Windows handle count is at ${Math.floor(headroom.pressureRatio * 100)}% ` +
                  `of the ${ceiling}-descriptor ceiling proxy. HandleCount covers ` +
                  `kernel/GDI/user objects — far broader than fds — so this is a soft ` +
                  `signal, not fd risk: watch the trend, and treat a monotonic climb ` +
                  `(trend.state "leaking") as the real alarm.`
                : `Editor fd usage is at ${Math.floor(headroom.pressureRatio * 100)}% of ` +
                  `the ${ceiling}-descriptor ceiling — the next domain reload is ` +
                  `likely to trip the Bee build-driver hang. Save scene work and restart ` +
                  `Unity via the Hub now, before the Editor hangs.`
              : trend.state === "leaking"
                ? headroom.state === "over_ceiling"
                  ? `Editor handle count (${count}) is ABOVE the ${ceiling} ceiling ` +
                    `proxy and climbing monotonically (leak in progress): trend ` +
                    `delta ${trend.delta} over ${trend.sampleCount} sample(s). ` +
                    `Save scene work and restart Unity via the Hub now.`
                  : `Editor fd usage is climbing monotonically across samples (leak in ` +
                    `progress): trend delta ${trend.delta} over ${trend.sampleCount} ` +
                    `sample(s). Save scene work and plan a restart before the count ` +
                    `crosses the ${ceiling}-descriptor ceiling.`
                : `Editor fd usage is at ${Math.floor(headroom.pressureRatio * 100)}% of ` +
                  `the ${ceiling}-descriptor ceiling. Monitor the trend; if it keeps ` +
                  `climbing across domain reloads, save scene work and restart Unity ` +
                  `via the Hub before the Editor hangs.`,
        }
      : null;

    // Informational note for the remaining soft case: a Windows HandleCount
    // over the ceiling proxy with no qualifying leak trend.
    const pressureNote =
      !shouldWarn && headroom.state === "over_ceiling"
        ? `Windows handle count (${count}) is above the ${ceiling} fd-ceiling ` +
          `proxy, but HandleCount covers kernel/GDI/user objects — far broader ` +
          `than Unix fds — so this is expected on a healthy process. Treat it as ` +
          `a concern ONLY if the trend becomes a monotonic leak (leaking) — ` +
          `re-sample after the next domain reload to establish one.`
        : null;

    return sourceResult(
      {
        pid,
        fdCount: count,
        fdMethod,
        approximate,
        ...(partial ? { partial } : {}),
        ceiling,
        ceilingSource,
        headroom: headroom.headroom,
        pressureRatio: headroom.pressureRatio,
        state: headroom.state,
        reliable: headroom.reliable,
        trend,
        samples: pidSamples.map((s) => ({
          ts: s.ts,
          pid: s.pid,
          count: s.count,
        })),
        sampleCount: pidSamples.length,
        launchContextCaveat: LAUNCH_CONTEXT_CAVEAT,
        ...(probeReason !== null ? { probeReason } : {}),
        ...(probeMessage !== null ? { probeMessage } : {}),
        ...(warningState !== null
          ? {
              warning: warningState,
              agentNextSteps: [
                "Surface this to the operator — the Editor is approaching " +
                  "fd-exhaustion and the bridge (the thing that dies on " +
                  "exhaustion) cannot recover on its own.",
                "If scene work is unsaved, recommend saving now while the " +
                  "bridge is still healthy.",
                "When the operator is ready, restart Unity via the Hub. " +
                  "After relaunch, call resource_pressure again to sample " +
                  "the fresh Editor's fd baseline (a fresh process starts " +
                  "back near zero).",
              ],
            }
          : {}),
        ...(pressureNote !== null ? { pressureNote } : {}),
      },
      "local",
    );
  }

  // ── M26 Plan 2 — Unity Hub control (local-routed) ────────────────
  //
  // Each of these resolves entirely inside the MCP server (no bridge tool
  // endpoint, no batch Unity) → every response is tagged `_source: "local"`.
  // Read-only members are plain JSON bodies; mutating members return the
  // gate-consistent mutation/gate/agentNextSteps envelope shape so agents
  // parse them uniformly, with gate.skipped=true because these are system-
  // level ops where the project-asset gate does not apply (paths_hint N/A).

  private async routeHubListEditors(): Promise<CallToolResult> {
    const editors = this.hubBackend.listInstalledEditors();
    return sourceResult(
      { editors, count: editors.length },
      "local",
    );
  }

  private async routeHubAvailableReleases(): Promise<CallToolResult> {
    const result = await this.hubBackend.fetchAvailableReleases();
    return sourceResult(
      {
        entries: result.entries,
        count: result.entries.length,
        stale: result.stale,
        fetchedAt: result.fetchedAt,
      },
      "local",
    );
  }

  private async routeHubInstallEditor(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const version = typeof args.version === "string" ? args.version : "";
    const changeset = typeof args.changeset === "string" ? args.changeset : undefined;
    if (!version.trim()) {
      return localError("missing_parameter", "version is required.");
    }
    const res = this.hubBackend.openInstallDeepLink(version, changeset);
    return hubMutationEnvelope({
      success: res.opened,
      output: { deepLink: res.deepLink, version, changeset: changeset ?? null },
      error: res.error,
      nextSteps: res.opened
        ? [
            "Unity Hub opened at the install dialog for " +
              version +
              ". The download runs inside the Hub (watch its progress UI).",
            "There is no in-call completion detection — poll " +
              "unity_open_mcp_hub_list_editors after the Hub finishes to confirm the new editor.",
          ]
        : [
            "Ensure Unity Hub is installed and registered as the unityhub:// handler, then retry.",
            "Fallback: open the release-notes URL from hub_available_releases in a browser and install manually.",
          ],
      isError: !res.opened,
    });
  }

  private async routeHubInstallModules(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const version = typeof args.version === "string" ? args.version : "";
    const changeset = typeof args.changeset === "string" ? args.changeset : undefined;
    const modules = Array.isArray(args.modules)
      ? args.modules.filter((m): m is string => typeof m === "string")
      : [];
    if (!version.trim()) {
      return localError("missing_parameter", "version is required.");
    }
    const res = this.hubBackend.openInstallDeepLink(version, changeset);
    return hubMutationEnvelope({
      success: res.opened,
      output: {
        deepLink: res.deepLink,
        version,
        changeset: changeset ?? null,
        modulesRequested: modules,
        note: "The unityhub:// scheme does not expose a module-specific deep link; the Hub opens at the version's install dialog where the operator selects the modules.",
      },
      error: res.error,
      nextSteps: res.opened
        ? [
            `Unity Hub opened at the install dialog for ${version}. Select the requested modules (${modules.join(", ") || "none specified"}) in the Hub UI.`,
            "Poll unity_open_mcp_hub_list_editors (which scans Data/PlaybackEngines) afterwards to confirm the new modules.",
          ]
        : [
            "Ensure Unity Hub is installed and registered as the unityhub:// handler, then retry.",
          ],
      isError: !res.opened,
    });
  }

  private async routeHubGetInstallPath(): Promise<CallToolResult> {
    const res = this.hubBackend.getInstallPath();
    return sourceResult(
      { path: res.path, source: res.source, error: res.error },
      "local",
      res.error !== null,
    );
  }

  private async routeHubSetInstallPath(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const path = typeof args.path === "string" ? args.path : "";
    if (!path.trim()) {
      return localError("missing_parameter", "path is required.");
    }
    const res = this.hubBackend.setInstallPath(path);
    return hubMutationEnvelope({
      success: res.success,
      output: { path, hubCliOutput: res.output },
      error: res.error,
      nextSteps: res.success
        ? [`Default Unity Editor install path set to ${path}.`]
        : [
            "Set the UNITY_HUB_PATH env var to the Unity Hub binary if the CLI was not found, then retry.",
          ],
      isError: !res.success,
    });
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
      logRoute("[unity-open-mcp] Route: find_references -> live");
      // The live bridge only honors max_results (no detail/per-file/threshold).
      // Ask for the full set when paging so the cursor can walk it; otherwise
      // honor the legacy max_results cap. The 0 here is the server-internal
      // "unlimited" sentinel (documented in the find-references schema); callers
      // never pass it — the schema declares minimum: 1 for caller-facing use.
      const liveArgs: Record<string, unknown> = {
        ...args,
        max_results: wantPaging ? 0 : (typeof args.max_results === "number" ? args.max_results : 100),
      };
      const result = await live.route("unity_open_mcp_find_references", liveArgs);
      // M31-optimizations Plan 1 / H2 — fold + tag _source=live + stamp _route
      // in ONE parse + ONE stringify. Previously this was three cycles:
      // foldFindReferencesLive → tagSource → injectRouteMeta. T6.1's _source
      // contract is preserved (clients switching on _source still see "live").
      return transformLiveResult(
        result,
        "live",
        { route: "live" },
        (body) => foldFindReferencesLiveBody(body, profile, wantPaging, args),
      );
    }

    logRoute(
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
        // 0 is the server-internal "unlimited" sentinel (see find-references
        // schema description); the schema's caller-facing minimum is 1.
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
      return sourceResult(withPaging, "offline");
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
      logRoute("[unity-open-mcp] Route: dependencies -> live");
      // The live tool honors asset_path/guid/detail/max_results only.
      const liveArgs: Record<string, unknown> = {
        asset_path: assetPath,
        guid,
        detail,
        max_results: typeof args.max_results === "number" ? args.max_results : 100,
      };
      const result = await live.route("unity_open_mcp_dependencies", liveArgs);
      // M31-optimizations Plan 1 / H2 — tag _source=live + stamp _route in ONE
      // parse + ONE stringify. Previously this was two cycles:
      // tagSource → injectRouteMeta. T6.1's _source=live contract preserved.
      return transformLiveResult(result, "live", { route: "live" });
    }

    logRoute(
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
    // M31-optimizations Plan 1 / H2 — defer the _route stamp to the single-
    // parse fold below (saves one parse+stringify per call vs. the previous
    // injectRouteMeta → parseResultBody → withResultBody chain).
    const canBatch = this.batch.isBatchTool(toolName);
    let raw: CallToolResult;
    let routeMeta: RouteMeta;
    if (!canBatch) {
      raw = await live.route(toolName, args);
      routeMeta = { route: "live" };
    } else {
      const liveAvailable = await live.isLiveAvailable();
      if (liveAvailable) {
        raw = await live.route(toolName, args);
        routeMeta = { route: "live" };
      } else {
        raw = await this.batch.route(toolName, args);
        routeMeta = { route: "batch", fallbackReason: "live_unavailable" };
      }
    }

    // feedback-fable-04-08 §7 — rewrite the generic live_unavailable to
    // editor_busy when the batch hit a live editor's project lock.
    routeMeta = this.rewriteEditorBusy(raw, routeMeta);

    const body = parseResultBody(raw);
    if (body === null) {
      // Body not parseable — fall back to the legacy injectRouteMeta path so
      // _route is still stamped (matches pre-change behavior on this edge).
      return injectRouteMeta(raw, routeMeta);
    }
    if (body.error) {
      return injectRouteMeta(raw, routeMeta);
    }

    // Stamp _route on the parsed body BEFORE folding so the key order matches
    // the pre-change chain (injectRouteMeta ran first, then foldVerifyResult
    // spread its result on top). foldVerifyResult's spreads (`...rest` /
    // `...result`) carry `_route` through unchanged, preserving byte-identical
    // output. Single stringify via withResultBody below.
    body._route = routeMeta;
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
    // M3 — when paging, request the full hierarchy from the bridge. The bridge
    // clamps max_nodes to >= 1 (ScenesTools.cs:387), so 0 is NOT an unlimited
    // sentinel there; without this override the default 200 would cap the
    // flattened stream and paging could never walk past node 200 even once the
    // field path is fixed. Use a large explicit cap so paging can walk the
    // whole tree; applyPaging enforces the per-page size.
    if (wantPaging) {
      bridgeArgs.max_nodes = SCENE_PAGING_MAX_NODES;
    }

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
        // feedback-fable-04-08 §7 — rewrite to editor_busy when the batch hit
        // a live editor's project lock. Compute the meta from the raw result
        // (which carries the editor_instance_locked error) before stamping.
        const meta = this.rewriteEditorBusy(raw, {
          route: "batch",
          fallbackReason: "live_unavailable",
        });
        raw = injectRouteMeta(raw, meta);
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
  // M3 — the bridge nests the hierarchy under `body.scene` (ScenesTools.cs:
  // 393-417 emits `{"status":"ok","scene":{...,"roots":[...],"truncated":N}}`),
  // so the structural fields live on `body.scene`, not `body`. The previous
  // destructure looked at the top level and silently dropped the bridge's
  // `truncated` count; we now pull it from `body.scene` and re-attach it on
  // the page so callers can tell paging-truncated (resumable via next_cursor)
  // from bridge-truncated (the source cap was hit).
  //
  // B-N20 — the previous rewrite built a fresh `{status, scene: {...}}` object,
  // silently discarding every OTHER top-level field. The most important of
  // those is `_route` (stamped at :2409/2414/2417 to signal live vs batch
  // fallback), which docs/api/routing-lifecycle.md documents as the agent's
  // data-origin signal: a paging agent would lose the live-vs-batch-fallback
  // indication whenever `page_size` is supplied, while the unpaged response
  // still carries it. Spread the rest of `body` (everything except `scene`)
  // so the page matches the unpaged response's top-level shape.
  const scene = (body.scene && typeof body.scene === "object")
    ? body.scene as Record<string, unknown>
    : {};
  const { roots, rootGameObjects, nodes, truncated, moreHidden, pagination: _existing, ...sceneMeta } = scene;
  void roots;
  void rootGameObjects;
  void nodes;
  // `truncated` is read at the source-truncated carry-through below, so it is
  // not voided with the other intentionally-discarded scene fields.
  void moreHidden;
  void _existing;

  // Pull the top-level meta (everything except `scene`, which we rebuild).
  // B-N21 — strip the structural fields from the TOP level too. For a
  // pre-unwrapped body (roots/rootGameObjects at the top level, no `scene`
  // wrapper — the fallback flattenSceneBody handles), `scene` above resolves
  // to {} and strips nothing, so spreading the raw remainder of `body` used
  // to carry the ENTIRE unpaged roots array into the response alongside the
  // page — paging never bounded the payload for that shape. flattenSceneBody
  // already consumed those fields; drop them wherever they live.
  const {
    scene: _s,
    roots: topRoots,
    rootGameObjects: topRootGameObjects,
    nodes: topNodes,
    truncated: topTruncated,
    moreHidden: topMoreHidden,
    pagination: _topExisting,
    ...topMeta
  } = body;
  void _s;
  void topRoots;
  void topRootGameObjects;
  void topNodes;
  void topMoreHidden;
  void _topExisting;

  const result: Record<string, unknown> = {
    ...topMeta,
    status: body.status ?? "ok",
    scene: {
      ...sceneMeta,
      nodes: page,
      pagingNote: "page_size returns a flattened node stream; re-read without page_size for the structural root/hierarchy view",
    },
  };
  // Carry the bridge's source-side truncation count through to the page so an
  // agent can distinguish "more pages" (pagination.truncated) from "the bridge
  // capped the hierarchy" (scene.truncated). Both can be non-zero at once.
  // For a pre-unwrapped body the count lives at the top level (B-N21).
  const sourceTruncated = typeof truncated === "number" ? truncated : topTruncated;
  if (typeof sourceTruncated === "number" && sourceTruncated > 0) {
    (result.scene as Record<string, unknown>).truncated = sourceTruncated;
  }

  return attachPagination(result, block);
}

/**
 * Flatten the scene body into a node stream for paging. Handles both the
 * summary root roster (array of root objects) and the nested normal/verbose
 * hierarchy (objects with `children`).
 */
function flattenSceneBody(body: Record<string, unknown>): Record<string, unknown>[] {
  const out: Record<string, unknown>[] = [];
  // Rows are flat (no nested `children`), so each carries its hierarchy
  // `path` — otherwise same-named nodes on a page cannot be told apart or
  // addressed once a depth limit or truncation desyncs `childCount`.
  const walk = (node: unknown, parentPath: string | undefined) => {
    if (!node || typeof node !== "object") return;
    const obj = node as Record<string, unknown>;
    const { children, ...flatNode } = obj;
    const name = typeof obj.name === "string" ? obj.name : undefined;
    const path = typeof obj.path === "string"
      ? obj.path
      : name === undefined ? undefined : parentPath === undefined ? name : `${parentPath}/${name}`;
    if (path !== undefined && flatNode.path === undefined) flatNode.path = path;
    out.push(flatNode);
    if (Array.isArray(children)) {
      for (const child of children) walk(child, path);
    }
  };

  // M3 — the bridge nests everything one level down under `body.scene`
  // (`{"status":"ok","scene":{...,"roots":[...]}}`, ScenesTools.cs:393-417).
  // The previous code read `body.roots` / `body.rootGameObjects` directly, so
  // `rootField` was always undefined and paging returned an empty node list
  // while claiming completeness. Read from `body.scene` first, then fall back
  // to the top level for any caller that hands us a pre-unwrapped body.
  const scene = (body.scene && typeof body.scene === "object")
    ? body.scene as Record<string, unknown>
    : body;
  // The bridge emits `roots` (summary) or `rootGameObjects` (normal/verbose);
  // accept either.
  const rootField = Array.isArray(scene.roots) ? scene.roots : scene.rootGameObjects;
  if (Array.isArray(rootField)) {
    for (const root of rootField) walk(root, undefined);
  }
  return out;
}

// M3 — large explicit cap forwarded to the bridge when paging so the
// flattened stream is not truncated at the default 200 before paging even
// starts. applyPaging enforces the per-page size; this only lifts the
// source-side ceiling.
const SCENE_PAGING_MAX_NODES = 100_000;

/**
 * Add `typedToolHints` to the result's JSON body (the first text block), or as
 * its own block when the body is not a JSON object. Advisory only: isError and
 * every other field are left as they were.
 */
function attachTypedToolHints(result: CallToolResult, hints: string[]): void {
  if (!hints.length) return;
  const first = result.content[0];
  if (first?.type === "text") {
    try {
      const body = JSON.parse(first.text);
      if (body && typeof body === "object" && !Array.isArray(body)) {
        result.content[0] = { ...first, text: JSON.stringify({ ...body, typedToolHints: hints }) };
        return;
      }
    } catch { /* not JSON: fall through */ }
  }
  result.content.push({ type: "text", text: JSON.stringify({ typedToolHints: hints }) });
}
