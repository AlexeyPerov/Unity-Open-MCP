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
4. Prefer typed tools. Use canonical `asset_path`, `game_object_path`, `component_type`, and `property_path` from the exact schema. Start compact and page reads; expand only the relevant slice. Before a reflection snippet, check `invoke_method`, `object_get_data` (`include_non_public` for private state), `playerprefs_get`/`_set`, `settings_set_time`, `scene_save`; an `execute_csharp` result's `typedToolHints` names the one that fits, and a `compilation_error` carries `error.didYouMean` with the real type / member names.
5. Scope mutations with nonempty `paths_hint` (project-wide `assets_refresh` / `recompile_scripts` default to `Assets`), then inspect `gate.delta` and `agentNextSteps`. Mutation success does not prove project safety. Validate and test the affected scope.
6. GameObject paths and ids resolve in the loaded scenes and the open prefab stage; results name the `container`. Edit a prefab with typed tools inside its stage, not with `LoadPrefabContents` snippets: `prefab_open` returns `rootPath` / `rootInstanceId`, stage paths start at the prefab root (`Root/Child`), and `prefab_close {"save":true}` (or Prefab Mode auto-save) writes the edit — the gate skips stage-only edits (`prefab_stage_edit`), so `validate_edit` the prefab after it is saved. A path found in several scenes is narrowed by `scene_path`, then `paths_hint`, the open stage, the active scene; on `ambiguous_path` retry with the `scene_path` its `agentNextSteps` names. An id inside an asset on disk is refused (`persistent_object`).
7. To wait for the Editor — after `editor_set_state`, `script_write`, asmdef / package / define edits — call `editor_status {"wait_until":"idle"|"playing"|"edit"}` (pass `after_generation` from the call's `compile.generationAtStart` when `agentNextSteps` names it). It outlives the compile and domain reload, reports the compile status and error count, and continues as a `jobs` wait past ~40 s. Never poll `editor_status`, `read_compile_errors` or the instance lock in a loop. A call sent mid-reload waits for it (bounded) on its own.
8. After writing or deleting C# on disk, call `recompile_scripts {}` (`typed-editor` group): it imports scripts Unity has not seen, compiles, and answers with the compile — `status` `compiled` / `compile_failed` (a failed call with the first errors) / `compiling` (+ `job_id`). A compile-starting `assets_refresh` answers the same way. Trust `read_compile_errors` only when `determinacy` is `current`; `indeterminate` (an offline or busy-Editor read) is never "clean", and `historicalLogErrors` are not to be fixed.
9. After a lost response, socket reset, or timeout, inspect post-state before retrying a mutation, even if the bridge restarted. If `delta` returns `validation_incomplete`, resolve `error.rulesFailed` and create a fresh checkpoint. Copy scan `issueId` unchanged into `apply_fix.issue_id`; use `profile: "full"` to retrieve issues.

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
- To playtest the game (read the screen, tap by label, wait, capture, isolate the save, record a report for a human): follow [Playtest](#playtest) below.

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

## Playtest

Activate `playtest` with `input-simulation` (`manage_tools` `activate_for`, intent "playtest …").

1. **Prepare.** Device first: `game_view get` (note what to restore), then `set_device` (Simulator device + orientation; read `screen.safe_area` / `insets`) or `set_resolution`; continue only on `settled: true`. Isolate the state: `project_commands(action="list", query="playtest")` and invoke the project's `playtest.begin` when it exists; otherwise start the session with `snapshot: true`. Then `playtest_session {"action":"start","name":"…","device":"…"}` — from now your input / wait / capture / project-command calls are recorded with their frames and console errors (one open session per agent: a second start is `session_already_open`).
2. **Boot** with `editor_set_state play` (then `editor_status {"wait_until":"playing"}`) and the project's `playtest.boot` command. Never reflect into launchers, singletons or save systems.
3. **Loop.** `ui_snapshot` (compact, paged) → act like a player: `target_text` on `inputsim_pointer` / `inputsim_touch` (resolve `ambiguous_target` with `index` / `under`, never by guessing names) or `inputsim_gesture` → one `playtest_wait` on a condition (`log` regex, `ui` query — `matches:false` for "gone", `object`, the project's read-only `project_command`, `frames`, `ms`, `all`/`any` — nestable once, e.g. `any:[{all:[…]},{ms:5000}]`) with a `timeout_ms`, never a polling loop of snapshots, steps or `execute_csharp`; read `fired`, on `timed_out` read `condition.last` → look: `unity_senses_capture_series` for anything animated (`start_on`, `contact_sheet: true`, `metric: "diff"` in a `metric_region`), a composed screenshot (no width/height; quote its `capturedAt`) for a static screen → `playtest_session {"action":"mark","label":"…","verdict":"ok|bug|suspicious","note":"…"}`.
4. **Findings.** Every console exception or error is a finding of the step that caused it: read `since_last_mark.errors` on each mark; `fail_on: ["exception"]` stops a wait at the first one with its stack.
5. **Finish.** `playtest_session {"action":"end"}` → report path + summary. Stop play mode; invoke `playtest.end`, or `player_state restore` (dry run: read `prefs.removed` / `files.removed`; then `confirm: true` with its `plan`, check `verified`) and `delete` the session's snapshot; put the Game view back (`set_resolution` with the `size_label` from `get`). Hand the report path and the bug / suspicious steps to the human.

Traps: one frame lies about animation — use a series (a tap and what follows: `inputsim_gesture` `capture_at_ms`). Captures name their device: set it before capturing; a hidden Game view tab does not render. Never mutate project assets (scenes, prefabs, writing snippets) during a playtest. Restore overwrites PlayerPrefs and deletes `persistentDataPath` files not in the snapshot; never restore a snapshot you did not take for this session. Waits / series over 40 s return a `job_id` → `jobs wait`. After an MCP server restart, `playtest_session status` lists your session under `orphaned`: `end` (or `resume`) with `session: "<id>"`.
