# Routing, offline, and lifecycle contracts

This page owns MCP route selection, offline coverage, lifecycle recovery, and
retry behavior.

## Route classes

| Route | Meaning |
|---|---|
| `live` | Call the running Unity bridge. |
| `batch` | Spawn headless Unity for a supported operation. |
| `offline` | Read project files or logs without Unity. |
| `local` | Resolve entirely in the MCP server. |

`capabilities.tools[].routePolicy` and `batchCapable` describe possible
fallbacks, not necessarily the route a reachable Editor will use. Inspect
`_route` and `_source` on a response for the route and data origin of that call.

## Route selection

The router prefers the live bridge for most tools. Batch and offline policies
normally describe fallback behavior when live Unity is unavailable.

Pinned exceptions:

- Always batch: `compile_check`, `scan_all`, `baseline_create`,
  `regression_check`.
- Always offline: `list_assets`.
- Live compile snapshot, never batch: `read_compile_errors`. While a live
  Editor holds the project (fresh instance lock) but `/compile-state` gives no
  determinate answer — it is compiling, reloading or busy — the answer comes
  from the lock's compile block (`_route`/`_source` `local`,
  `compileSource: "instance_lock"`); the `Editor.log` is read alone only when no
  live Editor answers (`offline`), and then historical errors never drive
  `compile_failed` and a clean read is `indeterminate`
  ([contract](mcp-tools.md#unity_open_mcp_read_compile_errors)).
- Live call, then a local wait for the compile it started: `recompile_scripts`,
  and `assets_refresh` when it started a compile. The compile result (inline,
  or a `jobs` wait past ~40 s) is the call's answer; a failed compile fails the
  call ([contract](mcp-tools.md#unity_open_mcp_recompile_scripts)).
- Live status with an instance-lock fallback, never batch: `editor_status`
  (`_route` `live` / `local`); its `wait_until` form is a local wait that polls
  the lock, `/ping` and `/compile-state`. `ping` answers from the lock the same
  way while the listener is down.
- Always local: `capabilities`, `manage_tools`, `generate_skill`,
  `bridge_status`, `restart_editor`, `resource_pressure`, `jobs`,
  `playtest_session`, `playtest_explore`, `playtest_devices` and
  `playtest_replay` (their bridge calls go through the normal route; a crawl, a
  device run or a replay is a `jobs` job; replay's `export` / `list` / `show`
  touch only scenario files in the project),
  `hub_*`, and
  event-pull meta-tools.
- Always offline when transitive impact is requested: `dependencies` with
  `include_impact=true`.

Offline-first tools such as `find_references`, `read_asset`, and
`search_assets` can still enter through the live/compressible router when the
bridge is reachable. For text-serialized assets the implementation may parse
disk data without contacting the bridge; `_route.route` then reports
`offline` (matching `_source`) so the two tags never disagree about where the
payload came from. Binary formats fall back to the live bridge and report
`_route.route: "live"`. Error bodies tag their origin the same way: a
caller-side validation failure reports `_route.route: "local"`, and a failed
offline-first attempt with the bridge down reports `offline` — never `live`
for a payload the bridge never produced.

## Offline coverage

Without a live bridge, these tools retain useful disk-backed behavior:

- `list_assets`
- `read_asset`
- `search_assets`
- `find_references`
- `dependencies`
- `read_compile_errors`

These tools also skip the [mid-reload wait](#calls-during-a-compile-reload-or-play-mode-change):
while the instance lock reports a compiling or reloading Editor they still
reach their handler (and its disk fallback) at once.

`read_asset` normalizes forward and backward slashes before both disk reads and
live fallback. Its session cache checks the asset's modification time, its own
metadata GUID, and the current paths of referenced GUIDs before reusing an
offline model. Renaming, deleting, or restoring referenced assets invalidates
that model even if the requested asset did not change. Dependency validation
re-reads the GUID index for models with references; the cache still avoids
re-parsing the requested asset. No persistent disk cache is created.

The asset parser supports text-serialized Unity YAML such as scenes, prefabs,
materials, controllers, animations, presets, SpriteAtlases, TerrainLayers, and
VFX assets. It also understands JSON-backed asmdef and Shader Graph assets.
Asmdefs are validated as a single JSON document. Shader Graph document streams
report `malformed_json` for unfinished objects or non-whitespace between or after
documents, even when preceding objects parsed successfully. Binary content such
as PNG, WAV, and FBX data still needs the live bridge.

Offline reads can reconstruct GameObject/component trees, prefab variant
overrides, GUID references, and integrity signals. Project-wide integrity scans
also detect orphaned meta files (a `.meta` whose companion file or folder is
gone; a folder's own `.meta` is not an orphan), duplicate GUIDs, missing
references, and missing scripts.

GUIDs resolve offline the way Unity resolves them: against the metas under
`Assets/`, Unity's built-in resources (`Library/unity default resources`,
`Resources/unity_builtin_extra`), and — for a GUID that `Assets/` does not own —
the metas of the project's packages: embedded folders under `Packages/`, `file:`
references in `Packages/manifest.json`, `Library/PackageCache`, and built-in
packages of the project's Unity version when that version is installed. A
component's script (`m_Script`) is looked up in the package `.cs` / `.dll`
metas; a field reference (a URP material's shader, a renderer's default
texture, a settings asset's compute shaders) in every package meta. The package
lookup runs only when some GUID is not resolved by `Assets/` and stops once all
are found. A uGUI `Image` is therefore named and resolved, and a material's URP
shader resolves to `Packages/com.unity.render-pipelines.universal/…`, instead of
being reported missing. A GUID that resolves nowhere is `missing_script_reference`
(error) for a script and `missing_reference` (warning) for a field — unless a
package the project uses is not on disk where these reads look (no `Library/`
in a fresh clone, a built-in package of a Unity version not installed here, a
`file:` path that does not exist): then it is `unverified_script_reference` /
`unverified_reference` (warning), whose detail names those packages.
`read_asset`, `dependencies`, `find_references` (also by a `Packages/<name>/…`
asset path), `search_assets` (`component`) and the offline integrity scan share
this resolution.

`dependencies` combines forward edges, reverse references, broken GUIDs, and
cycles. `include_impact=true` computes the transitive reverse closure offline.
JSON or binary queried assets may skip forward parsing while retaining reverse
references. Offline, an edge that cannot be verified (see above) is listed in
`unverifiedForwardGuids` with `unverifiedReason`, not in `brokenForwardGuids`.

## Lifecycle policy

Every tool declares `capabilities.tools[].lifecycle` and may add a
`lifecycleNote`. The complete taxonomy is also returned as
`capabilities.lifecycleBlock`.

| Class | Concern | Recovery |
|---|---|---|
| `none` | Read-only or side-effect-free operation. | Retry only when the underlying error is transient. |
| `compile-reload` | Script, asmdef, package, scene-open, or settings work can trigger compilation/domain reload. | Wait with `editor_status {"wait_until":"idle"}` (pass the result's `compile.generationAtStart` as `after_generation`): it outlives the reload and returns the compile status and error count. Do not repeat a mutation blindly. |
| `modal-dialog` | The operation or Unity startup can raise an OS modal. | Follow [Dialog policy](../dialog-policy.md); obtain operator consent for destructive/irreversible choices. |
| `scene-dirty` | The operation mutates scene, prefab, hierarchy, or asset state. | Read the gate delta; save/discard dirty scenes or deliberately accept the risk. |
| `process-stale` | A long operation can pause bridge responses/heartbeat. | Wait and re-probe before declaring the bridge dead. |

### Compile and reload constraints

- `compile_check` is batch-only and cannot open a project already locked by a
  live Editor. Use `read_compile_errors` or a live scoped validation when the
  Editor must remain open.
- Local package source is outside the normal `Assets/` watch root.
  `reimport_package` reports DLL mtimes so an incremental compile no-op is
  detectable.
- `execute_csharp` runs synchronously on Unity's main thread. Never block on a
  callback, task result, wait handle, sleep loop, or TestRunner API. Prefer a
  typed tool or fire-and-poll workflow.
- An empty HTTP 200 body from a compile-reload tool is returned as
  `status: "triggered_reload"`. The mutation likely committed before the
  domain reload tore down the response. Verify post-state; do not retry it.
- The gate's checkpoint store lives in memory. A domain reload (the same
  recompile a `restart_then_settle` mutation triggers) clears it, so a later
  `delta` call against a pre-reload `checkpoint_id` cannot compare and the
  response carries `checkpointLostOnReload: true`. Capture a fresh checkpoint
  after any reload and validate with `validate_edit` / `scan_paths` instead of
  relying on a stale `delta`.

### Scene identity

`scene_set_active`, `scene_unload`, `scene_save`, and `scene_get_data` resolve
an opened scene by asset `path` first and display `name` second. Prefer paths.
For `scene_save`, a path that does not identify an open scene is treated as a
save-as destination.

## Batch behavior

Headless batch is for fallback and automation:

- `find_members`, `execute_csharp`, `invoke_method`, and the allowed
  `execute_menu` subset can run in batch.
- Mutating power tools skip the live gate in batch because the interactive
  checkpoint/validate/delta flow is unavailable.
- `execute_menu` allows only batch-viable operations such as asset refresh,
  reimport, and save.
- Agent senses require a live Editor.
- `UNITY_PROJECT_PATH` is required. Before every spawn, the server reads the
  exact editor version from `ProjectSettings/ProjectVersion.txt` and selects
  only that installed version. A missing exact install fails closed without
  opening the project. Installed editors are rescanned when the project version,
  `UNITY_PATH`, `UNITY_HUB`, or the mismatch opt-in changes, and a failed lookup
  is retried on every spawn, so a project upgrade or a newly installed exact
  editor takes effect without restarting the server. Discovery's headless
  availability reuses a failed lookup for up to 30 seconds before rescanning.
  `UNITY_PATH` may pin an executable, but a recognizable mismatched Hub path is
  refused too, including after the project moves to another version. The
  explicit escape hatch `UNITY_OPEN_MCP_ALLOW_VERSION_MISMATCH=1` restores
  mismatch fallback for an intentional upgrade or compatibility test.

`unity_senses_run_tests` has no MCP batch route. For Unity's own headless test
runner, omit `-quit`; the runner exits itself after writing results. Unity test
exit codes differ from the Unity Open MCP CLI contract: `0` all passed, `2`
test failures, `3` runner setup failure.

### Live `batch_execute`

`unity_open_mcp_batch_execute` is distinct from headless batch. It sends one
request to an already-open Editor and runs typed commands sequentially.

| Field | Required | Notes |
|---|---|---|
| `commands` | yes | Non-empty `{ tool, params }[]`. |
| `paths_hint` | yes | Union of project paths the batch may touch. |
| `fail_fast` | no | Defaults to `true`. |
| `gate` | no | `enforce`, `warn`, or `off`; defaults to `enforce`. |
| `parallel` | no | Ignored; Unity execution remains sequential. |

The batch uses one gate cycle and one undo group. Successful earlier steps are
not rolled back when a later step fails; undo the group when needed. Power
tools and local-only meta-tools cannot be nested. The default limit is 25
commands and the hard maximum is 100.

Nestability is decided by `batch_execute`'s pre-flight refusals, **not** by the
per-tool `batchCapable` capability flag (that flag is about the headless
batch-spawn fallback above — a different axis). Every pre-flight refusal happens
before the dispatch loop, so `batch.results[]` is empty and nothing was
committed; `agentNextSteps` says exactly that rather than offering the
partial-failure/`editor_undo` guidance. `unity_senses_run_tests`,
`unity_open_mcp_inputsim_gesture`, `unity_open_mcp_playtest_wait`,
`unity_senses_capture_series` and `unity_open_mcp_game_view` with
`set_resolution` / `set_device` are refused with
`batch_step_requires_server_poll` — their terminal result comes from the server
polling (a results file, or the play-mode operation), which only the top-level
route does (`game_view` `get` / `list_devices` stay batchable). Where a concrete
meta-tool equivalent exists the refusal names it, so a client that ignores
`tools/list_changed` (and therefore cannot see a freshly activated tool) is not
pointed at a top-level call it cannot make.

## Common recovery codes

| Code | Meaning | Recovery |
|---|---|---|
| `bridge_offline` / `bridge_unavailable` | No reachable live bridge. | Open the project, check path/port, or use supported offline tools. |
| `bridge_compile_failed` | Unity is alive, the bridge is down, and `Editor.log` shows it cannot come back on its own: Unity is in Safe Mode, the last domain reload finished without the bridge starting, or (no instance lock yet) the startup compile failed. Carries `evidence: { kind, detail, logPath, errors? }`. A stopped heartbeat alone is never reported as this code — see [lost bridge vs. long reload](#a-lost-response-or-a-long-reload-is-not-a-dead-bridge). | Call `read_compile_errors`; restart Unity for editor resource exhaustion. |
| `response_lost` | The request reached the bridge but its response never arrived (the connection closed, or nothing came back before the call's deadline) while the Editor looked settled, and `Editor.log` shows no compile failure. `dispatched: null`, `cause` (`connection_closed` / `timeout`), `retryAfterMs`. | Wait with `editor_status {"wait_until":"idle"}`, then re-send a read; check a mutation's effect before repeating it. Never replayed automatically. |
| `main_thread_blocked` | A modal or long editor operation prevented dispatch. | Dismiss the modal, check dirty scenes, then re-probe; do not only raise the timeout. |
| `editor_instance_locked` | Headless Unity cannot open a project held by the Editor. | Read the message's diagnosis: a fresh instance lock means the live bridge should be reachable (retry the live route); a live process with no listener means a booting Editor — wait and retry; no matching process means a stale `Temp/UnityLockfile`. The error names the invoked tool, and `agentNextSteps` carries the variant-specific remedy. |
| `unity_not_discovered` | No Unity executable was found. | Install under a standard Hub path or set `UNITY_PATH`. |
| `unity_version_not_installed` | The exact version in `ProjectVersion.txt` is not installed. No Editor was started. | Install that version or intentionally opt in with `UNITY_OPEN_MCP_ALLOW_VERSION_MISMATCH=1`. |
| `unity_version_mismatch` | `UNITY_PATH` identifies a different Hub Editor version than the project requires. No Editor was started. | Correct `UNITY_PATH` or intentionally opt in with `UNITY_OPEN_MCP_ALLOW_VERSION_MISMATCH=1`. |
| `unity_spawn_refused` | The configured Unity binary could not execute. | Correct `UNITY_PATH`; do not retry unchanged. |
| `restart_signature_absent` | `restart_editor` was asked to kill but the `editor_fd_exhaustion` signature is NOT in the recent Editor.log tail. | Re-run `read_compile_errors`; do not kill the Editor for a fixable compile failure. A call with `confirm` absent/false is NOT an error — it returns a structured dry-run preview the agent can inspect before committing. |
| `unity_process_not_found` | `restart_editor` / `resource_pressure` could not resolve a live Unity PID for this project. The process scan skips headless children this MCP server spawned. | Open Unity for this project (with `-projectPath`), or pass an explicit `pid` to `resource_pressure`. |
| `headless_run_in_progress` | `restart_editor` / `resource_pressure` found no interactive Editor for this project while this MCP server's own headless run (e.g. `compile_check`) holds it. Carries `tool`, `operation`, `pid`, `startedAt`, `elapsedMs`, `timeoutMs`; a `stopped` `bridge_status` reports the same data in a `headlessRun` block. | Wait for the pending headless call to return (it exits when finished or at `timeoutMs`); do not relaunch Unity via the Hub meanwhile. `restart_editor` never targets the headless child; pass an explicit `pid` to `resource_pressure` to sample it anyway. |
| `compile_indeterminate` | `compile_check` exited 0 without a report and secondary evidence cannot certify compilation. | Inspect its captured log tail and assembly evidence; exit 0 alone is not success. |
| `batch_aborted` | Unity exited nonzero before a report with no recognized compiler/package/spawn cause. | Inspect the captured output tail. |
| `compile_failed` | The child emitted compiler diagnostics before its report. | Fix those diagnostics, then retry. |
| `project_load_failed` | Headless Unity stopped at package/project resolution before its entry point; no compile ran. | Read the Package Manager notice at the end of the captured output tail (the error also names offending packages), fix `Packages/manifest.json`, then retry. |
| `editor_reloading` | The Editor was still compiling, reloading or changing play mode when the call's [wait budget](#one-wait-budget-per-call) ran out (`dispatched: false`, nothing ran), or a reload closed the response of a call that may have run (`dispatched: true` / `null`). A reload whose heartbeat stopped longer than the stale threshold without `Editor.log` evidence of a dead bridge is one of these too (`heartbeatStalledMs`). | Wait with `editor_status {"wait_until":"idle"}`, then retry a `dispatched: false` call; verify the effect of a `dispatched: true` / `null` mutation before repeating it. Carries `retryAfterMs` and `transition`. Never launches headless Unity. |
| `batch_in_progress` | Another headless operation owns the project: one this MCP server is driving, or a `-batchmode` Unity another process (a second MCP server, CI) has open on it. Checked before the Editor-ownership checks, so a headless Unity is never reported as `editor_instance_locked`; the message names the foreign process when there is one. | Wait for it to finish. |
| `markers_missing` | A non-compile batch operation exited 0 without its report. | Inspect the captured output tail and the operation's post-state before repeating a mutation. |
| `batch_spawn_failed` | Headless Unity produced no classifiable result (non-zero exit, no markers). | Inspect compile errors, package state, project lock, and path. |
| `batch_step_requires_server_poll` | A `batch_execute` step's terminal result is produced by the server polling a results file, which the batch route does not do (today: `unity_senses_run_tests`, `unity_open_mcp_inputsim_gesture`, `unity_open_mcp_playtest_wait`, `unity_senses_capture_series`, `unity_open_mcp_game_view` set actions). | Call the tool as a single top-level call. The message also names a reachable `execute_csharp` / `invoke_method` equivalent for clients that cannot see the tool. |
| `scene_dirty` | A disruptive call was refused because a loaded scene has unsaved work, including unsaved additive scenes. The guard covers every `restart_then_settle` call (recompile, package and asmdef edits, build target/defines, player settings, menus, single-mode scene switches, upgrade, and an `execute_csharp` snippet whose text can disrupt the Editor) plus `editor_set_state` play and `run_tests`; each publishes `ignore_scene_dirty`. A snippet that only writes assets is not refused, and one that saves scenes is refused only for an untitled dirty scene — see [`execute_csharp`](mcp-tools.md#unity_open_mcp_execute_csharp). | Follow `agentNextSteps`, in order: save with `scene_save` (the step carries the call and the dirty scene's path; an untitled scene gets a save-as path), or discard by reopening the scene with `scene_open` + `ignore_scene_dirty: true`, or deliberately retry with `ignore_scene_dirty: true` and accept Unity's native save prompt. |
| `open_scene_changed_on_disk` | A refresh (`assets_refresh`, `execute_menu "Assets/Refresh"`, the refresh `recompile_scripts` runs for a deleted script, an `execute_csharp` snippet that calls `AssetDatabase.Refresh` / `ImportAsset` — refused while anything open changed on disk) was refused before it ran: an open scene changed on disk and has unsaved changes, its file is gone, play mode is on, or a prefab stage with unsaved changes has its prefab changed. Refreshing would raise Unity's native "changed on disk" dialog, which blocks the Editor until a human answers it. A clean changed scene is not refused: it is reloaded in place (`reloadedScenes`). Carries `scenes: [{path, reason}]` and `prefabStage`. | Follow `agentNextSteps`: `scene_save` keeps the edits (overwrites the disk change), `scene_open` + `ignore_scene_dirty: true` takes the disk version; stop play mode; `prefab_save` / `prefab_close {"save": false}` for the prefab stage. Then retry. See [refreshes and open scenes](mcp-tools.md#refreshes-and-open-scenes-changed-on-disk). |
| `bridge_response_unparsable` | A bridge response could not be parsed while the Editor was settled (no reload or play-mode change explains it). | Do not trust partial output; check bridge health before retrying. A response cut off by a reload is `editor_reloading` / `triggered_reload` instead. |

A failed tool POST is replayed only for proven pre-connection failures
(`ECONNREFUSED`, DNS lookup failures, or a connect timeout): the client waits
for the listener from the call's wait budget and sends it again. A socket
reset, response timeout, or changed bridge endpoint cannot establish that a
mutation did not commit. Those are classified at once — no wait — as
`triggered_reload` (a compile-reload call whose reload is underway),
`editor_reloading` (`dispatched: null`, a reload is underway),
`bridge_compile_failed` (only with `Editor.log` evidence) or `response_lost`,
and are never replayed automatically: inspect the post-state before retrying,
even if `/ping` succeeds again. Explicit compiling 503 responses retain their
bounded settle-and-retry behavior.

### One wait budget per call

A live call waits for an Editor that is not ready from **one** budget,
`UNITY_OPEN_MCP_RELOAD_WAIT_MS` (default 30000; `0` refuses at once): the
router's wait before dispatch, the client's readiness wait, the wait before
re-sending a call the bridge refused with 503, and the wait for a listener
that refused the connection all draw from the same deadline, so they cannot
stack. Past it the call answers `editor_reloading` (`dispatched: false`) or
`bridge_offline`. The tool's own run time is separate: it is bounded by its
`timeout_ms` (clamped to 55 s) plus 10 s of transport slack, after which a
missing answer is `response_lost` (`cause: "timeout"`). A live call therefore
ends within about `reload wait + timeout_ms + 10 s`; longer work continues as a
[`jobs`](jobs.md) job where the tool supports it (`editor_status` waits,
compiles, test runs, play-mode operations).

Other retry tunables: `UNITY_OPEN_MCP_COMPILE_WAIT_MS` (an extra cap on the
client's readiness wait, default 120000 — the call budget is normally the
tighter one), `UNITY_OPEN_MCP_COMPILE_POLL_INTERVAL_MS`,
`UNITY_OPEN_MCP_TRANSIENT_RETRY_ATTEMPTS` and
`UNITY_OPEN_MCP_TRANSIENT_BACKOFF_MS`.

### Calls during a compile, reload or play-mode change

A compile ends in a domain reload that tears the bridge's listener down, and
entering play mode reloads it too. The MCP server reads the Editor from outside
for the whole window — the instance lock (heartbeat `state`, `isPlaying` /
`isCompiling`, and a `compile` block with the last generation, `failed`,
`errorCount` and `pending`; the new domain's first write already carries the
Editor's state and that block, so a reload in play mode reads `playing` and a
reload while entering it `entering_playmode` — never `idle` — and the generation
never reads `null`), one `/ping` probe (`transition`) and, once settled,
`/compile-state` — through one reader shared by `bridge_status`,
`editor_status`, `ping`, the policy below and the CLI `wait-for-ready`.

- **Status always answers.** `editor_status` and `ping` answer from the lock and
  `/ping` (`source: "lock"`, `reloading: true`, `transition`,
  `compileGeneration`, `retryAfterMs`) when the bridge cannot.
- **One wait.** `editor_status {"wait_until":"idle"|"playing"|"edit"}` blocks
  until the Editor is settled in that mode with no compile due and reports the
  compile generation, status and error count
  ([contract](mcp-tools.md#unity_open_mcp_editor_status)); past 40 s it continues
  as a [`jobs`](jobs.md) wait.
- **The compile handshake.** Unity starts a requested compile on a later update,
  and `isCompiling` can read false in the gap. A call that leaves a compile
  requested records it (`pending` in the lock and `/compile-state`, cleared when
  the next compile starts); its envelope's `compile.generationAtStart` lets the
  caller require a compile after it (`after_generation`). The wait holds while
  a compile is pending (up to 60 s from the request).
- **Calls that arrive mid-transition wait.** While the lock shows a compile,
  reload, play-mode change or a pending compile, a call (other than the local,
  offline-capable and status tools above) waits for the Editor to settle —
  bounded by `UNITY_OPEN_MCP_RELOAD_WAIT_MS` (default 30 s) so it stays inside a
  host's request timeout — and is then dispatched; past the budget it is refused
  with `editor_reloading` (`dispatched: false`, `retryAfterMs`, the wait call).
  A `_meta.port` override aimed at another Editor is not held by the configured
  project's lock.
- **The bridge never runs a mutation into a transition.** A mutation that
  reaches the main thread while a compile is running or due, or a play-mode
  change is pending, is answered HTTP 503 `editor_reloading` with
  `dispatched: false`; the client waits for the Editor (bounded) and sends it
  once more — nothing ran, so the resend is safe.
- **A reload never reads as corruption.** A tool call whose response a domain
  reload cuts off is answered with a well-formed body before the listener stops:
  HTTP 503 `editor_reloading` (`dispatched: false`) when its work never reached
  the main thread, HTTP 200 `editor_reloading` with `dispatched: true` (and the
  call's `compile.generationAtStart`) when it ran. A compile-reload call (and
  `assets_refresh`) reads the latter as `triggered_reload` (its expected end,
  with the wait keyed to that generation — `recompile_scripts` and
  `assets_refresh` run that wait themselves and answer with the compile
  result); any other call gets
  `editor_reloading` — verify a mutation's effect before repeating it. An empty
  body while the lock or `/ping` shows a transition maps to `editor_reloading`
  with `dispatched: null`; only an unparsable body from a settled Editor stays
  `bridge_response_unparsable`.
- **A call that requests a reload is answered first.** When a call's dispatch
  leaves a domain reload requested (`EditorUtility.RequestScriptReload` from an
  `execute_csharp` snippet or an invoked method), the bridge holds the reload
  until the response is written, so the result arrives with
  `reloadRequested: true` and `reloadTiming`, plus the wait to make. Until the
  reload starts the lock state and `/ping` `transition` read `reloading` and
  mutations are refused, so the next call waits for it instead of being cut
  off. In play mode the reload runs and play continues ("Script Changes While
  Playing": *Recompile And Continue Playing* or *Stop Playing And Recompile*);
  under *Recompile After Finished Playing* Unity defers it to the end of play
  mode (`reloadTiming: "after_play_mode"`), and calls keep running meanwhile.
  An `execute_csharp` snippet that leaves a compile due is answered without the
  settle wait too, with `compile.generationAtStart` for
  `editor_status {"wait_until":"idle","after_generation":N}`.

### A lost response or a long reload is not a dead bridge

The instance lock's heartbeat is written from the Editor's main thread, so it
stops during every domain reload. A reload that outlives the 10 s stale
threshold — a large project, a play-mode reload, a reload a test run deferred
to its end — looks exactly like a bridge assembly that never came back: a live
Unity process with a frozen heartbeat. The server tells them apart only with
evidence from the project's `Editor.log`:

- **Safe Mode** (`Safe Mode: …` logged after the bridge last started);
- **the last reload finished without the bridge** (`Begin MonoManager
  ReloadAssembly` → reload end, with no `[Unity Open MCP Bridge] Listening on`
  in between: its assembly failed to compile or load, or was removed); current
  compiler errors, the bridge's own first, ride along;
- with no instance lock yet, **the startup compile's current errors**.

With that evidence the answer is `bridge_compile_failed` (and `bridge_status`
reports `dead_bridge` with an `evidence` block). Without it a frozen heartbeat
inside a transition is a reload that runs long: calls wait for it from their
budget, `editor_status` waits through it, and the answers carry
`heartbeatStalledMs`; outside a transition it is an unreachable listener
(`bridge_status` `unreachable`, a retryable `bridge_offline`). Compiler errors
alone are not evidence while a reload runs: Unity does not reload after a
failed compile, so the old domain — and the bridge — keep running, and a
reload in progress loads the last good assemblies.

## Multi-agent scheduling

A call can use `_meta.port` to target a specific bridge and `_meta.agentId` to
override agent identity. These routing fields are removed before the tool
arguments reach Unity.

A `_meta.port` call is diagnosed against the Editor whose instance lock claims
that port, not the configured project. A lock with a live PID is preferred;
otherwise the most recent stale lock claiming the port is used. That lock
supplies the bearer token and drives `bridge_compile_failed`, cold Safe Mode
detection, the ping `lockCheck` note and the offline hint. If no lock claims
the port, the configured project is used only when it has no lock and the port
is its deterministic port. Otherwise an unreachable override bridge reports
plain `bridge_offline` and never picks up another Editor's state.

When multiple agent identities share a bridge, the bridge can enable its fair
round-robin queue: several reads and one serialized write per Editor frame.
Single-agent traffic bypasses this scheduling path. Configure it in
`.unity-open-mcp/settings.json` with `fairQueueEnabled`,
`fairQueueReadsPerFrame`, `editorSettleCapMs`, and `restartSettleCapMs`.

## Operator bridge status

`unity_open_mcp_bridge_status` is an always-visible local tool for coarse
operator health:

- `running`
- `compiling`
- `stopped`
- `dead_bridge`
- `unreachable`
- `wedged`

It combines the instance-lock classifier with one ping probe — the same
[editor-state reader](#calls-during-a-compile-reload-or-play-mode-change)
`editor_status` uses — and never errors merely because the bridge is offline. It
also reports `transition`, `compileGeneration` and `compilePending`.
`dead_bridge` needs `Editor.log` evidence that the bridge cannot come back
([above](#a-lost-response-or-a-long-reload-is-not-a-dead-bridge)) and carries
it as `evidence`, with a `recoveryHint` pointing to
`unity_open_mcp_read_compile_errors`. A frozen heartbeat without evidence (a
long reload, an Editor still starting) is `unreachable`, with
`heartbeatStalledMs`.

`running` means **the listener answers**, not **the Editor can compile**. Two
failure modes leave the process, the heartbeat and `/ping` all looking healthy
while the Editor is unusable; both report `wedged` with a `wedged: { reason,
detail }` block and a non-null `recoveryHint`:

- `editor_fd_exhaustion` — the Bee build driver died on Mono's fd ceiling.
  `Library/ScriptAssemblies` stops updating, so C# edits never take effect and
  `execute_csharp` keeps running the previous assembly. Detected by folding the
  `read_compile_errors` red-flag scan (a regex over the freshest `Editor.log`
  tail) into this tool, so the two can no longer disagree. Only a restart
  recovers.
- `main_thread_wedged` — a modal dialog is blocking Unity's message pump. The
  heartbeat is written from `EditorApplication.update`, so it goes stale and the
  lock classifier says `dead_bridge`; but the HTTP listener runs on its own
  thread and keeps answering, which a bridge assembly that actually failed to
  compile could never do. A reachable `/ping` alongside a stale heartbeat is
  therefore the discriminator. No tool can dismiss a modal — an operator must.

`classification` keeps mirroring the instance lock verbatim
(`healthy | reloading | dead_bridge | gone`); the wedge is carried by the tool's
own `status` token plus the `wedged` block.

### Stale install vs. regression

When `/ping` is reachable the response also carries a `wireContract` block:

```json
{ "bridge": 1, "expected": 1, "stale": false, "note": null }
```

`wireContract` is an integer the bridge bumps for every observable change to the
request/response contract (accepted parameter keys, error codes, envelope fields,
declared schema ceilings) — the things `bridgeVersion` does **not** move for.
Without it, a tool failing in a way the docs describe as already fixed is
indistinguishable from a regression, because a stale install and a current one
report the same semver.

- `stale: true` — the installed Unity bridge package predates this MCP server's
  contract. Reinstall/update it and retry before reporting anything. A bridge old
  enough to omit the field reports `bridge: null, stale: true`.
- `stale: false` — the pair is aligned, so a failure the docs call fixed is a real
  regression worth reporting.

The block is `null` when `/ping` was unreachable (there is nothing to compare).

### `ping` provenance

A successful `unity_open_mcp_ping` answer is always the result of a fresh HTTP
probe, never a lock-derived guess — and the response says so: `source:
"probe"` plus `asOf` (when the probe was answered), so an agent can re-probe
before acting on a possibly-seconds-stale `connected: true`. While the
listener is down for a compile, reload or play-mode change (or answers 503 while
its session initializes) and the lock shows a live Editor, ping answers from the
lock: `source: "lock"`, `connected: false`, `reloading: true`, `transition`,
`compileGeneration`, `retryAfterMs` — not an error. The batch-route fallback
ping (no live Editor, no probe possible) reports `source: "unprobed"` with
`connected: false`. When the probe answers but the
project's instance lock is `gone`, the response additionally carries a
`lockCheck: { classification, note }` warning — the answering listener may be
a dying Editor mid-shutdown or a foreign process on the port, so
`connected: true` plus `bridge_status: "stopped"` stops reading as a
contradiction: do not route live mutations off that ping alone.

Bridge start/stop tools are not exposed. Start/stop currently exists only in
the Unity toolbar, and stopping through the same HTTP listener would risk
tearing down the connection before its response is delivered.

## Editor fd-exhaustion recovery and prediction

Two always-visible local tools cover the Bee build-driver fd-exhaustion hang
(`System.NotSupportedException: Could not register to wait for file descriptor
N`) that wedges a long-running Editor after many domain reloads. The bridge is
the thing that dies in this failure mode, so both tools act on the OS process
and never depend on a reachable bridge.

- **`unity_open_mcp_restart_editor`** (reactive). After `read_compile_errors`
  reports an `editor_fd_exhaustion` issue, terminate the hung Unity with
  explicit `confirm: true`. The tool refuses when the signature is absent (no
  restart on a fixable compile failure) and surfaces unsaved-scene risk when
  the bridge is still reachable. SIGTERM → SIGKILL on macOS/Linux,
  `taskkill /T /F` on Windows. Relaunch is NOT automatic — the response tells
  the operator to relaunch via the Hub (the interactive-Editor launch recipe
  is not knowable from the server).
- **`unity_open_mcp_resource_pressure`** (proactive). Sample the live Unity
  process's fd usage and report headroom against the Mono fd ceiling (default
  ~1024 — the real trip point, NOT the OS soft limit `ulimit -n` /
  `launchctl limit maxfiles`, which is only a loose upper bound and is
  misleading for a GUI-launched Unity on macOS). Returns `state` (`ok` / `warn`
  at ≥80% / `critical` at ≥90% / `over_ceiling` / `unknown`), a `trend`
  (`stable` / `rising` / `leaking` — a monotonic climb across successive
  samples is the leak signature), `ceiling` + `ceilingSource` (`"default"` vs
  `"config"`), and the session-scoped sample ring. No disk cache — samples are
  in-memory and clear on server restart. **`over_ceiling` semantics:** with a
  real fd count (`fdMethod` lsof/proc — only numbered descriptors are counted,
  never mmap/txt rows), at/past the ceiling every NEW descriptor number lands
  above it and the next Mono IOSelector registration can hang the Editor, so
  the response carries a **critical-level `warning`** (a fresh healthy Unity 6
  editor sits at ~160 fds). The one soft case is the Windows `HandleCount`
  probe — handle counts cover kernel/GDI/user objects and routinely exceed
  1024 on a healthy process, so an over-ceiling handle count carries an
  informational `pressureNote` and only a `leaking` trend alarms there. A
  timed-out `lsof` (`partial: true`) is NOT soft: its count is a lower bound
  on real fds, so the normal thresholds apply and the true pressure is only
  ever higher. Override the ceiling for a runtime whose internal limit differs
  from Mono's 1024 (e.g. Unity 6 / CoreCLR) via `.unity-open-mcp/settings.json`
  (`resourcePressure.fdCeiling`) — the bridge's in-band `execute_csharp`
  advisory reads the same key, so both surfaces alarm at the same pressure.

Use `resource_pressure` after heavy automation (many recompiles / domain
reloads) to catch fd growth before the Editor hangs; escalate to
`read_compile_errors` → `restart_editor` once the hang has happened.

### Compile evidence and response integrity

Headless runs capture Unity output with `-logFile -`, preserving the live Editor's
log. Compiler, package/project-load, spawn, and unknown pre-report failures have
distinct codes and an actionable output tail. That tail is the run's only log:
the child never writes `Editor.log`, so `read_compile_errors` cannot show it.
Exit-0 `compile_check` without JSON markers succeeds only when its own log
confirms completion, assemblies advanced during that child run, and the disk
staleness check is clear; otherwise it returns `compile_indeterminate`. Process
discovery also blocks a spawn during cold Safe Mode before a bridge lock exists.
Editor recovery remains PID-specific.

Live compile verification records generation and before/after assembly timestamps.
A completed generation with matching input content can legitimately retain DLL
mtimes; subsequent source changes invalidate that confirmation.

JSON HTTP replies are validated before headers, sent once per request, and close
the connection. `X-Request-Id` correlates requests and responses. A mismatched
response is discarded with `bridge_response_request_mismatch`, without repeating
the operation. Invalid bridge JSON returns `invalid_response_json` rather than a
partial success envelope. Client deadlines cover body reads and abandon unread bodies.

### Request-specific lifecycle

Catalog lifecycle values are conservative defaults. A pure inspection snippet
with `read_only: true`, an explicitly declared `[BridgeReadOnlyMenu]` verifier,
or an all-read `batch_execute` resolves to `none` in its response. Dirty scenes
are left untouched. Known disruptive snippet calls retain the default protection;
`read_only` is an assertion, not proof that arbitrary C# is safe. A writing
snippet whose text shows only asset writes or a scene save resolves to
`editor_settle` and skips the dirty-scene preflight; one that can compile,
reload, import, switch scenes or change play mode — or resolves a member by a
computed name — keeps `restart_then_settle`
([classification](mcp-tools.md#unity_open_mcp_execute_csharp)). Mixed batches
retain `editor_settle`, union scope, and one gate/undo group. Nested reload and
server-polled operations are rejected during preflight before step zero.

## Project commands and jobs

`project_commands` list/describe read the authenticated live catalog; invoke uses
the live gate/lifecycle pipeline. They never spawn batch Unity. Describe the exact
id before invocation and pin its schema version when retaining a planned call.

`jobs` is an always-visible local orchestration surface. Start delegates only to
an explicitly supported live adapter; status/wait/cancel/list address the retained
session-owned record. Start acknowledgement is not completion: call status, then
bounded wait until terminal. Wait timeout does not cancel the native operation.
Project commands losing their domain-local record become orphaned; test jobs can
survive PlayMode reload through the same run-id result-file handoff. Neither path
replays an ambiguous start. See [project commands](project-commands.md) and
[jobs](jobs.md) for ownership, retention and cancellation contracts.
