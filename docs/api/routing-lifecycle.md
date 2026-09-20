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
- Live compile snapshot with offline log fallback, never batch: `read_compile_errors`.
- Always local: `capabilities`, `manage_tools`, `generate_skill`,
  `bridge_status`, `restart_editor`, `resource_pressure`, `jobs`, `hub_*`, and
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

Nestability is decided by `batch_execute`'s pre-flight refusals, **not** by the
per-tool `batchCapable` capability flag (that flag is about the headless
batch-spawn fallback above — a different axis). Every pre-flight refusal happens
before the dispatch loop, so `batch.results[]` is empty and nothing was
committed; `agentNextSteps` says exactly that rather than offering the
partial-failure/`editor_undo` guidance. `unity_senses_run_tests` is refused with
`batch_step_requires_server_poll` — its terminal result comes from the server
polling a results file, which only the top-level route does. Where a concrete
meta-tool equivalent exists the refusal names it, so a client that ignores
`tools/list_changed` (and therefore cannot see a freshly activated tool) is not
pointed at a top-level call it cannot make.

## Common recovery codes

| Code | Meaning | Recovery |
|---|---|---|
| `bridge_offline` / `bridge_unavailable` | No reachable live bridge. | Open the project, check path/port, or use supported offline tools. |
| `bridge_compile_failed` | Unity is alive but the bridge assembly did not reload, often Safe Mode or a compile failure. | Call `read_compile_errors`; restart Unity for editor resource exhaustion. |
| `main_thread_blocked` | A modal or long editor operation prevented dispatch. | Dismiss the modal, check dirty scenes, then re-probe; do not only raise the timeout. |
| `compile_timeout` | Compile wait exceeded. | Wait and re-probe; switch to offline compile errors if the bridge is dead. |
| `editor_instance_locked` | Headless Unity cannot open a project held by the Editor. | Read the message's diagnosis: a fresh instance lock means the live bridge should be reachable (retry the live route); a live process with no listener means a booting Editor — wait and retry; no matching process means a stale `Temp/UnityLockfile`. The error names the invoked tool, and `agentNextSteps` carries the variant-specific remedy. |
| `unity_not_discovered` | No Unity executable was found. | Install under a standard Hub path or set `UNITY_PATH`. |
| `unity_spawn_refused` | The configured Unity binary could not execute. | Correct `UNITY_PATH`; do not retry unchanged. |
| `restart_signature_absent` | `restart_editor` was asked to kill but the `editor_fd_exhaustion` signature is NOT in the recent Editor.log tail. | Re-run `read_compile_errors`; do not kill the Editor for a fixable compile failure. A call with `confirm` absent/false is NOT an error — it returns a structured dry-run preview the agent can inspect before committing. |
| `unity_process_not_found` | `restart_editor` / `resource_pressure` could not resolve a live Unity PID for this project. | Open Unity for this project (with `-projectPath`), or pass an explicit `pid` to `resource_pressure`. |
| `compile_indeterminate` | `compile_check` exited 0 without a report and secondary evidence cannot certify compilation. | Inspect its captured log tail and assembly evidence; exit 0 alone is not success. |
| `batch_aborted` | Unity exited nonzero before a report with no recognized compiler/package/spawn cause. | Inspect the captured output tail. |
| `compile_failed` | The child emitted compiler diagnostics before its report. | Fix those diagnostics, then retry. |
| `editor_reloading` | A fresh instance lock identifies a live compiling/reloading Editor. | Retry after `retryAfterMs`; the router makes one bounded live re-probe and never launches headless Unity. |
| `batch_in_progress` | Another headless operation in this MCP server owns the project. | Wait for it to finish. |
| `markers_missing` | A non-compile batch operation exited 0 without its report. | Inspect its post-state before repeating a mutation. |
| `batch_spawn_failed` | Headless Unity produced no classifiable result (non-zero exit, no markers). | Inspect compile errors, package state, project lock, and path. |
| `batch_step_requires_server_poll` | A `batch_execute` step's terminal result is produced by the server polling a results file, which the batch route does not do (today: `unity_senses_run_tests`). | Call the tool as a single top-level call. The message also names a reachable `execute_csharp` / `invoke_method` equivalent for clients that cannot see the tool. |
| `scene_dirty` | A disruptive mutation was refused because a loaded scene has unsaved work, including unsaved additive scenes. | Save/discard first or deliberately opt into the documented risk. |
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
- `wedged`

It combines the instance-lock classifier with one ping probe and never errors
merely because the bridge is offline. `dead_bridge` includes a
`recoveryHint` pointing to `unity_open_mcp_read_compile_errors`.

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
before acting on a possibly-seconds-stale `connected: true`. The batch-route
fallback ping (live route unavailable, no probe possible) reports
`source: "unprobed"` with `connected: false`. When the probe answers but the
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
distinct codes and an actionable output tail. Exit-0 `compile_check` without JSON
markers succeeds only when its own log confirms completion, assemblies advanced
during that child run, and the disk staleness check is clear; otherwise it returns
`compile_indeterminate`. Process discovery also blocks a spawn during cold Safe Mode
before a bridge lock exists. Editor recovery remains PID-specific.

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
`read_only` is an assertion, not proof that arbitrary C# is safe. Mixed batches
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
