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
| `update [--check]` | Check for or apply an MCP server update; no Unity project is required. | npm registry, then GitHub Releases fallback |
| `setup --project P --client C` | Install or repair Unity package pins, project MCP config, and the bundled core skill. | Local files only; no bridge required |

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
npx -y unity-open-mcp@1.2.3 wait-for-ready \
  --project /absolute/path/to/MyGame

npx -y unity-open-mcp@1.2.3 status \
  --project /absolute/path/to/MyGame --json

npx -y unity-open-mcp@1.2.3 run-tool unity_open_mcp_capabilities \
  --project /absolute/path/to/MyGame --json \
  --arg include_planned=false
```

`run-tool` returns the same JSON payload as an MCP call to that tool.

## Project setup

```bash
npx -y unity-open-mcp@latest setup \
  --project /absolute/path/to/MyGame \
  --client cursor
```

`setup` pins the bridge, verify package, and MCP server to the version of the
package currently running. It accepts the project-config writers `cursor`,
`claude`, `opencode`, and `agents`. The command preserves unrelated Unity
dependencies, MCP servers, and environment keys, then byte-copies the core
skill bundled in the npm package. It never needs a live Editor or bridge and
does not install optional domain packages.

Use `--dry-run` to preview without writes, `--skip-skill` to leave the skill
untouched, and `--json` for a stable report. Exit `0` means success, `2` means
project/client validation failed, and `1` means a read, parse, or write failed.
See [Agent setup](../setup/agent-setup.md) for the handoff flow.

## MCP server updates

```bash
unity-open-mcp update --check
unity-open-mcp update
```

The check exits `0` when current, `10` when an update is available, and `11`
when neither npm nor the GitHub fallback can resolve a version. Apply exits `12`
if npm cannot install the resolved release. `--json` provides the same outcome
as structured data.

Global and project-local npm installs update in place. An `npx` invocation
prints pin/`@latest` guidance because a running npx cache cannot safely replace
itself. The command changes only the MCP npm install—never Unity package pins or
MCP client configuration. Follow [Updating](../updating.md) to move the whole
installation together or to update without network access.

## Environment

`UNITY_PROJECT_PATH` can provide the project path when `--project` is omitted
for bridge-backed commands. `setup` deliberately requires an explicit absolute
`--project`; `update` does not need a Unity project path.
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
