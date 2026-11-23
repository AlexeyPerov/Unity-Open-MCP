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
4. Prefer typed tools. Use canonical `asset_path`, `game_object_path`, `component_type`, and `property_path` from the exact schema. Start compact and page reads; expand only the relevant slice. Before a reflection snippet, check `invoke_method`, `object_get_data` (`include_non_public` for private state), `playerprefs_get`/`_set`, `settings_set_time`, `scene_save`, `playtest_probe` (values over frames); an `execute_csharp` result's `typedToolHints` names the one that fits, and a `compilation_error` carries `error.didYouMean` with the real type / member names.
5. Scope mutations with nonempty `paths_hint` (project-wide `assets_refresh` / `recompile_scripts` default to `Assets`), then inspect `gate.delta` and `agentNextSteps`. Mutation success does not prove project safety. Validate and test the affected scope. Each mutating call (a whole `batch_execute`) is one Editor undo step named `MCP <tool>`; `editor_undo {}` reverts exactly the last one, and `editor_undo_history` lists the steps. In play mode typed edits to scenes last only until play mode exits (`playModeOnly: true`).
6. GameObject paths and ids resolve in the loaded scenes and the open prefab stage; results name the `container`. A returned `instanceId` is the GameObject's (pass it as `instance_id` / `parent_instance_id`); a tool that adds a component also returns `componentInstanceId` (pass it as `component_instance_id`). Edit a prefab asset with typed tools, never with `LoadPrefabContents` snippets: pass `in_prefab: "Assets/…/X.prefab"` to `gameobject_*` / `component_*` / `ui_*` / `prefab_instantiate` (and the component domain tools) and address objects by path from the prefab root — the bridge loads, saves and unloads it, the gate validates the saved file, and a failed call writes nothing. Not undoable; the result's instance ids die with the call (`instanceIds: "ephemeral"`), so keep using paths; `prefabAsset.overrides` says when a variant's base or a nested prefab got the override. Several edits to one prefab: one `batch_execute` (one load, one save; a failed step drops them all). `prefab_stage_unsaved`: save / close the stage first. In an open stage (`prefab_open` → `rootPath`), edits stay unsaved until `prefab_close {"save":true}` or auto-save — the gate skips them (`prefab_stage_edit`), so `validate_edit` after. A path found in several scenes is narrowed by `scene_path`, then `paths_hint`, the open stage, the active scene; on `ambiguous_path` retry with the `scene_path` its `agentNextSteps` names. An id inside an asset on disk is refused (`persistent_object`).
7. To wait for the Editor — after `editor_set_state`, `script_write`, asmdef / package / define edits — call `editor_status {"wait_until":"idle"|"playing"|"edit"}` (pass `after_generation` from the call's `compile.generationAtStart` when `agentNextSteps` names it). It outlives the compile and domain reload, reports the compile status and error count, and continues as a `jobs` wait past ~40 s. Never poll `editor_status`, `read_compile_errors` or the instance lock in a loop. A call sent mid-reload waits for it (bounded) on its own.
8. After writing or deleting C# on disk, call `recompile_scripts {}` (`typed-editor` group): it imports scripts Unity has not seen, compiles, and answers with the compile — `status` `compiled` / `compile_failed` (a failed call with the first errors) / `compiling` (+ `job_id`). A compile-starting `assets_refresh` answers the same way. Trust `read_compile_errors` only when `determinacy` is `current`; `indeterminate` (an offline or busy-Editor read) is never "clean", and `historicalLogErrors` are not to be fixed.
9. After a lost response, socket reset, or timeout, inspect post-state before retrying a mutation, even if the bridge restarted. `editor_reloading` / `response_lost` are retryable (wait `idle`, then resend if `dispatched: false`, else verify first); `bridge_compile_failed` comes with `Editor.log` `evidence` — a frozen heartbeat alone is a long reload, not a dead bridge. A snippet that calls `RequestScriptReload` answers `reloadRequested: true` before the reload; wait `idle` next. If `delta` returns `validation_incomplete`, resolve `error.rulesFailed` and create a fresh checkpoint. Copy scan `issueId` unchanged into `apply_fix.issue_id`; use `profile: "full"` to retrieve issues.

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
- Before screenshots, event capture, or test runs: [Senses and tests](references/senses-and-tests.md). Check a UI prefab's layout in edit mode with `unity_senses_screenshot {"view":"prefab","asset_path":"…","width":…,"height":…}` (or `device`), never with a scratch scene and a `RenderTexture` snippet.
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
   To see how a value moves over frames (HP, a cooldown, a speed): `playtest_probe start` (a `member` path on a GameObject / component or a static `Type.Member`, or a read-only `expression`; `every_frames`, `max_samples`, `until` = a wait condition) before acting, then `read` with the returned `next_cursor`, `stop` for the summary — never a per-frame hook from `execute_csharp` (`Application.onBeforeRender +=`, logs in `AppDomain.SetData`), which keeps running until a reload.
4. **Findings.** Every console exception or error is a finding of the step that caused it: read `since_last_mark.errors` on each mark; `fail_on: ["exception"]` stops a wait at the first one with its stack. `playtest_session {"action":"check"}` records the current screen's detector findings (overflow, outside the safe area, overlapping controls, placeholder text, touch targets under 44 dp) with a frame.
   **Explore instead of tapping by hand** ("go through the game / find UI bugs", menu-driven and mobile UI): after steps 1–2, `playtest_explore {}` (add `deny` for project-specific danger, `restart: "command"` + `restart_command` when the game has `playtest.boot`) → `jobs wait` until terminal, hands off the game meanwhile. It taps every screen's buttons / toggles except money, destructive and leaving ones and files findings (dead buttons, dead ends, console errors and the snapshot diagnostics) into the session; it needs an isolated session (`not_isolated`) and play mode. Confirm each finding on its screen's frame before reporting it; drive sliders, text fields and scrolling by hand.
5. **Finish.** `playtest_session {"action":"end"}` → report path + summary. Stop play mode; invoke `playtest.end`, or `player_state restore` (dry run: read `prefs.removed` / `files.removed`; then `confirm: true` with its `plan`, check `verified`) and `delete` the session's snapshot; put the Game view back (`set_resolution` with the `size_label` from `get`). Hand the report path and the bug / suspicious steps to the human.

Traps: one frame lies about animation — use a series (a tap and what follows: `inputsim_gesture` `capture_at_ms`). Captures name their device: set it before capturing; a hidden Game view tab does not render. Never mutate project assets (scenes, prefabs, writing snippets) during a playtest. Restore overwrites PlayerPrefs and deletes `persistentDataPath` files not in the snapshot; never restore a snapshot you did not take for this session. Waits / series over 40 s, or slower than their schedule (low `Time.timeScale`), return a `job_id` (`status: "running"`, frames so far) → `jobs wait`; never start them again. `capture_series_stalled` / `wait_stalled` / `gesture_stalled` = no progress for 15 s (`error.reason`: `editor_paused` / `time_scale_zero`), frames so far kept. After an MCP server restart, `playtest_session status` lists your session under `orphaned`: `end` (or `resume`) with `session: "<id>"`.
