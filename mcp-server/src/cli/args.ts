// M15 T6.1 — Thin CLI argument parsing.
//
// No runtime deps (mcp-server/AGENTS.md: only @modelcontextprotocol/sdk). The
// parser is hand-rolled and intentionally small — it covers the four CLI
// subcommands and their shared options. Anything more complex should go through
// the MCP server proper.
//
// Command shapes:
//   unity-open-mcp ping [--json] [--timeout-ms N]
//   unity-open-mcp wait-for-ready [--json] [--timeout-ms N] [--interval-ms N]
//   unity-open-mcp status [--json]
//   unity-open-mcp run-tool <name> [--json] [--args '<json>'] [--arg k=v]...
//   unity-open-mcp --help | -h
//   unity-open-mcp --version | -V
//
// Shared options (where relevant):
//   --project <path> | -P <path>   override UNITY_PROJECT_PATH
//   --port <n>      | -p <n>       override UNITY_OPEN_MCP_BRIDGE_PORT
//   --timeout-ms <n>               ping/wait timeout in milliseconds
//   --interval-ms <n>              wait-for-ready poll interval
//   --json                         emit JSON instead of human-readable output

export type CliCommand =
  | "ping"
  | "wait-for-ready"
  | "status"
  | "run-tool"
  | "help"
  | "version";

export const KNOWN_COMMANDS: readonly string[] = [
  "ping",
  "wait-for-ready",
  "status",
  "run-tool",
];

export interface ParsedCli {
  command: CliCommand | null;
  /** Bare `--json` flag — switches human-readable output to JSON. */
  json: boolean;
  /** Resolved project path (flag wins, else UNITY_PROJECT_PATH env). */
  projectPath: string | undefined;
  /** Resolved bridge port override (flag wins, else UNITY_OPEN_MCP_BRIDGE_PORT env). */
  port: number | undefined;
  /** Ping / wait-for-ready overall timeout (ms). */
  timeoutMs: number | undefined;
  /** wait-for-ready poll interval (ms). */
  intervalMs: number | undefined;
  /** Tool name for run-tool. */
  toolName: string | undefined;
  /** Tool args for run-tool, merged from --args + --arg. */
  toolArgs: Record<string, unknown>;
  /** Parse error message; when set, the dispatcher prints it and exits non-zero. */
  error: string | undefined;
  /** Unknown / unparsed leftovers (currently an error condition). */
  unknown: string[];
}

export function emptyParsed(): ParsedCli {
  return {
    command: null,
    json: false,
    projectPath: undefined,
    port: undefined,
    timeoutMs: undefined,
    intervalMs: undefined,
    toolName: undefined,
    toolArgs: {},
    error: undefined,
    unknown: [],
  };
}

/**
 * Parse the CLI argv (everything after `node dist/index.js`). Returns a
 * structured result; the dispatcher interprets `command`/`error`. Never throws
 * — parse problems are reported through `error`.
 */
export function parseCliArgs(argv: string[]): ParsedCli {
  const parsed = emptyParsed();
  // Make a mutable copy; we walk it with an index so `--arg`/`--set` can
  // consume their following token.
  const args = argv.slice();

  let i = 0;
  // First non-flag token is the command (positional). For run-tool, a second
  // positional is the tool name.
  let positionalCount = 0;

  while (i < args.length) {
    const tok = args[i];

    // --- flags that take no value ---
    if (tok === "--json") {
      parsed.json = true;
      i++;
      continue;
    }
    if (tok === "-h" || tok === "--help") {
      parsed.command = "help";
      return parsed;
    }
    if (tok === "-V" || tok === "--version") {
      parsed.command = "version";
      return parsed;
    }

    // --- flags that take a value ---
    if (tok === "--project" || tok === "-P") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = `${tok} requires a project path.`;
        return parsed;
      }
      parsed.projectPath = v;
      i += 2;
      continue;
    }
    if (tok === "--port" || tok === "-p") {
      const v = args[i + 1];
      const n = parsePositiveInt(v);
      if (n === undefined) {
        parsed.error = `${tok} requires a valid port number (1-65535).`;
        return parsed;
      }
      parsed.port = n;
      i += 2;
      continue;
    }
    if (tok === "--timeout-ms") {
      const v = args[i + 1];
      const n = parsePositiveInt(v);
      if (n === undefined) {
        parsed.error = "--timeout-ms requires a positive integer (ms).";
        return parsed;
      }
      parsed.timeoutMs = n;
      i += 2;
      continue;
    }
    if (tok === "--interval-ms") {
      const v = args[i + 1];
      const n = parsePositiveInt(v);
      if (n === undefined) {
        parsed.error = "--interval-ms requires a positive integer (ms).";
        return parsed;
      }
      parsed.intervalMs = n;
      i += 2;
      continue;
    }

    // run-tool arg passing. Two shapes:
    //   --args '<json blob>'   → merge parsed JSON object into toolArgs
    //   --arg key=value        → set one key; value JSON-parsed if valid JSON,
    //                             else kept as a string. Repeatable.
    if (tok === "--args") {
      const v = args[i + 1];
      if (v === undefined) {
        parsed.error = "--args requires a JSON object argument.";
        return parsed;
      }
      const merged = mergeJsonArgs(parsed.toolArgs, v);
      if (merged instanceof Error) {
        parsed.error = merged.message;
        return parsed;
      }
      parsed.toolArgs = merged;
      i += 2;
      continue;
    }
    if (tok === "--arg" || tok === "--set") {
      const v = args[i + 1];
      if (v === undefined || !v.includes("=")) {
        parsed.error = `${tok} requires a key=value token.`;
        return parsed;
      }
      const eq = v.indexOf("=");
      const key = v.slice(0, eq);
      const rawValue = v.slice(eq + 1);
      if (key.length === 0) {
        parsed.error = `${tok} has an empty key in '${v}'.`;
        return parsed;
      }
      parsed.toolArgs[key] = coerceArgValue(rawValue);
      i += 2;
      continue;
    }

    // --- positionals ---
    if (!tok.startsWith("-")) {
      if (positionalCount === 0) {
        if (!KNOWN_COMMANDS.includes(tok)) {
          parsed.error = `Unknown command '${tok}'. Known: ${KNOWN_COMMANDS.join(", ")}.`;
          return parsed;
        }
        // narrowed by the includes() check above
        parsed.command = tok as CliCommand;
      } else if (positionalCount === 1) {
        if (parsed.command !== "run-tool") {
          parsed.error = `Unexpected positional '${tok}' for command '${parsed.command}'.`;
          return parsed;
        }
        parsed.toolName = tok;
      } else {
        parsed.error = `Unexpected positional '${tok}'.`;
        return parsed;
      }
      positionalCount++;
      i++;
      continue;
    }

    // Anything else is an unknown flag. Collect for a helpful error.
    parsed.unknown.push(tok);
    i++;
  }

  if (parsed.unknown.length > 0) {
    parsed.error = `Unknown option(s): ${parsed.unknown.join(", ")}.`;
    return parsed;
  }

  // Command-specific validation.
  if (parsed.command === "run-tool" && !parsed.toolName) {
    parsed.error = "run-tool requires a tool name (run-tool <name>).";
    return parsed;
  }

  if (!parsed.command) {
    // No command at all — the caller (dispatcher) decides what to do. When
    // invoked as an MCP server (no subcommand), the stdio path handles it; we
    // surface help for an explicit `--json` with no command, but otherwise
    // return null command so the dispatcher can fall through to the server.
  }

  return parsed;
}

function parsePositiveInt(v: string | undefined): number | undefined {
  if (v === undefined || v.startsWith("-")) return undefined;
  const n = Number(v);
  if (!Number.isInteger(n) || n <= 0) return undefined;
  return n;
}

/**
 * Parse a `--args` JSON blob and merge it into the existing args object. The
 * blob MUST parse to a JSON object — arrays / scalars are rejected so a stray
 * `--args '[1,2]'` doesn't silently become `{ "0": 1 }`. Returns an Error when
 * the blob is invalid so the parser can surface a clean message.
 */
function mergeJsonArgs(
  into: Record<string, unknown>,
  blob: string,
): Record<string, unknown> | Error {
  let parsed: unknown;
  try {
    parsed = JSON.parse(blob);
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    return new Error(`--args is not valid JSON: ${msg}`);
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    return new Error("--args must be a JSON object.");
  }
  return { ...into, ...(parsed as Record<string, unknown>) };
}

/**
 * Coerce a `--arg key=value` value. If the raw value parses as JSON, use the
 * parsed form (so `--arg timeout_ms=30000` yields a number and `--arg
 * include_planned=true` yields a boolean); otherwise keep it as a string.
 */
export function coerceArgValue(rawValue: string): unknown {
  try {
    return JSON.parse(rawValue);
  } catch {
    return rawValue;
  }
}
