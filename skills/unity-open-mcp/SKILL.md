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
6. After a lost response, socket reset, or timeout, inspect post-state before retrying a mutation, even if the bridge restarted. If `delta` returns `validation_incomplete`, resolve `error.rulesFailed` and create a fresh checkpoint. Copy scan `issueId` unchanged into `apply_fix.issue_id`; use `profile: "full"` to retrieve issues.

## Non-negotiable safety

- Never launch a second Unity instance for the same project. A live PID with an unavailable bridge requires recovery, not another launch.
- Never silently discard dirty scenes or bypass lifecycle guards. Pure `read_only` snippets must not write, refresh, compile, switch scenes, or change play state; this flag is an assertion, not a sandbox.
- While the Editor is reachable, prefer typed live edits. Never directly edit an open/dirty scene or asset YAML. Explicitly identify necessary file repair as the offline-file route; preserve GUID/fileID links, check the intended scene, then reimport/reserialize and run scoped validation.
- Run one test request at a time. `run_id` names the started run, not a poll handle. Do not retry an unresolved run or put server-polled tests/recovery tools in a live batch.
- If a top-level fix reports `rollback_unavailable` because backup failed, repair temporary-directory space or file permissions before retrying; do not bypass the refusal.
- Batch arguments are validated before step zero. All-read batches need no mutation scope; mixed batches require aggregate scope. A failed mutation with a skipped gate has not passed validation.

## Load only the reference needed now

- Before browsing or activating unfamiliar tools: [Discovery and groups](references/discovery-and-groups.md).
- On compile errors, reloads, Safe Mode, or a live PID with no bridge: [Compile and Safe Mode](references/compile-and-safe-mode.md).
- Before scene switches, play transitions, snippets, or choosing live/batch/offline routes: [Routing and lifecycle](references/routing-and-lifecycle.md).
- Before batching, interpreting a failed gate, or applying fixes: [Batch and gates](references/batch-and-gates.md).
- Before file-level repair, YAML edits, or offline asset investigation: [YAML and offline work](references/yaml-and-offline-work.md).
- Before screenshots, event capture, or test runs: [Senses and tests](references/senses-and-tests.md).
- Before driving gameplay input (taps, drags, multi-second gestures — legacy Input Manager or Input System): activate `input-simulation` and follow its domain skill; read the reported `backend` array and refusals instead of reflecting into game input code.
- To playtest a screen (read its texts, find "the Buy button"): activate `playtest` with `input-simulation`, read it with `ui_snapshot` (compact, paged), then tap by label with `target_text`; resolve `ambiguous_target` with `index` / `under`, never by guessing names.
- To wait for the game (a wave ends, a timer fires, a popup closes): one `playtest_wait` with a condition (`log` regex, `ui` query — `matches:false` for "gone", `object` exists/active/destroyed, the project's own read-only `project_command`, `frames`, `ms`, or `all`/`any`) and a `timeout_ms` — never poll in a loop of snapshots, steps or `execute_csharp`. Read `fired` (which condition, `frame`, `play_time`); on `timed_out` read `condition.last` before retrying. `time_scale` fast-forwards for the wait only (restored); `fail_on: ["exception"]` stops on a game exception with its stack. Long waits return a `job_id` → `jobs wait`.
- For animated content (tweens, popups, pulsing / "breathing" effects) one-shot before/after frames lie: capture a series with `unity_senses_capture_series` (`at_ms` / `every_ms`+`duration_ms` / `at_frames`, optionally `start_on` a condition), add `contact_sheet: true` for one labelled review image and `metric: "diff"` in a `metric_region` to see the change as numbers. For a tap and the frames after it, use `inputsim_gesture` `capture_at_ms`.
- Before a live playtest that can change the player's save (currency, rewards, progress, unlocks), isolate the state first. Prefer the project's own hook: `project_commands(action="list", query="playtest")` — invoke `playtest.begin` before playing and `playtest.end` after when both exist. Otherwise, in edit mode: `player_state` `snapshot` (`name`; add `editor_prefs_prefixes` only for the project's own dev toggles — EditorPrefs are shared by every project), play, `diff` to report what the session changed, stop play mode, `restore` without `confirm` and read `prefs.removed` / `files.removed`, then `restore` with `confirm: true` and check `verified`. Restore overwrites PlayerPrefs and deletes `persistentDataPath` files that are not in the snapshot; never restore a snapshot you did not take for this session.
- To check layout on a device or resolution: device first, then capture. `game_view get` (note the state to restore), then `set_device` (Device Simulator device + orientation; read `screen.safe_area` / `insets`) or `set_resolution` (reuses an `Open MCP <w>x<h>` Game view size — its only persistent effect); continue only on `settled: true` (`not_settled` = paused or hidden view). Capture `view: "composed"` without width/height (native size; `overlay: "safe_area"` for notch checks) and quote its `capturedAt`. Restore explicitly afterwards (`set_resolution` with the `size_label` from `get`); input tools never open the Simulator.

### Project-owned commands

Use `unity_open_mcp_project_commands(action="list", query="…", limit=20)` to
find project commands; page using `nextOffset`. Always describe an exact catalog id
with `action="describe", id="project.owner.command"` for its schema, safety
metadata and availability diagnostics. This tool stays visible regardless of
active groups and reads fresh metadata after recompilation. Invoke with `action="invoke", command_id="project.owner.command", args={...}`.
The server revalidates a fresh schema; optionally pin `schema_version` from describe.
Supply `paths_hint` for mutations without declared paths; gate and dirty-scene
options stay outside `args`. Check `mutation.output.result` and the normal gate /
lifecycle envelope. After timeout or reload, inspect post-state before retrying.
For an `async` command, use jobs with its exact id and the invocation envelope
in job `args`. Do not bypass unavailable commands with reflection or nest project
commands in `batch_execute`.

## Asynchronous job observation

Use the always-visible `unity_open_mcp_jobs` for explicitly supported asynchronous
operations. `start` requires `tool_or_command`, adapter `args`, and an
`idempotency_key` (optional for read-only targets). Supported targets are
`unity_senses_run_tests` (ordinary test filters, no caller `run_id`, cancellation
unsupported), available async project commands (`args: {args: {...}, gate,
paths_hint}`), and play-mode operations (`unity_open_mcp_inputsim_gesture`,
`unity_open_mcp_playtest_wait`, `unity_senses_capture_series`; the tool's own args;
cancellable — cancel releases held input and restores a wait's time scale). Read terminal project-job gate
results even on failure/cancellation. An unsupported target must not be retried as
an arbitrary background tool.

After start, call `status`; a queued acknowledgement is not completion. Use bounded
`wait` calls until terminal, then inspect the retained result and gate.
Keep the job id and the same project/port/agent identity for `status`, `wait`,
`cancel` and `list`. A wait timeout leaves execution running. Reuse the same key
and arguments after a start response is lost. Cancellation is cooperative and may
be unsupported. Treat `orphaned` as an unknown outcome; inspect operation evidence
before retrying. Records and keys expire 30 minutes after completion and are lost
on server restart. `job_busy` means a project job owns the Editor: mutations, Editor-state
writes, test starts and every `execute_csharp` (even `read_only: true`) are refused.
Wait on `error.job.jobId` via jobs (owner only), use typed reads meanwhile, retry once; never loop-retry. See [Routing and lifecycle](references/routing-and-lifecycle.md).
