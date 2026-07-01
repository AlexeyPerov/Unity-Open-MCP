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
//   unity-open-mcp stream-events [--json] [--max-events N] [--follow]
//   unity-open-mcp verify [paths...] [--json] [--mode auto|scan-paths|validate-edit]
//                                 [--fail-on-severity error|warn|info|verbose|never]
//                                 [--profile compact|balanced|full]
//                                 [--include-rules a,b] [--exclude-rules a,b]
//                                 [--platform-profile mobile|console|desktop]
//   unity-open-mcp baseline create|update [--json] [--baseline-path <path>]
//                                          [--platform-profile ...]
//   unity-open-mcp regression check [--json] [--baseline-path <path>]
//                                   [--regression-threshold N]
//                                   [--platform-profile ...]
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
  | "stream-events"
  | "verify"
  | "baseline"
  | "regression"
  | "help"
  | "version";

/** Subcommand for `baseline` / `regression` (the second positional). */
export type CliSubcommand = string | undefined;

export const KNOWN_COMMANDS: readonly string[] = [
  "ping",
  "wait-for-ready",
  "status",
  "run-tool",
  "stream-events",
  "verify",
  "baseline",
  "regression",
];

/**
 * Commands that take a second positional subcommand (`baseline create`,
 * `regression check`). Used by the parser to accept rather than reject the
 * extra positional.
 */
export const COMMANDS_WITH_SUBCOMMAND: readonly string[] = [
  "baseline",
  "regression",
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
  // --- stream-events ---
  /** stream-events: max events to drain per call. */
  maxEvents: number | undefined;
  /** stream-events: keep polling until interrupted (follow mode). */
  follow: boolean;
  // --- verify ---
  /** verify: asset paths to scan (variadic positional). Empty = whole project (scan_all). */
  verifyPaths: string[];
  /** verify: which underlying tool to call. `auto` picks scan_all when no paths, else scan_paths. */
  verifyMode: "auto" | "scan-paths" | "validate-edit" | undefined;
  /** verify/regression: severity threshold string forwarded to the tool. */
  failOnSeverity: string | undefined;
  /** verify: output profile forwarded to scan_paths/validate_edit. */
  profile: string | undefined;
  /** verify: comma-separated include/exclude rule lists (parsed to arrays). */
  includeRules: string[] | undefined;
  excludeRules: string[] | undefined;
  /** verify/baseline/regression: platform profile forwarded to the tool. */
  platformProfile: string | undefined;
  // --- baseline / regression ---
  /** baseline/regression: second positional subcommand (create/update/check). */
  subcommand: CliSubcommand;
  /** baseline/regression: path to the baseline JSON file. */
  baselinePath: string | undefined;
  /** regression: max allowed error-count increase (global). */
  regressionThreshold: number | undefined;
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
    maxEvents: undefined,
    follow: false,
    verifyPaths: [],
    verifyMode: undefined,
    failOnSeverity: undefined,
    profile: undefined,
    includeRules: undefined,
    excludeRules: undefined,
    platformProfile: undefined,
    subcommand: undefined,
    baselinePath: undefined,
    regressionThreshold: undefined,
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

    // --- stream-events flags ---
    if (tok === "--max-events") {
      const v = args[i + 1];
      const n = parsePositiveInt(v);
      if (n === undefined) {
        parsed.error = "--max-events requires a positive integer.";
        return parsed;
      }
      parsed.maxEvents = n;
      i += 2;
      continue;
    }
    if (tok === "--follow") {
      parsed.follow = true;
      i++;
      continue;
    }

    // --- verify flags ---
    if (tok === "--mode") {
      const v = args[i + 1];
      if (v !== "auto" && v !== "scan-paths" && v !== "validate-edit") {
        parsed.error =
          "--mode requires one of: auto, scan-paths, validate-edit.";
        return parsed;
      }
      parsed.verifyMode = v;
      i += 2;
      continue;
    }
    if (tok === "--fail-on-severity") {
      const v = args[i + 1];
      if (
        v !== "error" &&
        v !== "warn" &&
        v !== "info" &&
        v !== "verbose" &&
        v !== "never"
      ) {
        parsed.error =
          "--fail-on-severity requires one of: error, warn, info, verbose, never.";
        return parsed;
      }
      parsed.failOnSeverity = v;
      i += 2;
      continue;
    }
    if (tok === "--profile") {
      const v = args[i + 1];
      if (v !== "compact" && v !== "balanced" && v !== "full") {
        parsed.error = "--profile requires one of: compact, balanced, full.";
        return parsed;
      }
      parsed.profile = v;
      i += 2;
      continue;
    }
    if (tok === "--include-rules") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = "--include-rules requires a comma-separated rule list.";
        return parsed;
      }
      parsed.includeRules = v.split(",").map((s) => s.trim()).filter(Boolean);
      i += 2;
      continue;
    }
    if (tok === "--exclude-rules") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = "--exclude-rules requires a comma-separated rule list.";
        return parsed;
      }
      parsed.excludeRules = v.split(",").map((s) => s.trim()).filter(Boolean);
      i += 2;
      continue;
    }
    if (tok === "--platform-profile") {
      const v = args[i + 1];
      if (v !== "mobile" && v !== "console" && v !== "desktop") {
        parsed.error =
          "--platform-profile requires one of: mobile, console, desktop.";
        return parsed;
      }
      parsed.platformProfile = v;
      i += 2;
      continue;
    }

    // --- baseline / regression flags ---
    if (tok === "--baseline-path") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = "--baseline-path requires a path.";
        return parsed;
      }
      parsed.baselinePath = v;
      i += 2;
      continue;
    }
    if (tok === "--regression-threshold") {
      const v = args[i + 1];
      const n = parseNonNegativeInt(v);
      if (n === undefined) {
        parsed.error = "--regression-threshold requires a non-negative integer.";
        return parsed;
      }
      parsed.regressionThreshold = n;
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
        // The second positional is command-specific:
        //   run-tool <toolName>
        //   baseline <create|update>
        //   regression <check>
        //   verify <first path>  (variadic — first path counted here)
        if (parsed.command === "run-tool") {
          parsed.toolName = tok;
        } else if (parsed.command === "baseline") {
          parsed.subcommand = tok;
        } else if (parsed.command === "regression") {
          parsed.subcommand = tok;
        } else if (parsed.command === "verify") {
          parsed.verifyPaths.push(tok);
        } else {
          parsed.error = `Unexpected positional '${tok}' for command '${parsed.command}'.`;
          return parsed;
        }
      } else {
        // positionalCount >= 2
        if (parsed.command === "verify") {
          // verify accepts variadic path positionals.
          parsed.verifyPaths.push(tok);
        } else {
          parsed.error = `Unexpected positional '${tok}'.`;
          return parsed;
        }
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

  // baseline requires a subcommand (create | update).
  if (parsed.command === "baseline") {
    if (!parsed.subcommand) {
      parsed.error = "baseline requires a subcommand: baseline create | update.";
      return parsed;
    }
    if (parsed.subcommand !== "create" && parsed.subcommand !== "update") {
      parsed.error = `baseline subcommand must be 'create' or 'update' (got '${parsed.subcommand}').`;
      return parsed;
    }
  }

  // regression requires `check` as its subcommand.
  if (parsed.command === "regression") {
    if (!parsed.subcommand) {
      parsed.error = "regression requires a subcommand: regression check.";
      return parsed;
    }
    if (parsed.subcommand !== "check") {
      parsed.error = `regression subcommand must be 'check' (got '${parsed.subcommand}').`;
      return parsed;
    }
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

/** Like parsePositiveInt but allows zero (for regression_threshold). */
function parseNonNegativeInt(v: string | undefined): number | undefined {
  if (v === undefined || v.startsWith("-")) return undefined;
  const n = Number(v);
  if (!Number.isInteger(n) || n < 0) return undefined;
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
