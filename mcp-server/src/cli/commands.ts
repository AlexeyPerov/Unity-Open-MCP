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
    baseUrl: `http://127.0.0.1:${stack.port}`,
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
        ? `Bridge is not reachable at http://127.0.0.1:${stack.port}.`
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
    `Bridge: http://127.0.0.1:${port}`,
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
    baseUrl: `http://127.0.0.1:${stack.port}`,
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
// help / version
// ---------------------------------------------------------------------------

export function helpText(binName: string): string {
  return [
    `Usage: ${binName} <command> [options]`,
    "",
    "Thin CLI for Unity Open MCP — wraps the MCP server for CI / scripting.",
    "When invoked with no command, runs the stdio MCP server (MCP client mode).",
    "",
    "Commands:",
    "  ping                          One /ping against the bridge; exit 0 if ready.",
    "  wait-for-ready                Poll /ping until the bridge is ready; exit 0/non-zero.",
    "  status                        Show resolved bridge port, instance lock, and readiness.",
    "  run-tool <name>               Invoke an MCP tool by name; print its JSON result.",
    "  --help, -h                    Show this help.",
    "  --version, -V                 Print the package version.",
    "",
    "Options:",
    "  --json                        Emit JSON instead of human-readable output (all commands).",
    "  --project <path>, -P <path>   Unity project path (default: UNITY_PROJECT_PATH).",
    "  --port <n>, -p <n>            Bridge port override (default: UNITY_OPEN_MCP_BRIDGE_PORT).",
    "  --timeout-ms <n>              Ping / wait-for-ready timeout in ms.",
    "  --interval-ms <n>             wait-for-ready poll interval in ms.",
    "  --args '<json>'               JSON object of tool args (run-tool).",
    "  --arg key=value               One tool arg (run-tool, repeatable; JSON-parsed if valid).",
    "",
    "Environment:",
    "  UNITY_PROJECT_PATH             Required for every command.",
    "  UNITY_OPEN_MCP_BRIDGE_PORT     Optional port override.",
    "  UNITY_PATH                     Unity Editor executable for batch-only tools.",
    "",
    "Examples:",
    `  ${binName} wait-for-ready`,
    `  ${binName} run-tool unity_open_mcp_ping --json`,
    `  ${binName} run-tool unity_open_mcp_list_assets --arg folder=Assets --arg max_per_folder=10`,
    `  ${binName} status --json`,
  ].join("\n");
}

export function versionText(version: string): string {
  return `unity-open-mcp ${version}`;
}

export {
  DEFAULT_WAIT_TIMEOUT_MS,
  DEFAULT_POLL_INTERVAL_MS,
};
