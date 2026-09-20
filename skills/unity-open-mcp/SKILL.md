---
name: unity-open-mcp
description: Inspect, modify, validate, and test Unity projects through Unity Open MCP, including live Editor recovery and offline asset work.
---
# Unity Open MCP

Use exact schemas, scoped mutations, and gate evidence to work on the selected Unity project.

## Core workflow

1. Discover a tool with `unity_open_mcp_capabilities(tool_name: "unity_open_mcp_component_modify")`, or browse with `query` and `page_size`. Exact lookup returns one complete schema and example. Never request the full unpaged catalog.
2. Activate the needed tools with `unity_open_mcp_manage_tools(action: "activate_for", intent: "…")`. If list refresh is unavailable, use its `invoke` action with `tool_name` and `arguments`.
3. Confirm the target project and live state with `ping` / `bridge_status`; use the instance lock’s port, never a hardcoded port. Offline reads and `read_compile_errors` do not require a live bridge.
4. Prefer typed tools. Use canonical `asset_path`, `game_object_path`, `component_type`, and `property_path` from the exact schema. Start compact and page reads; expand only the relevant slice.
5. Scope mutations with nonempty `paths_hint`, then inspect `gate.delta` and `agentNextSteps`. Mutation success does not prove project safety. Validate and test the affected scope.

## Non-negotiable safety

- Never launch a second Unity instance for the same project. A live PID with an unavailable bridge requires recovery, not another launch.
- Never silently discard dirty scenes or bypass lifecycle guards. Pure `read_only` snippets must not write, refresh, compile, switch scenes, or change play state; this flag is an assertion, not a sandbox.
- While the Editor is reachable, prefer typed live edits. Never directly edit an open/dirty scene or asset YAML. Explicitly identify necessary file repair as the offline-file route; preserve GUID/fileID links, check the intended scene, then reimport/reserialize and run scoped validation.
- Run one test request at a time. `run_id` names the started run, not a poll handle. Do not retry an unresolved run or put server-polled tests/recovery tools in a live batch.
- Batch arguments are validated before step zero. All-read batches need no mutation scope; mixed batches require aggregate scope. A failed mutation with a skipped gate has not passed validation.

## Load only the reference needed now

- Before browsing or activating unfamiliar tools: [Discovery and groups](references/discovery-and-groups.md).
- On compile errors, reloads, Safe Mode, or a live PID with no bridge: [Compile and Safe Mode](references/compile-and-safe-mode.md).
- Before scene switches, play transitions, snippets, or choosing live/batch/offline routes: [Routing and lifecycle](references/routing-and-lifecycle.md).
- Before batching, interpreting a failed gate, or applying fixes: [Batch and gates](references/batch-and-gates.md).
- Before file-level repair, YAML edits, or offline asset investigation: [YAML and offline work](references/yaml-and-offline-work.md).
- Before screenshots, event capture, or test runs: [Senses and tests](references/senses-and-tests.md).

### Project-owned commands

Use `unity_open_mcp_project_commands(action="list", query="…", limit=20)` to
find project commands; page using `nextOffset`. Describe an exact catalog id
with `action="describe", id="project.owner.command"` for its schema, safety
metadata and availability diagnostics. This tool stays visible regardless of
active groups and reads fresh metadata after recompilation. Invoke with `action="invoke", command_id="project.owner.command", args={...}`.
The server revalidates a fresh schema; optionally pin `schema_version` from describe.
Supply `paths_hint` for mutations without declared paths; gate and dirty-scene
options stay outside `args`. Check `mutation.output.result` and the normal gate /
lifecycle envelope. After timeout or reload, inspect post-state before retrying.
Async/cancellable declarations do not yet start jobs; do not bypass unavailable
commands with reflection or nest project commands in `batch_execute`.
