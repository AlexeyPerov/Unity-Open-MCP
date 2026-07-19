// M31 Plan 6 / T6.6 — light `helpText` + `versionText` module.
//
// These two formatters are needed on the `--help` / `--version` fast paths,
// which must NOT pull in the heavy command/router import graph (commands.ts
// imports ALL_TOOLS, the ~270-tool surface). Splitting them into their own
// module lets cli.ts import only this light file for the fast paths and defer
// the heavy `./commands.js` + `../routers.js` imports to a dynamic `import()`
// that runs only when a real subcommand is dispatched.
//
// `commands.ts` re-exports both so existing imports from `./commands.js`
// (commands.test.ts) keep resolving.

import {
  PORT_ENV_VAR,
  PROJECT_PATH_ENV_VAR,
} from "../constants.js";

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
    "  stream-events                 Drain bridge SSE events (console logs + state) to stdout.",
    "  verify [paths...]             Run a verify scan; exit code reflects severity (4-level).",
    "  baseline create|update        Create/refresh the regression baseline JSON file.",
    "  regression check              Compare current scan against the baseline; exit on regression.",
    "  --help, -h                    Show this help.",
    "  --version, -V                 Print the package version.",
    "",
    "Exit codes (verify / baseline / regression):",
    "  0  success        no issues / no regression.",
    "  1  warnings       only warnings/info below the fail threshold.",
    "  2  errors         errors present, or a regression was detected.",
    "  3  timeout        bridge never became reachable, or a call timed out.",
    "",
    "Options:",
    "  --json                        Emit JSON instead of human-readable output (all commands).",
    `  --project <path>, -P <path>   Unity project path (default: ${PROJECT_PATH_ENV_VAR}).`,
    `  --port <n>, -p <n>            Bridge port override (default: ${PORT_ENV_VAR}).`,
    "  --timeout-ms <n>              Ping / wait-for-ready timeout in ms.",
    "  --interval-ms <n>             wait-for-ready poll interval in ms.",
    "  --args '<json>'               JSON object of tool args (run-tool).",
    "  --arg key=value               One tool arg (run-tool, repeatable; JSON-parsed if valid).",
    "  --max-events <n>              stream-events: max events to drain per pull.",
    "  --follow                      stream-events: keep polling until interrupted (CI log tap).",
    "  --mode <m>                    verify: auto (default) | scan-paths | validate-edit.",
    "  --fail-on-severity <s>        verify: error | warn | info | verbose | never.",
    "  --profile <p>                 verify: compact (default) | balanced | full.",
    "  --include-rules <a,b>         verify: comma-separated rule allow-list.",
    "  --exclude-rules <a,b>         verify: comma-separated rule deny-list.",
    "  --platform-profile <p>        verify/baseline/regression: mobile | console | desktop.",
    "  --baseline-path <path>        baseline/regression: path to the baseline JSON file.",
    "  --regression-threshold <n>    regression: max allowed error-count increase (default 0).",
    "",
    "Environment:",
    `  ${PROJECT_PATH_ENV_VAR.padEnd(30)}Required for every command.`,
    `  ${PORT_ENV_VAR.padEnd(30)}Optional port override.`,
    "  UNITY_PATH                     Unity Editor executable for batch-only tools.",
    "",
    "Examples:",
    `  ${binName} wait-for-ready`,
    `  ${binName} run-tool unity_open_mcp_ping --json`,
    `  ${binName} run-tool unity_open_mcp_list_assets --arg folder=Assets --arg max_per_folder=10`,
    `  ${binName} stream-events --follow`,
    `  ${binName} verify Assets/Prefabs --fail-on-severity warn`,
    `  ${binName} verify --mode scan-paths Assets/Scripts --profile balanced`,
    `  ${binName} baseline create --baseline-path CI/baseline.json`,
    `  ${binName} regression check --baseline-path CI/baseline.json --json`,
    `  ${binName} status --json`,
  ].join("\n");
}

export function versionText(version: string): string {
  return `unity-open-mcp ${version}`;
}
