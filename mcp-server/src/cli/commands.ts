// M15 T6.1 — CLI command implementations.
//
// Each command is a plain async function returning a CliCommandResult. The
// dispatcher (cli.ts) owns stdout/stderr/exit-code; the commands are pure and
// therefore unit-testable. Commands always produce JSON-shaped data; the
// `--json` flag toggles whether the dispatcher prints that JSON verbatim or
// renders a human-readable summary.
//
// Commands share the same router stack the MCP server uses (buildRouterStack),
// so a `run-tool` call returns byte-for-byte the same JSON an MCP client would
// receive — that parity is the primary acceptance criterion of T6.1.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { RouterStack } from "../routers.js";
import { ALL_TOOLS } from "../tools/index.js";
import {
  PORT_ENV_VAR,
  PROJECT_PATH_ENV_VAR,
  bridgeBaseUrl,
} from "../constants.js";
import { withSchemaDefaults } from "../schema-defaults.js";
import {
  readInstanceLock,
  classifyInstance,
  lockPath,
  type InstanceClassification,
  type InstanceLock,
} from "../instance-discovery.js";
import {
  pollUntilReady,
  singlePing,
  DEFAULT_WAIT_TIMEOUT_MS,
  DEFAULT_POLL_INTERVAL_MS,
  PING_FETCH_TIMEOUT_MS,
  type PollOutcome,
  type PingBody,
} from "./ping-poller.js";
import { checkBridgeCompat } from "../compat.js";
import {
  EXIT,
  classifyBySeverity,
  severityThreshold,
  withTimeout,
  type SeverityCounts,
} from "./exit-codes.js";
// M31 Plan 6 / T6.6 — light help/version formatters live in their own module
// so the CLI fast paths can load them without pulling ALL_TOOLS (this file is
// heavy). Imported as `*Impl` and re-exported under the canonical names below.
import {
  helpText as helpTextImpl,
  versionText as versionTextImpl,
} from "./help-text.js";

export interface CliCommandResult {
  /** Process exit code. 0 = success, non-zero = failure. */
  exitCode: number;
  /** JSON-serializable payload. Always populated — `--json` prints it verbatim. */
  json: unknown;
  /** Human-readable multi-line summary; printed when --json is NOT set. */
  human: string;
  /** Optional structured error label (e.g. "unknown_tool") surfaced in JSON. */
  errorLabel?: string;
}

const TOOL_BY_NAME = new Map(ALL_TOOLS.map((t) => [t.name, t]));

// ---------------------------------------------------------------------------
// ping
// ---------------------------------------------------------------------------

export interface PingCommandOptions {
  json: boolean;
  timeoutMs: number;
}

export async function runPingCommand(
  stack: RouterStack,
  opts: PingCommandOptions,
): Promise<CliCommandResult> {
  const poll = await singlePing(stack.live, opts.timeoutMs);
  const json = {
    command: "ping",
    baseUrl: bridgeBaseUrl(stack.port),
    status: poll.status,
    ready: poll.status === "ready",
    body: poll.body,
  };

  if (poll.status === "ready" && poll.body) {
    return {
      exitCode: 0,
      json,
      human: formatPingHuman(stack.port, poll.body),
    };
  }

  const reason =
    poll.status === "compiling"
      ? "Bridge is reachable but Unity is compiling."
      : poll.status === "offline"
        ? `Bridge is not reachable at ${bridgeBaseUrl(stack.port)}.`
        : `Ping failed (${poll.status}).`;
  return {
    exitCode: 1,
    json,
    human: reason,
    errorLabel: poll.status,
  };
}

function formatPingHuman(port: number, body: PingBody): string {
  const lines = [
    `Bridge: ${bridgeBaseUrl(port)}`,
    `connected: ${body.connected ?? "unknown"}`,
    `compiling: ${body.compiling ?? "unknown"}`,
    `isPlaying: ${body.isPlaying ?? "unknown"}`,
  ];
  if (body.unityVersion) lines.push(`unityVersion: ${body.unityVersion}`);
  if (body.bridgeVersion) lines.push(`bridgeVersion: ${body.bridgeVersion}`);
  if (body.projectPath) lines.push(`projectPath: ${body.projectPath}`);
  if (body.mode) lines.push(`mode: ${body.mode}`);
  return lines.join("\n");
}

// ---------------------------------------------------------------------------
// wait-for-ready
// ---------------------------------------------------------------------------

export interface WaitForReadyCommandOptions {
  json: boolean;
  timeoutMs: number;
  intervalMs: number;
}

export async function runWaitForReadyCommand(
  stack: RouterStack,
  opts: WaitForReadyCommandOptions,
): Promise<CliCommandResult> {
  const outcome: PollOutcome = await pollUntilReady(
    stack.live,
    stack.projectPath,
    (live) => singlePing(live, PING_FETCH_TIMEOUT_MS),
    {
      timeoutMs: opts.timeoutMs,
      intervalMs: opts.intervalMs,
    },
  );

  const json = {
    command: "wait-for-ready",
    ready: outcome.ready,
    status: outcome.status,
    elapsedMs: outcome.elapsedMs,
    reason: outcome.reason,
    lastPing: outcome.lastPing,
  };

  return {
    exitCode: outcome.ready ? 0 : 1,
    json,
    human: outcome.reason,
    errorLabel: outcome.ready ? undefined : outcome.status,
  };
}

// ---------------------------------------------------------------------------
// status
// ---------------------------------------------------------------------------

export interface StatusCommandOptions {
  json: boolean;
}

export async function runStatusCommand(
  stack: RouterStack,
  _opts: StatusCommandOptions,
): Promise<CliCommandResult> {
  const lockPathOnDisk = lockPath(stack.projectPath);
  const lock = readInstanceLock(stack.projectPath);
  const classification: InstanceClassification = classifyInstance(lock);

  // Cheap readiness probe — one /ping, no compile-wait.
  const poll = await singlePing(stack.live, PING_FETCH_TIMEOUT_MS);

  // Advisory version-compat between this CLI/server and the running bridge.
  // Computed from the bridge's reported bridgeVersion; ok=false means the pair
  // is considered incompatible (pre-1.0: minor differs). The status command
  // never hard-fails on this — it just surfaces the line so operators see drift.
  const compat =
    poll.body?.bridgeVersion !== undefined && poll.body?.bridgeVersion !== null
      ? checkBridgeCompat(String(poll.body.bridgeVersion))
      : null;

  const json = {
    command: "status",
    projectPath: stack.projectPath,
    port: stack.port,
    baseUrl: bridgeBaseUrl(stack.port),
    authTokenDiscovered: stack.authToken !== undefined,
    instance: {
      lockPath: lockPathOnDisk,
      classification,
      lock: lock ? summarizeLock(lock) : null,
    },
    bridge: {
      status: poll.status,
      ready: poll.status === "ready",
      body: poll.body,
    },
    compat: compat
      ? {
          ok: compat.ok,
          serverVersion: compat.serverVersion,
          bridgeVersion: compat.bridgeVersion,
          message: compat.message,
        }
      : null,
  };

  return {
    exitCode: 0,
    json,
    human: formatStatusHuman(json),
  };
}

function summarizeLock(lock: InstanceLock) {
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

function formatStatusHuman(json: {
  projectPath: string;
  port: number;
  baseUrl: string;
  authTokenDiscovered: boolean;
  instance: { classification: InstanceClassification; lock: unknown };
  bridge: { status: string; ready: boolean; body: PingBody | null };
  compat: { ok: boolean; serverVersion: string; bridgeVersion: string; message: string } | null;
}): string {
  const lines = [
    `Project:   ${json.projectPath}`,
    `Bridge:    ${json.baseUrl}`,
    `Auth:      ${json.authTokenDiscovered ? "token discovered (sent as Bearer)" : "no token (authMode must be 'none')"}`,
    `Instance:  ${json.instance.classification}`,
    `Bridge:    ${json.bridge.status}${json.bridge.ready ? " (ready)" : ""}`,
  ];
  const body = json.bridge.body;
  if (body) {
    if (body.unityVersion) lines.push(`Unity:    ${body.unityVersion}`);
    if (body.bridgeVersion) lines.push(`Bridge ver: ${body.bridgeVersion}`);
    if (body.compiling) lines.push(`State:     compiling`);
    if (body.isPlaying) lines.push(`Playmode:  playing`);
  }
  if (json.compat) {
    const tag = json.compat.ok
      ? json.compat.message
        ? "ok (drift)"
        : "ok"
      : "WARN";
    lines.push(
      `Compat:   ${tag} (server ${json.compat.serverVersion} / bridge ${json.compat.bridgeVersion})`,
    );
  }
  return lines.join("\n");
}

// ---------------------------------------------------------------------------
// run-tool
// ---------------------------------------------------------------------------

export interface RunToolCommandOptions {
  json: boolean;
  toolName: string;
  /** Raw caller args (before schema defaults). */
  toolArgs: Record<string, unknown>;
}

export async function runRunToolCommand(
  stack: RouterStack,
  opts: RunToolCommandOptions,
): Promise<CliCommandResult> {
  const tool = TOOL_BY_NAME.get(opts.toolName);
  if (!tool) {
    const json = {
      command: "run-tool",
      tool: opts.toolName,
      error: {
        code: "unknown_tool",
        message: `Unknown tool '${opts.toolName}'.`,
        available: ALL_TOOLS.map((t) => t.name),
      },
    };
    return {
      exitCode: 2,
      json,
      human:
        `Unknown tool '${opts.toolName}'.\n` +
        `Available tools (${ALL_TOOLS.length}):\n` +
        wrapList(ALL_TOOLS.map((t) => t.name)),
      errorLabel: "unknown_tool",
    };
  }

  // Apply the same schema-default injection the MCP server's CallTool handler
  // does (index.ts). Without this, a CLI caller omitting timeout_ms would get
  // a different default than an MCP client — parity requires it.
  const routedArgs = withSchemaDefaults(tool, opts.toolArgs);

  const result: CallToolResult = await stack.router.route(
    opts.toolName,
    routedArgs,
  );

  const body = extractResultBody(result);
  const isError = result.isError === true;

  const json = {
    command: "run-tool",
    tool: opts.toolName,
    isError,
    result: body,
  };

  const human = formatRunToolHuman(opts.toolName, isError, body);
  return {
    exitCode: isError ? 1 : 0,
    json,
    human,
    errorLabel: isError ? "tool_error" : undefined,
  };
}

function extractResultBody(
  result: CallToolResult,
): unknown {
  const first = result.content[0];
  if (!first || first.type !== "text" || typeof first.text !== "string") {
    return result;
  }
  try {
    return JSON.parse(first.text);
  } catch {
    return first.text;
  }
}

function formatRunToolHuman(toolName: string, isError: boolean, body: unknown): string {
  const head = `${toolName}: ${isError ? "ERROR" : "ok"}`;
  const bodyStr =
    typeof body === "string" ? body : JSON.stringify(body, null, 2);
  return `${head}\n${bodyStr}`;
}

function wrapList(items: string[], perLine = 4): string {
  const out: string[] = [];
  for (let i = 0; i < items.length; i += perLine) {
    out.push("  " + items.slice(i, i + perLine).join(", "));
  }
  return out.join("\n");
}

// ---------------------------------------------------------------------------
// stream-events
// ---------------------------------------------------------------------------
//
// Drains the per-process SSE subscription (the same one
// unity_senses_pull_events uses) and prints incremental console + editor-state
// events. `--follow` keeps polling until the process is interrupted (Ctrl-C),
// which is the CI logging shape. Without --follow it drains once and exits.

export interface StreamEventsCommandOptions {
  json: boolean;
  maxEvents: number;
  follow: boolean;
  /** Poll interval in ms when following. Defaults to 1s. */
  intervalMs?: number;
}

export async function runStreamEventsCommand(
  stack: RouterStack,
  opts: StreamEventsCommandOptions,
): Promise<CliCommandResult> {
  const intervalMs = opts.intervalMs ?? DEFAULT_POLL_INTERVAL_MS;
  const allEvents: unknown[] = [];
  let firstPull: unknown = null;
  let connected = false;
  let lastError: string | null = null;

  // First pull starts the subscription; subsequent pulls drain incrementally.
  const pullOnce = async (maxEvents: number): Promise<unknown> => {
    const result = await stack.router.route("unity_senses_pull_events", {
      max_events: maxEvents,
    });
    const body = extractResultBody(result);
    return body;
  };

  firstPull = await pullOnce(opts.maxEvents);
  const firstBatch = extractEvents(firstPull);
  connected = extractConnected(firstPull);
  lastError = extractLastError(firstPull);
  for (const evt of firstBatch) allEvents.push(evt);

  if (opts.follow) {
    // Keep draining until interrupted. The dispatcher's stdout writes happen
    // per-batch in JSON-lines shape so a CI log shows events as they arrive.
    let moreToRead = true;
    while (moreToRead) {
      await sleep(intervalMs);
      const batch = await pullOnce(opts.maxEvents);
      connected = extractConnected(batch);
      lastError = extractLastError(batch);
      const events = extractEvents(batch);
      for (const evt of events) allEvents.push(evt);
      // In follow mode the loop only ends on interruption (Ctrl-C → SIGINT →
      // process.exit from the dispatcher). We do not break on bridge disconnect
      // because the SSE reader auto-reconnects; surface the state in output.
    }
  }

  const json = {
    command: "stream-events",
    connected,
    lastError,
    eventCount: allEvents.length,
    events: allEvents,
  };

  // stream-events never fails the CI gate on event contents — it is a log
  // tap. It exits non-zero only when the bridge was never reachable.
  const unreachable = !connected && allEvents.length === 0 && lastError !== null;
  const exitCode = unreachable ? EXIT.TIMEOUT : EXIT.SUCCESS;

  return {
    exitCode,
    json,
    human: formatStreamEventsHuman(json),
    errorLabel: unreachable ? "bridge_unavailable" : undefined,
  };
}

function extractEvents(body: unknown): unknown[] {
  if (body && typeof body === "object" && Array.isArray((body as { events?: unknown }).events)) {
    return (body as { events: unknown[] }).events;
  }
  return [];
}

function extractConnected(body: unknown): boolean {
  if (body && typeof body === "object") {
    return (body as { connected?: boolean }).connected === true;
  }
  return false;
}

function extractLastError(body: unknown): string | null {
  if (body && typeof body === "object") {
    const le = (body as { lastError?: string | null }).lastError;
    return typeof le === "string" ? le : null;
  }
  return null;
}

function formatStreamEventsHuman(json: {
  connected: boolean;
  lastError: string | null;
  eventCount: number;
  events: unknown[];
}): string {
  const lines = [
    `connected: ${json.connected}`,
    `events: ${json.eventCount}`,
  ];
  if (json.lastError) lines.push(`lastError: ${json.lastError}`);
  for (const evt of json.events) {
    const e = evt as Record<string, unknown>;
    if (typeof e === "object" && e !== null) {
      const type = e.type ?? "event";
      if (type === "log") {
        lines.push(`[log ${e.logType ?? "log"}] ${e.message ?? ""}`);
      } else if (type === "editor_state") {
        lines.push(
          `[state] ${e.state ?? ""}${e.isCompiling === true ? " (compiling)" : ""}${e.isPlaying === true ? " (playing)" : ""}`,
        );
      } else {
        lines.push(`[${type}] ${JSON.stringify(e)}`);
      }
    } else {
      lines.push(String(evt));
    }
  }
  return lines.join("\n");
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// ---------------------------------------------------------------------------
// verify
// ---------------------------------------------------------------------------
//
// Thin wrapper over scan_paths / validate_edit / scan_all. Picks the tool:
//   - --mode validate-edit  → unity_open_mcp_validate_edit (needs paths)
//   - --mode scan-paths     → unity_open_mcp_scan_paths (needs paths)
//   - --mode auto (default) → scan_paths when paths given, else scan_all
// Exit code follows the 4-level contract based on severity counts vs the
// resolved fail_on_severity threshold.

export interface VerifyCommandOptions {
  json: boolean;
  paths: string[];
  mode: "auto" | "scan-paths" | "validate-edit";
  failOnSeverity: string | undefined;
  profile: string | undefined;
  includeRules: string[] | undefined;
  excludeRules: string[] | undefined;
  platformProfile: string | undefined;
}

export async function runVerifyCommand(
  stack: RouterStack,
  opts: VerifyCommandOptions,
): Promise<CliCommandResult> {
  const hasPaths = opts.paths.length > 0;

  // Resolve which tool to call.
  let toolName: string;
  if (opts.mode === "validate-edit") {
    if (!hasPaths) {
      return verifyError("validate-edit mode requires at least one path.");
    }
    toolName = "unity_open_mcp_validate_edit";
  } else if (opts.mode === "scan-paths") {
    if (!hasPaths) {
      return verifyError("scan-paths mode requires at least one path.");
    }
    toolName = "unity_open_mcp_scan_paths";
  } else {
    // auto: scan_paths when paths given, else scan_all (whole project).
    toolName = hasPaths ? "unity_open_mcp_scan_paths" : "unity_open_mcp_scan_all";
  }

  const tool = TOOL_BY_NAME.get(toolName);
  if (!tool) {
    return verifyError(`Internal error: tool '${toolName}' not registered.`);
  }

  // Build args from the CLI flags, applying schema defaults for omitted keys.
  const args: Record<string, unknown> = {};
  if (hasPaths) args.paths = opts.paths;
  if (opts.failOnSeverity) args.fail_on_severity = opts.failOnSeverity;
  if (opts.profile) args.profile = opts.profile;
  if (opts.includeRules) args.include_rules = opts.includeRules;
  if (opts.excludeRules) args.exclude_rules = opts.excludeRules;
  if (opts.platformProfile) args.platform_profile = opts.platformProfile;
  // scan_all doesn't accept include/exclude/profile/paging — it's whole-project.
  if (toolName === "unity_open_mcp_scan_all") {
    delete args.include_rules;
    delete args.exclude_rules;
    delete args.profile;
  }

  const routedArgs = withSchemaDefaults(tool, args);
  const result: CallToolResult = await stack.router.route(toolName, routedArgs);
  const body = extractResultBody(result);
  const isError = result.isError === true;

  // Classify the exit code from severity counts. The folded verify result
  // carries `issuesBySeverity`; scan_all carries a `summary` with severity
  // counts. Extract whichever shape we got.
  const counts = extractSeverityCounts(body);
  const threshold = severityThreshold(opts.failOnSeverity);
  const unreachable = isUnreachableResult(body, isError);
  let exitCode: number = isError
    ? (isTimeoutError(body) ? EXIT.TIMEOUT : EXIT.ERRORS)
    : classifyBySeverity(counts, threshold);
  exitCode = withTimeout(exitCode, unreachable);

  const json = {
    command: "verify",
    tool: toolName,
    isError,
    result: body,
    severityCounts: counts,
    exitLevel: exitCodeToLevel(exitCode),
  };

  return {
    exitCode,
    json,
    human: formatVerifyHuman(json),
    errorLabel: isError ? "tool_error" : undefined,
  };
}

function verifyError(message: string): CliCommandResult {
  return {
    exitCode: EXIT.ERRORS,
    json: { command: "verify", error: { code: "verify_arg", message } },
    human: `verify: ${message}`,
    errorLabel: "verify_arg",
  };
}

/** Pull {error,warn,info,verbose} counts from a verify/scan result body. */
function extractSeverityCounts(body: unknown): SeverityCounts {
  const counts: SeverityCounts = {};
  if (!body || typeof body !== "object") return counts;
  const obj = body as Record<string, unknown>;
  // scan_paths/validate_edit folded shape: issuesBySeverity directly.
  if (obj.issuesBySeverity && typeof obj.issuesBySeverity === "object") {
    Object.assign(counts, obj.issuesBySeverity);
  }
  // scan_all shape: nested under summary.counts or summary.severity.
  if (obj.summary && typeof obj.summary === "object") {
    const summary = obj.summary as Record<string, unknown>;
    if (summary.counts && typeof summary.counts === "object") {
      Object.assign(counts, summary.counts);
    }
    if (summary.severity && typeof summary.severity === "object") {
      Object.assign(counts, summary.severity);
    }
  }
  return counts;
}

/** Detect bridge-unreachable / timeout error envelopes in a tool result. */
function isUnreachableResult(body: unknown, isError: boolean): boolean {
  if (!isError || !body || typeof body !== "object") return false;
  const obj = body as Record<string, unknown>;
  const err = obj.error;
  if (err && typeof err === "object") {
    const code = (err as Record<string, unknown>).code;
    if (typeof code === "string") {
      return (
        code === "bridge_unavailable" ||
        code === "bridge_compile_failed" ||
        code === "timeout" ||
        code === "request_timeout"
      );
    }
  }
  return false;
}

function isTimeoutError(body: unknown): boolean {
  if (!body || typeof body !== "object") return false;
  const obj = body as Record<string, unknown>;
  const err = obj.error;
  if (err && typeof err === "object") {
    const code = (err as Record<string, unknown>).code;
    return code === "timeout" || code === "request_timeout";
  }
  return false;
}

function exitCodeToLevel(code: number): string {
  switch (code) {
    case EXIT.SUCCESS:
      return "success";
    case EXIT.WARNINGS:
      return "warnings";
    case EXIT.ERRORS:
      return "errors";
    case EXIT.TIMEOUT:
      return "timeout";
    default:
      return "unknown";
  }
}

function formatVerifyHuman(json: {
  tool: string;
  isError: boolean;
  severityCounts: SeverityCounts;
  exitLevel: string;
  result: unknown;
}): string {
  const c = json.severityCounts;
  const head =
    `verify (${json.tool}): ${json.isError ? "ERROR" : json.exitLevel.toUpperCase()} ` +
    `[errors=${c.error ?? 0} warn=${c.warn ?? 0} info=${c.info ?? 0} verbose=${c.verbose ?? 0}]`;
  const bodyStr =
    typeof json.result === "string"
      ? json.result
      : JSON.stringify(json.result, null, 2);
  return `${head}\n${bodyStr}`;
}

// ---------------------------------------------------------------------------
// baseline
// ---------------------------------------------------------------------------
//
// Wraps unity_open_mcp_baseline_create. `create` and `update` are the same
// operation (the tool always overwrites the file); the two subcommands exist
// for CI semantics (create = initial, update = refresh on main).

export interface BaselineCommandOptions {
  json: boolean;
  subcommand: "create" | "update";
  baselinePath: string | undefined;
  platformProfile: string | undefined;
}

export async function runBaselineCommand(
  stack: RouterStack,
  opts: BaselineCommandOptions,
): Promise<CliCommandResult> {
  const toolName = "unity_open_mcp_baseline_create";
  const tool = TOOL_BY_NAME.get(toolName);
  if (!tool) {
    return baselineError(`Internal error: tool '${toolName}' not registered.`);
  }

  const args: Record<string, unknown> = {};
  if (opts.baselinePath) args.baseline_path = opts.baselinePath;
  if (opts.platformProfile) args.platform_profile = opts.platformProfile;

  const routedArgs = withSchemaDefaults(tool, args);
  const result: CallToolResult = await stack.router.route(toolName, routedArgs);
  const body = extractResultBody(result);
  const isError = result.isError === true;
  const unreachable = isUnreachableResult(body, isError);
  const exitCode = withTimeout(
    isError ? EXIT.ERRORS : EXIT.SUCCESS,
    unreachable,
  );

  const json = {
    command: "baseline",
    subcommand: opts.subcommand,
    tool: toolName,
    isError,
    result: body,
    exitLevel: exitCodeToLevel(exitCode),
  };

  return {
    exitCode,
    json,
    human: formatBaselineHuman(json),
    errorLabel: isError ? "tool_error" : undefined,
  };
}

function baselineError(message: string): CliCommandResult {
  return {
    exitCode: EXIT.ERRORS,
    json: { command: "baseline", error: { code: "baseline_arg", message } },
    human: `baseline: ${message}`,
    errorLabel: "baseline_arg",
  };
}

function formatBaselineHuman(json: {
  subcommand: string;
  isError: boolean;
  exitLevel: string;
  result: unknown;
}): string {
  const head = `baseline ${json.subcommand}: ${json.isError ? "ERROR" : json.exitLevel.toUpperCase()}`;
  const bodyStr =
    typeof json.result === "string"
      ? json.result
      : JSON.stringify(json.result, null, 2);
  return `${head}\n${bodyStr}`;
}

// ---------------------------------------------------------------------------
// regression
// ---------------------------------------------------------------------------
//
// Wraps unity_open_mcp_regression_check. The tool already returns a structured
// `regressed`/`passed` verdict; we surface it and map to the 4-level exit code.

export interface RegressionCommandOptions {
  json: boolean;
  baselinePath: string;
  regressionThreshold: number | undefined;
  platformProfile: string | undefined;
}

export async function runRegressionCommand(
  stack: RouterStack,
  opts: RegressionCommandOptions,
): Promise<CliCommandResult> {
  const toolName = "unity_open_mcp_regression_check";
  const tool = TOOL_BY_NAME.get(toolName);
  if (!tool) {
    return regressionError(`Internal error: tool '${toolName}' not registered.`);
  }

  const args: Record<string, unknown> = {
    baseline_path: opts.baselinePath,
  };
  if (opts.regressionThreshold !== undefined) {
    args.regression_threshold = opts.regressionThreshold;
  }
  if (opts.platformProfile) args.platform_profile = opts.platformProfile;

  const routedArgs = withSchemaDefaults(tool, args);
  const result: CallToolResult = await stack.router.route(toolName, routedArgs);
  const body = extractResultBody(result);
  const isError = result.isError === true;
  const unreachable = isUnreachableResult(body, isError);

  // The regression tool returns regressed=true when the error-count increase
  // exceeded the threshold. A regression → ERRORS exit code.
  const regressed = isRegressed(body);
  let exitCode: number = isError
    ? (isTimeoutError(body) ? EXIT.TIMEOUT : EXIT.ERRORS)
    : regressed
      ? EXIT.ERRORS
      : EXIT.SUCCESS;
  exitCode = withTimeout(exitCode, unreachable);

  const json = {
    command: "regression",
    subcommand: "check",
    tool: toolName,
    isError,
    regressed,
    result: body,
    exitLevel: exitCodeToLevel(exitCode),
  };

  return {
    exitCode,
    json,
    human: formatRegressionHuman(json),
    errorLabel: isError ? "tool_error" : undefined,
  };
}

function isRegressed(body: unknown): boolean {
  if (!body || typeof body !== "object") return false;
  return (body as { regressed?: boolean }).regressed === true;
}

function regressionError(message: string): CliCommandResult {
  return {
    exitCode: EXIT.ERRORS,
    json: { command: "regression", error: { code: "regression_arg", message } },
    human: `regression: ${message}`,
    errorLabel: "regression_arg",
  };
}

function formatRegressionHuman(json: {
  isError: boolean;
  regressed: boolean;
  exitLevel: string;
  result: unknown;
}): string {
  const verdict = json.isError
    ? "ERROR"
    : json.regressed
      ? "REGRESSED"
      : "OK (no regression)";
  const head = `regression check: ${verdict}`;
  const bodyStr =
    typeof json.result === "string"
      ? json.result
      : JSON.stringify(json.result, null, 2);
  return `${head}\n${bodyStr}`;
}

// ---------------------------------------------------------------------------
// help / version
// ---------------------------------------------------------------------------

export function helpText(binName: string): string {
  // M31 Plan 6 / T6.6 — delegated to the light help-text module so the CLI's
  // --help / --version fast paths do not pull in the heavy command/router
  // import graph (this file imports ALL_TOOLS). Re-exported from here for
  // backward compat with commands.test.ts.
  return helpTextImpl(binName);
}

export function versionText(version: string): string {
  return versionTextImpl(version);
}

export {
  DEFAULT_WAIT_TIMEOUT_MS,
  DEFAULT_POLL_INTERVAL_MS,
};
