# CLI and automation reference

The `unity-open-mcp` package provides both the stdio MCP server and a CLI for
scripting. With no subcommand it starts the MCP server; with a subcommand it
uses the same routing stack from a terminal or CI job.

## Commands

| Command | Purpose | Backend |
|---|---|---|
| `ping` | Probe the live bridge once. | `unity_open_mcp_ping` |
| `wait-for-ready` | Poll until the bridge is ready or the timeout expires. | Ping loop |
| `status` | Show resolved port, instance lock, readiness, and compatibility. | `unity_open_mcp_bridge_status` |
| `run-tool <name>` | Invoke any MCP tool by full name. | Tool router |
| `stream-events` | Stream bridge console/state events. | `unity_senses_pull_events` |
| `verify [paths...]` | Run a scoped or full verify scan. | `scan_paths`, `validate_edit`, or `scan_all` |
| `baseline create\|update` | Create or refresh a regression baseline. | `unity_open_mcp_baseline_create` |
| `regression check` | Compare the project with its baseline. | `unity_open_mcp_regression_check` |

Use `unity-open-mcp --help` or
`unity-open-mcp <command> --help` for the current option list.

## Common options

- `--project <absolute-path>` selects the Unity project.
- `--json` emits machine-readable JSON.
- `--arg key=value` supplies a `run-tool` argument; repeat it for multiple
  arguments.
- Command-specific timeout and polling options are shown in command help.

Examples:

```bash
npx -y unity-open-mcp@0.6.1 wait-for-ready \
  --project /absolute/path/to/MyGame

npx -y unity-open-mcp@0.6.1 status \
  --project /absolute/path/to/MyGame --json

npx -y unity-open-mcp@0.6.1 run-tool unity_open_mcp_capabilities \
  --project /absolute/path/to/MyGame --json \
  --arg include_planned=false
```

`run-tool` returns the same JSON payload as an MCP call to that tool.

## Environment

`UNITY_PROJECT_PATH` can provide the project path when `--project` is omitted.
Batch fallback may also need `UNITY_PATH`. For unattended startup modal
handling, use [Dialog policy](../dialog-policy.md).

## CI

[CI templates](../ci/README.md) is the canonical owner of:

- the supported exit-code contract;
- the health-check → verify-on-PR → regression-on-main pipeline;
- baseline storage and update behavior;
- GitHub Actions and GitLab CI templates.

Do not infer the CLI's verify/regression exit meanings from Unity's own
`-runTests` process codes; they are separate contracts.
