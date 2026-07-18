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
- Always offline: `list_assets`, `read_compile_errors`.
- Always local: `capabilities`, `manage_tools`, `generate_skill`,
  `bridge_status`, `hub_*`, and event-pull meta-tools.
- Always offline when transitive impact is requested: `dependencies` with
  `include_impact=true`.

Offline-first tools such as `find_references`, `read_asset`, and
`search_assets` can still enter through the live/compressible router when the
bridge is reachable. For text-serialized assets the implementation may parse
disk data while `_route.route` remains `live`; `_source` identifies the actual
data source.

## Offline coverage

Without a live bridge, these tools retain useful disk-backed behavior:

- `list_assets`
- `read_asset`
- `search_assets`
- `find_references`
- `dependencies`
- `read_compile_errors`

The asset parser supports text-serialized Unity YAML such as scenes, prefabs,
materials, controllers, animations, presets, SpriteAtlases, TerrainLayers, and
VFX assets. It also understands JSON-backed asmdef and Shader Graph assets.
Binary content such as PNG, WAV, and FBX data still needs the live bridge.

Offline reads can reconstruct GameObject/component trees, prefab variant
overrides, GUID references, and integrity signals. Project-wide integrity scans
also detect orphaned meta files, duplicate GUIDs, missing references, and
missing scripts.

`dependencies` combines forward edges, reverse references, broken GUIDs, and
cycles. `include_impact=true` computes the transitive reverse closure offline.
JSON or binary queried assets may skip forward parsing while retaining reverse
references.

## Lifecycle policy

Every tool declares `capabilities.tools[].lifecycle` and may add a
`lifecycleNote`. The complete taxonomy is also returned as
`capabilities.lifecycleBlock`.

| Class | Concern | Recovery |
|---|---|---|
| `none` | Read-only or side-effect-free operation. | Retry only when the underlying error is transient. |
| `compile-reload` | Script, asmdef, package, scene-open, or settings work can trigger compilation/domain reload. | Verify post-state with `editor_status`, `bridge_status`, and `read_compile_errors`; do not repeat a mutation blindly. |
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
- `UNITY_PROJECT_PATH` is required; set `UNITY_PATH` when editor discovery
  cannot find the executable.

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

## Common recovery codes

| Code | Meaning | Recovery |
|---|---|---|
| `bridge_offline` / `bridge_unavailable` | No reachable live bridge. | Open the project, check path/port, or use supported offline tools. |
| `bridge_compile_failed` | Unity is alive but the bridge assembly did not reload, often Safe Mode or a compile failure. | Call `read_compile_errors`; restart Unity for editor resource exhaustion. |
| `main_thread_blocked` | A modal or long editor operation prevented dispatch. | Dismiss the modal, check dirty scenes, then re-probe; do not only raise the timeout. |
| `compile_timeout` | Compile wait exceeded. | Wait and re-probe; switch to offline compile errors if the bridge is dead. |
| `editor_instance_locked` | Headless Unity cannot open a project held by the Editor. | Close the Editor or use the live/offline alternative named in `agentNextSteps`. |
| `unity_not_discovered` | No Unity executable was found. | Install under a standard Hub path or set `UNITY_PATH`. |
| `unity_spawn_refused` | The configured Unity binary could not execute. | Correct `UNITY_PATH`; do not retry unchanged. |
| `restart_confirmation_required` | `restart_editor` was called without `confirm: true`. | Re-call with `confirm: true` after `read_compile_errors` confirms an `editor_fd_exhaustion` issue. |
| `restart_signature_absent` | `restart_editor` was asked to kill but the `editor_fd_exhaustion` signature is NOT in the recent Editor.log tail. | Re-run `read_compile_errors`; do not kill the Editor for a fixable compile failure. |
| `unity_process_not_found` | `restart_editor` / `resource_pressure` could not resolve a live Unity PID for this project. | Open Unity for this project (with `-projectPath`), or pass an explicit `pid` to `resource_pressure`. |
| `batch_spawn_failed` | Headless Unity produced no classifiable result. | Inspect compile errors, package state, project lock, and path. |
| `scene_dirty` | A disruptive mutation was refused because a scene has unsaved work. | Save/discard first or deliberately opt into the documented risk. |
| `bridge_response_unparsable` | A substantial bridge response could not be parsed. | Do not trust partial output; check bridge health before retrying. |

Retry tunables include `UNITY_OPEN_MCP_COMPILE_WAIT_MS`,
`UNITY_OPEN_MCP_COMPILE_POLL_INTERVAL_MS`,
`UNITY_OPEN_MCP_TRANSIENT_RETRY_ATTEMPTS`, and
`UNITY_OPEN_MCP_TRANSIENT_BACKOFF_MS`.

## Multi-agent scheduling

A call can use `_meta.port` to target a specific bridge and `_meta.agentId` to
override agent identity. These routing fields are removed before the tool
arguments reach Unity.

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

It combines the instance-lock classifier with one ping probe and never errors
merely because the bridge is offline. `dead_bridge` includes a
`recoveryHint` pointing to `unity_open_mcp_read_compile_errors`.

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
  process's fd usage and report headroom against Mono's internal ~1024 fd
  ceiling — the real trip point, NOT the OS soft limit (`ulimit -n` /
  `launchctl limit maxfiles`), which is only a loose upper bound and is
  misleading for a GUI-launched Unity on macOS. Returns `state` (`ok` / `warn`
  at ≥80% / `critical` at ≥90% / `unknown`), a `trend` (`stable` / `rising` /
  `leaking` — a monotonic climb across successive samples is the leak
  signature), and the session-scoped sample ring. No disk cache — samples are
  in-memory and clear on server restart.

Use `resource_pressure` after heavy automation (many recompiles / domain
reloads) to catch fd growth before the Editor hangs; escalate to
`read_compile_errors` → `restart_editor` once the hang has happened.
