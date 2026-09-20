# MCP tools API

`unity-open-mcp` exposes **250+ tools** for Unity editor workflows. This page is
the overview and index; focused pages own session visibility, routing/lifecycle,
and CLI automation.

> **Install / connect.** See [Manual setup](../setup/manual-setup.md) for the
> Unity packages and [MCP client configuration](../setup/client-configuration.md)
> for client paths, snippets, and environment variables.

For exact runtime schemas, call `unity_open_mcp_capabilities`. Source
definitions live in `mcp-server/src/tools/`.

| ![Bridge status](../screenshots/bridge-status.png) | ![Bridge tools](../screenshots/bridge-tools.png) |
|---|---|

## Focused references

| Document | Owns |
|---|---|
| [Tool groups and session visibility](tool-groups.md) | Default groups, `manage_tools` actions (list/activate/deactivate/reset + intent-driven `suggest`/`activate_for`), availability vs activation, reset/restart, and auto-activation. |
| [Routing, offline, and lifecycle contracts](routing-lifecycle.md) | Live/batch/offline/local selection, offline coverage, lifecycle recovery, batch behavior, errors, and multi-agent scheduling. |
| [CLI and automation](cli-automation.md) | CLI commands, options, JSON output, and links to canonical CI behavior. |
| [CI templates](../ci/README.md) | Pipeline shape, CLI exit codes, baselines, and provider templates. |
| [Input simulation](input-simulation.md) | Pointer delivery, device events, frame stepping, and supported input boundaries. |
| [Asynchronous jobs](jobs.md) | Job lifecycle, observation, idempotency, cancellation, retention and adapter contracts. |
| [MCP resources](resources.md) | Resource URIs, payloads, and resource routing. |

## Tool families

- **Core runtime** — ping, C# execution, method invocation, menu calls,
  reflection, compile checks, editor status, and live batch execution.
- **Bridge & Editor recovery** — operator health (`bridge_status`), offline
  compile-error diagnosis (`read_compile_errors`), Editor fd-exhaustion
  recovery (`restart_editor` — requires explicit confirmation, refuses when
  the fd-exhaustion signature is absent), and proactive fd-usage prediction
  (`resource_pressure` — headroom against the Mono fd ceiling (default ~1024,
  configurable) + leak-trend detection; a real fd count at/past the ceiling
  (`over_ceiling` from lsof/proc) warns at critical level; the broader
  Windows HandleCount metric stays informational over the ceiling proxy — in
  the ≥90% band below it, it still warns, with handle-aware wording rather
  than fd-hang claims). All
  local-routed; they act on the OS process and survive a dead bridge.
- **Gate and validation** — validation, checkpoints, deltas, references,
  dependencies, scans, baselines, regression checks, and targeted fixes.
- **Asset intelligence** — reserialize plus structured asset read/search/list.
- **Agent senses** — tests, screenshots, Frame Debugger, console, profiler,
  memory/rendering snapshots, spatial queries, and event pull.
- **Typed editor** — assets, materials, shaders, prefabs, GameObjects,
  components, scenes, packages, selection, undo, tags/layers, scripts,
  ScriptableObjects, asmdefs, build, and project settings.
- **Embedded domains** — navigation, input, ProBuilder, particles, animation,
  splines, lighting, audio, UI, constraints, terrain, Cinemachine, Timeline,
  Tilemap, Shader Graph, VFX Graph, Memory Profiler, 2D art, and input
  simulation (play-mode click/swipe/drag).
- **Discovery** — capabilities, rules, skill generation, and tool-group
  management.
- **Unity Hub control** — editor discovery, release listing, installs, modules,
  and install-path management without a running bridge.

The canonical embedded-domain dependency and activation table is
[Extension domains](../extensions.md).

## Example prompts

Natural-language prompts an agent can act on with the tool surface. Prefer
calling `unity_open_mcp_capabilities` and activating the right tool group before
a multi-step workflow. For session groups, see [Tool groups](tool-groups.md).

| Theme | Example prompt |
|---|---|
| Safety gate | Remove the Enemy prefab — but stop and ask for confirmation if the gate preview shows new missing references. |
| Asset intelligence | Find all Prefabs that reference `PlayerController` and summarize inbound dependencies. |
| Offline / recovery | Bridge is offline — show me the latest compile errors from the Editor log. |
| Typed editor | Create scene `Assets/Scenes/Level1.unity`, add a Player cube, and parent a Camera under it. |
| Embedded domains | Activate `cinemachine` and create a follow camera for the Player. |
| Agent senses | Run Play Mode tests for the Combat assembly and capture a Game-view screenshot on failure. |
| Game testing | Probe the UI for interactables, enter play mode, click the Start button by id, advance a few frames, then screenshot to confirm the level loaded. |
| Tool groups | Reset tool groups, then activate only `core` and `gate-and-verify`. |
| Batch mutations | In one batch: create ten empty GameObjects named `Enemy_1`…`Enemy_10` under an `Enemies` parent. |
| Verify / CI | Create a verify baseline for this project, then report any new blocking issues since the last baseline. |
| Skills | Install or regenerate the Unity Open MCP skill for Cursor in this project. |
| Power tools | Reflect the fields on `PlayerController` and set `moveSpeed` to 8. |
| Hub control | List installed Unity Editors and show which one this project would launch with. |

These are illustrative starting points, not a complete catalog. Exact tool names
and schemas come from `unity_open_mcp_capabilities`.

## Discover tools programmatically

Call `unity_open_mcp_capabilities` first:

```json
{
  "kind": "tools",
  "include_planned": true,
  "page_size": 40
}
```

Important response fields:

- `tools[].name`, `category`, `group`, and `inputSchema`
- `tools[].routePolicy` and `batchCapable`
- `tools[].lifecycle` and optional `lifecycleNote`
- `toolGroups[]` for defaults, activation guidance, and compiled availability
- `routing` for the current routing narrative
- `costHints` for output profiles, page sizes, and recommended tool chains
- `lifecycleBlock` for recovery policy

The response is the authoritative current catalog. Public documentation uses
`250+` instead of a hand-maintained exact total.

### Keeping the discovery call cheap

`capabilities` and `bridge_status` accept an optional `project_path` naming a Unity
project root. It overrides the configured project for that read-only call, including
port/auth discovery, without changing subsequent calls.

`capabilities` honors the same `profile` / `page_size` / `cursor` contract as the
heavy read tools, because the unfolded catalog is ~500 KB on one line (per-tool
`inputSchema` is ~85% of that) and overflows a typical client's per-result
ceiling.

| `profile` | Contains | Rough size |
| --- | --- | --- |
| `compact` (default) | identity + `group` + `routePolicy` + `batchCapable` + `lifecycle`; no `inputSchema`, no descriptions, per-group `tools[]` folded to `toolCount` | ~100 KB |
| `balanced` | adds descriptions back; still no `inputSchema` | ~285 KB |
| `full` | every field, including `inputSchema` | ~515 KB |

`page_size` + `cursor` page the `tools[]` list; the response carries a
`pagination` block with `next_cursor`, plus the echoed `profile` and a
`profileHint` naming what was folded away. The efficient pattern is
`kind: "tools"` + `profile: "compact"` + `page_size` to find the tool, then one
`profile: "full"` call for the single schema you need.

`batchCapable` answers "does this tool have a **headless batch-spawn** fallback
when the bridge is down?" — it does **not** say whether the tool may be a nested
`batch_execute` step. `routing.perToolFlagMeaning` states this inline; nestability
is decided by `batch_execute`'s pre-flight refusals.

## Mutation and gate contract

Mutating tools require a non-empty `paths_hint` scoped to the project assets
they may touch. A live mutation normally runs:

```text
checkpoint → mutate → validate → delta
```

Read `gate.delta`, inline `logs`, and `agentNextSteps` even when the mutation
reports success. Gate modes are:

- `enforce` — fail when validation introduces blocking issues;
- `warn` — report issues without blocking;
- `off` — skip the gate when explicitly supported.

`gate.delta` carries the counts (`newErrors`, `newWarnings`, `resolvedErrors`,
`resolvedWarnings`) plus **bounded** issue-key arrays: `newIssues` /
`resolvedIssues` are capped at the first 25 keys each, with a companion
`newIssuesTruncated` / `resolvedIssuesTruncated` count for the elided tail, and
`newIssuesByRule` / `resolvedIssuesByRule` histograms showing the per-rule
distribution (so a 729-issue delta that is all one noisy rule is visible at a
glance without the ~222 KB a full prefab rebuild used to inline). What an agent
branches on is `mutation.success` + `gate.outcome` + the counts; the full key
list is available via `validate_edit` / `scan_paths` on the touched paths.

`gate.delta` is `null` — never a zeroed object — when no delta was computed.
Three paths land there, with different outcome tokens: a **checkpoint failure**
or a **mutation failure** reports `gate.outcome: "failed"` with
`mutation.success: false` (the MCP result is `isError: true` via the mutation),
and a **validate-scan failure** (the scan threw, or a verify rule threw on
either side of the delta) reports `gate.outcome: "validate_scan_failed"` — the
mutation committed, and the MCP result is still flagged `isError: true`. When a
verify rule threw, `gate.rulesFailed` lists the rule ids whose findings are
missing: verify health manually with `validate_edit` / `scan_paths` instead of
trusting a withheld delta.

`compilePending: true` (next to `settleMs`) is emitted when the editor is still
compiling after the post-mutation settle wait — the gate's delta then reflects
the **pre-compile** state, so a `passed` / `newErrors:0` does not verify the new
code. Poll `editor_status.isCompiling` until false, then `read_compile_errors`.
`agentNextSteps` repeats the advisory.

`execute_csharp` accepts `read_only: true` for pure-read probes (type lookups,
`SessionState` reads, console reads): it waives the `paths_hint` requirement and
skips the gate even if a scope is supplied. Such reads run against dirty scenes
without saving them and return lifecycle `none`. Known compile/refresh,
scene-switch, and play-transition calls retain lifecycle and mutation-scope
protection. This is a caller assertion, not a sandbox or proof of purity:
indirect calls cannot be proven safe. The deny heuristic still applies; use
`read_only` only for inspection without writes or disruptive transitions.

Verifier menu handlers can explicitly declare `[BridgeReadOnlyMenu]` alongside
`[MenuItem("exact/menu/path")]`. The exact registered path then needs no fake
scope, checkpoint, or dirty-scene guard; descendants do not inherit permission.
The declaration promises inspection only, with no writes, scene switch, reload,
or play transition. Existing no-asset-write exemptions such as Refresh and Play
retain their disruption guard and remain unavailable inside batches.

Gate outcome describes validation independently of operation success. A mutation
failure before validation returns `mutation.success: false`, `gate.outcome:
"skipped"`, `gate.skipped: true`, `gate.skippedReason: "mutation_failed"`, and
null `validation`/`delta`. A completed checkpoint ID is retained. Preflight
refusals use `request_rejected`. Partial mutations still validate committed work;
the gate may pass while the batch operation fails. Check both fields. Existing
`gate.delta`, `gate.validation`, and `agentNextSteps` remain readable, and
`effectiveReadOnly` reports the request classification.

`unity_open_mcp_apply_fix` defaults to `dry_run: true`. Review the preview before
applying. Unsafe fixes require an explicit replacement target. A top-level
non-dry-run fix can restore touched files when application fails or introduces
new enforced errors; non-dry-run fixes are refused inside `batch_execute`
because that rollback snapshot is unavailable there. Applying a fix with
`gate: "off"` commits without rollback protection; the response carries
`rollbackDisabled: true` so the mutation is visible and the asset health must
be verified manually afterward.

Every issue can carry:

- `rootCause` — stable code for programmatic branching;
- `evidence` — instance-specific details;
- `fixCandidates` — available fixes and their safety flags;
- `remediation` — human-readable next action.

Copy `issue_id` verbatim from a scan response when applying a fix.

## Output shaping

Heavy tools share:

- `profile`: `compact` (default), `balanced`, or `full`;
- `page_size` to bound a response;
- `cursor` from the previous `pagination.next_cursor`.

Profile-aware tools include `read_asset`, `search_assets`, `scene_get_data`,
`find_references`, `validate_edit`, `scan_paths`, `component_get`, and
`capabilities` itself. `capabilities.costHints` provides recommended starting
page sizes and expected cost bands.

Legacy `detail` and per-tool caps remain aliases when `page_size` is omitted.
Prefer `profile` plus uniform paging for new callers.

Mutating responses include compact per-call Unity console entries in `logs[]`.
Use `unity_senses_read_console` only when the global console buffer and stack
traces are needed.

## Selected tool contracts

### `unity_open_mcp_batch_execute`

Runs multiple typed tools sequentially in one request to an already-open
Editor. All-read batches (including screenshots) need only `commands`, with no
`paths_hint`, checkpoint, gate, or undo group. Mixed/mutating batches require an
explicit union `paths_hint` and use one checkpoint/validation/delta cycle and
one undo group. Every nested schema and lifecycle is checked before step zero,
before scope enforcement or checkpoints. Refusals collect errors across all
steps, including unknown tools, unknown keys, missing fields, invalid values,
reload hazards, and server-polled operations. `fail_fast` defaults to
`true`; `gate` defaults to `enforce`. Successful earlier steps are not
automatically rolled back when a later step fails. Nested steps that resolve
to the `restart_then_settle` lifecycle (`scene_open` Single mode, `package_add`
/ `package_remove`, `asmdef_create` / `asmdef_modify`, `build_set_target` /
`build_set_defines`, `settings_set_player`, `reimport_package`) are refused
with `batch_nested_reload_unsafe` — a domain reload or scene switch mid-batch
would silently abort every later step. `batch_execute` itself and `compile_check`
are also refused as nested steps.

`script_write` and `script_delete` are also refused because importing/deleting
scripts may reload the domain even without a later refresh. The preflight also
reports cross-step script-write/import hazards. Write scripts as top-level
calls, let compilation settle, then continue with a separate batch.

A step whose **terminal result is produced by the MCP server** rather than by the
Editor dispatch is refused with `batch_step_requires_server_poll`.
`unity_senses_run_tests` is the case: it returns `{status: "started", runId}`
immediately and the server polls `test-results-<runId>.json` to turn that into a
real result — a top-level-route behavior the batch path does not have. Nested, the
step would be recorded `success` carrying the non-terminal `"started"` body and the
run's outcome would never reach the caller. Call it top-level.

Pre-flight refusals (`batch_tool_not_invokable`, `batch_nested_reload_unsafe`,
`batch_step_requires_server_poll`, `batch_too_many_commands`,
`batch_invalid_step`, `missing_parameter`) all happen **before** the dispatch
loop: no per-step result exists and nothing was committed. Read the aggregated
`mutation.error.message`; there is nothing to undo. Where a
concrete meta-tool equivalent exists, the refusal message also names it — a
client that ignores `tools/list_changed` cannot see a tool `manage_tools` just
activated, so "use it as a single top-level call" alone would be a dead end for
it. `recompile_scripts` points at the `CompilationPipeline.RequestScriptCompilation()`
`execute_csharp` one-liner plus an `editor_status` poll; `run_tests` points at
`invoke_method` / a `TestRunnerApi` snippet.

Nestability is a separate axis from the capability surface's `batchCapable` flag,
which is about the headless batch-spawn fallback. Read the refusals above, not
that flag, to decide what may be a step.

This is live request batching, not headless Unity fallback. See
[Routing and lifecycle](routing-lifecycle.md).

### Target-selector conventions

The typed tools accept a small, consistent set of target selectors, with
aliases so chaining tools does not require renaming the id field each tool
emits:

| Tool family | Primary selector | Accepted aliases |
| --- | --- | --- |
| `invoke_method` | `object_id` | `instance_id`, `objectId` (what object handles from `scene_snapshot` / `spatial_query` / screenshot emit) |
| `object_get_data` / `object_modify` | `instance_id` | `asset_path` |
| `component_get` / `component_modify` / `component_add` | `path` + `type_name` (singular) | `instance_id` (host GameObject), `component_types` array (first element), `component_instance_id` |
| `component_destroy` | host `instance_id` + `component_types` (array) | `path` + `component_types` (note: takes the **host GameObject** id, not `component_instance_id`) |
| `gameobject_*` | `path` + `parent_path` / `name` | `instance_id` |
| `assets_delete` | `paths` (array) | — |

`instance_id` is the canonical live-instance selector and is preferred. When a
field value is itself an object reference (in `object_modify` / `component_modify`
`fields[].value`), accept `{"path": "..."}`, `{"asset_path": "..."}`,
`{"instance_id": N}`, or null. A scene-hierarchy `path` is resolved through the
hierarchy walker (and `GetComponent(fieldType)` for component-typed fields); a
value that cannot be resolved surfaces a per-field error and is **not** silently
written as null.

Negative instance IDs (e.g. `-4617212`) returned by creator tools
(`gameobject_create`, `ui_canvas_add`) are valid live-instance selectors — pass
them back verbatim to `gameobject_modify` / `component_modify` / `invoke_method`.
If a tool ever reports `GameObject not found (instance_id=0…)`, the id changed
across a domain reload; re-acquire it via `gameobject_find` (by name) and retry.

#### `invoke_method` positional arguments

`invoke_method` converts `args[]` JSON values to the method's parameter types.
Beyond scalar types, it resolves:

- **`UnityEngine.Object` / `GameObject` / `Component` parameters** — pass a bare
  integer instance id, or a handle/selector object: `{"instance_id": N}`,
  `{"objectId": N}`, `{"path": "Root/Child"}`, or `{"name": "Child"}`.
- **`Scene` parameters** (e.g. `SceneManager.SetActiveScene(Scene)`,
  `MoveGameObjectToScene(GameObject, Scene)`) — pass `{"path": "Assets/Scenes/X.unity"}`,
  `{"scene_path": "..."}`, or `{"scene_name": "X"}`. The scene must be loaded
  first (`scene_open` Additive); an unloaded scene surfaces a clear
  `invocation_error`.
- **`Vector2` / `Vector3` / `Vector4` / `Color` / `Quaternion`** — pass a string
  array form (`"[1,2,3]"`) or object form (`"{x:1,y:2,z:3}"`).

Static methods resolve without `is_static: true` — the reflection lookup uses
both `Static` and `Instance` flags, so a `public static string Run()` on a
`static class` is found by `{type_name, method_name}` alone. `is_static` only
selects whether to instantiate/resolve a target (it no longer gates which
methods reflection can see). The `method_not_found` error lists the visible
candidates (prefixed `static ` for static members).

### `unity_senses_run_tests`

Results are retrieved by the server polling a results file under
`~/.unity-open-mcp/`, **not** via `unity_senses_pull_events` (which streams only
console/editor-state). `timeout_ms` is a client-side polling budget, not a
bridge parameter. When the filter matches zero tests, `TestRunnerApi.Execute`
runs nothing and the result carries `status: "completed"` with
`summary.total: 0` and a `note` — this looks like "no results" but is a no-match
filter, not a failure (`test_class` maps to NUnit `groupNames`, a partial/group
match). To run a single test method deterministically, invoke the test class or
method directly via `unity_open_mcp_invoke_method` — NUnit assertion failures
come back verbatim in the error.

`run_id` names the run this call **starts**; it is not a poll handle. There is no
polling step for the caller to perform — the call starts the run *and* waits out
`timeout_ms` for the results file, then returns the terminal result. Re-calling
with the id from a previous response used to start a *second* run under that id,
superseding the first and rewriting its pending marker with the second call's
(empty) filters — which read as "the declared params were dropped". The bridge now
refuses a `run_id` whose run is still in flight and says what the id is for.

`run_tests` also cannot be a nested `unity_open_mcp_batch_execute` step; the batch
route has no results poller and refuses it up-front with
`batch_step_requires_server_poll`.

A run that is aborted before its results land — superseded by a newer run,
aborted when play mode is entered mid-EditMode-run, cancelled by a domain
reload, or owned by an Editor process that is gone — writes a terminal
`test-results-<runId>.json` with `status: "aborted"` and a `reason`
(`superseded_by_run` | `playmode_entered` | `editor_process_gone`), so a polling
agent always sees a final state instead of timing out in silence. Each pending
marker records the Editor process that owns it, and the bridge finalizes markers
from a dead process on load — a killed/relaunched Editor no longer leaves one
sitting for the full one-hour TTL.
Unknown parameter keys are rejected with `validation_error` (the bridge
validates body keys against the declared parameter names, mirroring the MCP
schema's `additionalProperties: false`). The four **transport envelope keys**
— `gate`, `timeout_ms`, `ignore_scene_dirty`, `confirm_bypass` — are exempt:
the dispatcher consumes them off the raw body for every tool (gate policy,
queue timeout, scene-dirty guard, deny bypass), so they are valid on any tool
regardless of its parameter list. Without the exemption the server's
schema-default injection (`gate: "enforce"`, `timeout_ms: 30000`) made
registry-dispatched tools with no matching C# parameter (e.g.
`editor_status`) uncallable with default args.

### `unity_senses_pull_events`

Incremental console logs and editor-state transitions from a single
server-side SSE subscription. The first call opens the stream; later calls
return only new events (pass a stable `subscriber` id for a per-caller
cursor). The reader survives Unity restarts and domain reloads: every
reconnect re-resolves the bridge port and bearer token from the instance
lock under `~/.unity-open-mcp/instances/` (the bridge rotates its token on
each reload), reconnecting with exponential backoff — 2s doubling to a 30s
cap, reset after a successful connect. While the bridge is unreachable the
tool surfaces `bridge_unavailable` (with the last reconnect failure in
`lastError`); once the bridge returns, the next reconnect resumes the stream
without any agent action.

### `unity_senses_visual_compare`

Visual regression compare — capture a named reference snapshot, then diff a
later capture against it. A `compare` response carries `pixelDiffPercent`,
`mismatchedPixels`, `perceptualDistance` (8×8 average-hash Hamming distance —
tolerates minor anti-aliasing / compression drift), and `match`. The `match`
flag is true when `pixelDiffPercent <= sensitivity * 100` (default `sensitivity:
0.01` = 1% pixel diff); raise it for noisier scenes (dynamic backgrounds,
particle systems), set `0` for an exact match. On a non-zero diff the tool
returns an **inline diff image** (mismatched pixels highlighted red over the
current frame) as an MCP image content block — same `inlineImage` unwrap
mechanism `capture_inline` uses. Set `include_diff_image: false` to receive
metrics only.

References persist as flat named files under
`~/.unity-open-mcp/screenshots/references/` (`<name>.png` + `<name>.meta.json`);
`name` rejects path separators and traversal. The compare resamples the current
capture to the reference dimensions when they differ, so the diff is
resolution-stable across captures at different sizes. Per-channel drift up to an
epsilon (8) counts as equal, so sub-threshold AA noise does not register as a
regression. (Named "reference snapshot" / "visual compare" rather than
"baseline" — that name is taken by the verify-issue baseline tools
`baseline_create` / `regression_check`, which compare compiler-error /
project-health snapshots, not images.)

### `unity_open_mcp_manage_tools`

Activation is **additive** (activating one group never drops another). After
`activate` / `activate_for` the server emits `notifications/tools/list_changed`,
but newly-activated tools only appear in your tool list if your client honors
that notification and re-issues `tools/list`. If the tools do not show up,
manually re-request the tool list, or route the tool by id through
`batch_execute` (which works regardless of `listChanged` support).

### `unity_open_mcp_read_compile_errors`

The server first makes a bounded read-only `GET /compile-state` probe. A completed
CompilationPipeline generation whose source content still matches is authoritative:
`status` is `no_errors_found`, `compile_failed`, `currently_compiling`, or
`assembly_stale`. The response records `generation`, `sourceMatches`,
`beforeAssemblyMtimeMs`, and `afterAssemblyMtimeMs` (Unix milliseconds). Touching
unchanged content does not invalidate a confirmed compile. Editing source does.
Old log errors and issues remain separately in `historicalLogErrors` and
`historicalLogIssues`; they do not override a confirmed current compile.

If the bridge cannot provide a confirmed generation, the tool falls back to
log evidence with the following freshness and authorship caveats. It never spawns Unity.

Resolves the authoritative `Editor.log` per platform. Unity 6000.5+ writes a
project-relative log (`<project>/Logs/Editor.log`); pre-6000.5 writes the global
per-user log. When **both** exist, the resolver picks the **freshest** (newest
mtime): a project opened in 6000.5+ at some point leaves a stale
`<project>/Logs/Editor.log` on disk, and re-opening it under a pre-6000.5 Unity
makes the live editor write the global log while the project log lingers.
Existence-only resolution read the stale project log; the freshness comparison
prefers whichever file was written most recently (reported as
`logSource: "global_log_fresher"` when the global log wins).

When a live editor holds the project (known from the instance lock) and the
resolved log looks frozen (implausibly small — the signature of a batch spawn
that rotated `Editor.log` to `Editor-prev.log` while the live editor keeps
writing to the rotated file), it falls back to `Editor-prev.log` and reports
`logSource: "prev_log_live_editor"` in the response. When an assembly is stuck
in a failed-compile state, `AssetDatabase.Refresh` no-ops and the response
carries `staleLogSuspected` — force a recompile
(`unity_open_mcp_recompile_scripts`; `compile_check` with the Editor closed)
before trusting the errors. `compile_check` itself
short-circuits with `editor_instance_locked` **before** spawning when a live
editor holds the project, avoiding the spawn that would rotate the log in the
first place.

The log-mtime comparison behind `staleLogSuspected` cannot catch every case:
unrelated asset imports keep appending to `Editor.log`, so the **file** can be
fresh while the error **block** inside it is old, and Unity writes no timestamp
on or around a compile block to anchor against. The built-assembly set is the
anchor that does work — a cited source newer than the newest
`Library/ScriptAssemblies/*.dll` proves no compile has *completed* since that
file was edited, so the block necessarily comes from an earlier compile. The
response reports that as `errorsMayPredateEdits: true` +
`errorsMayPredateEditsFiles[]` + `errorsMayPredateEditsHint`, and the `headline`
carries the caveat inline. Distinct from `staleAssembly` (which scans all of
`Assets/`): this names the files the reported errors actually cite — the code an
agent is about to re-read.

When the log is stale (a cited source is newer than the log) **or** authored by
a different Unity (a version mismatch), the top-level `status` is downgraded to
`"stale_log"` and `unhealthy` is suppressed — the `errors[]` are still attached
as evidence, but the headline makes clear they **may not apply** to the running
editor and must be confirmed by a genuine recompile before acting on them. The
raw verdict (what the log literally says) is preserved in `logVerdict`. An agent
that branches on `unhealthy` will not treat phantom errors as a hard failure.

The response also carries `staleAssembly: true` when at least one
`Assets/**/*.cs` source is newer than the newest `Library/ScriptAssemblies/*.dll`
— the running assembly predates the latest source (Unity's incremental compiler
no-op'd a recompile), so a `no_errors_found` signal **cannot** be trusted until
the assembly is rebuilt. When that flag is set **with errors**, the `headline`
itself carries the may-predate caveat (not only the separate
`staleAssemblyHint` field) — errors parsed from a compile that predates the
newest on-disk source must not be acted on as fact. Call
`unity_open_mcp_recompile_scripts`, then re-read compile errors. That tool lives
in the non-default `typed-editor` group, so
every hint and description that names it also carries the concrete
`manage_tools` activation call: the strings come from one helper
(`tool-hint.ts`) that resolves a tool's group from the registry and appends the
activation call automatically, and a unit test asserts that no emitted hint or
tool description names a non-default tool without it. The helper also refuses
to prescribe an activation call for a name that is not a registered tool (the
registered name list is pushed to it from `tools/index.ts`, and tests pin
group-table ↔ registry parity plus every call site), so a hint can no longer
send an agent to activate a group for a tool that does not exist.

When `logSource` is a `prev_log_*` value (the resolver fell back to the
ROTATED `Editor-prev.log`) and the read finds no errors, `status` is
**`indeterminate`**, not `no_errors_found` — a clean read from the rotated log
is not a verified clean bill of health while a live Editor may be writing a
different log. The headline says exactly that and points at the live bridge
(`editor_status` / a live probe) for confirmation; the raw verdict is preserved
in `logVerdict`.

When the log header was parseable (a short `-batchmode` run usually fits in the
tail; a long live-editor session usually does not), the response also carries
log-authorship fields that identify **which Unity wrote the log**:
`logUnityVersion` (from the `Unity Editor version:` header line), `logBatchMode`
(from `Batch mode:`), `logDate`, and `liveUnityVersion` (the live bridge's
version, from the instance lock). On a version mismatch the response adds
`logAuthorshipMismatch: true` + `logAuthorshipHint` warning that the errors
**cannot all occur** in the live editor — a batch run of a newer Unity leaves
errors (e.g. an API deprecation that is only an error on that version) that the
live editor never produced. Do **not** "fix" those files or refuse to continue on
a `compile_failed` signal from a log whose version differs from the live editor;
the inverse of the stale-log trap is just as dangerous.

### `unity_open_mcp_recompile_scripts`

A deterministic "force a real recompile and wait for it" primitive. Calls
`CompilationPipeline.RequestScriptCompilation()` and blocks until the
compile settles via its `restart_then_settle` lifecycle. Use this when
`assets_refresh` / `execute_csharp` fail to recompile after a C# edit — Unity's
incremental compiler frequently no-ops on unchanged-import views, leaving the
running assembly stale. The response reports `dllMtimeBefore` / `dllMtimeAfter`
(newest `Library/ScriptAssemblies/*.dll` mtime, UTC ticks) and a `recompiled`
boolean so a no-op is detectable; `agentNextSteps` branches on the outcome
(`read_compile_errors` when rebuilt, poll `isCompiling` when a compile was
already in flight, fall back to `assets_refresh` / `reimport_package` when Unity
judged sources unchanged). The recompile is project-wide; `paths_hint` exists
only to give the gate a scoped hint (the edited scripts).

### `unity_open_mcp_upgrade`

Plans or applies the same single-project update shown in the bridge window's
**Status → Updates** section. It lives in the `typed-editor` group and defaults
to `dry_run: true`. `target_version` accepts a plain `X.Y.Z`; when omitted, the
explicit tool call queries the latest npm release. Category flags independently
select UPM, project configs, home configs, and agent-facing prose.

The preview reports each file, pins found, intended action, and skip reason.
Home-scoped entries that belong to another project, have no ownership marker,
or share a file with another project are skipped. Apply writes one-time `.bak`
files for configs/prose, then schedules one atomic verify + bridge UPM request
and returns before a possible domain reload. Embedded and `file:` bridge installs
never have their packages replaced; their selected config/prose updates remain
available. Restart MCP clients after apply so they reload their configuration.

### `unity_open_mcp_execute_csharp`

The snippet is compiled into its **own** assembly (`UnityOpenMcpSnippet`), so it
sees only the `public` surface of the project's assemblies. An `internal` member
— the natural visibility for a testable seam — reports as `CS0117` ("does not
contain a definition for"), `CS0122`, or `CS1061`, which reads like a typo. The
`compilation_error` message appends a note naming the assembly boundary and the
reflection recipe (`typeof(T).GetMethod(name, BindingFlags.NonPublic | …)`)
whenever one of those diagnostics is present. For a seam you will call more than
once, prefer making it public or exercising it from a test assembly with
`InternalsVisibleTo` via `unity_senses_run_tests` — reflection loses
compile-time checking.

When the assembly is stale (a source is newer than the newest built DLL), the
response carries a top-level `staleAssembly: true` + `warning` alongside the
`_staleDomain` evidence — the snippet ran against the **pre-edit** code. When
that coincides with the `editor_fd_exhaustion` signature in the freshest
`Editor.log`, the call **fails** with `editor_build_wedged` instead: the build
driver is dead, edits are on disk but were never compiled, and no recompile
clears it. Discard anything concluded from results returned since.

Snippets and compiles are cached per domain: an identical snippet re-run reuses
its loaded assembly (no recompile, no fresh `Assembly.Load`), and the Roslyn
metadata references are built once per domain through streams that hold no file
descriptors open. As a tripwire, once the Editor process passes 80% of Mono's
~1024 fd ceiling the response's `agentNextSteps` carries an fd-pressure
advisory (elevated at ≥80%, CRITICAL at ≥90%) recommending a domain reload —
which releases leaked descriptors — and a `resource_pressure` check. The
advisory honours `resourcePressure.fdCeiling` from
`.unity-open-mcp/settings.json`, the same override `resource_pressure` reads,
so a project on a non-Mono ceiling does not get two contradicting verdicts.

### `unity_open_mcp_execute_menu`

Runs on Unity's main thread and blocks until the menu returns. It accepts
`timeout_ms` with the same semantics and host-safe cap as `execute_csharp`
(~55s — above that the MCP host aborts the `tools/call` before the bridge can
return a structured envelope). Raise it for a genuinely slow menu:
`Assets/Refresh` after an `AssetPostprocessor.GetVersion()` bump reimports the
whole content tree.

A `timeout` response is a wait that elapsed, **not** a failure — the menu is
usually still running and completes. Confirm with `editor_status` or an asset
probe; a blind retry of an authoring menu can double-write assets. The timeout
envelope's `agentNextSteps` says exactly this.

`paths_hint` is required for a mutating menu path **even with `gate: "off"`** —
it is the declared mutation scope recorded in the audit trail, not only the
gate's validation scope, and there is no whole-project fallback (read-only menu
paths waive it). The refusal happens before dispatch, so the envelope reports
`gate.skippedReason: "request_rejected"` — the gate never ran.

Menus that open a **modal dialog** (material/scene converters, importer
prompts) hold the main thread for as long as the dialog is open, wedging this
and every subsequent call. No tool can dismiss a modal; `bridge_status` reports
the state as `wedged` / `main_thread_wedged` so it is not mistaken for a compile
failure. Prefer the non-interactive API.

### Creator tools and scene targeting

`gameobject_create`, `ui_canvas_add`, and `prefab_instantiate` accept an optional
`scene_path` / `scene_name` to control which **loaded** scene receives the new
root GameObject / Canvas / instance. Without it, the new object lands in the
active scene — invisible and mutable, so with multiple scenes loaded the result
was inconsistent. Pass the target scene asset path (e.g.
`Assets/Scenes/Bootstrap.unity`) or name; the scene must already be loaded
(`scene_open` Additive first). `ui_canvas_add`'s EventSystem ensure is scoped to
that scene, so a stray EventSystem is neither created in nor stolen from a
different loaded scene.

### `unity_open_mcp_editor_status`

In addition to play/compile/pause state and the active scene path, reports
`dirtySceneCount` + `dirtyScenes: [{name, path}]` — every loaded scene with
unsaved in-memory changes. This is the memory-vs-disk signal: after a structural
op (`gameobject_set_parent`) that marks a scene dirty without writing it, check
`dirtySceneCount > 0` to know a `scene_save` is pending before reasoning against
on-disk YAML. `reserialize` reports `touchedOpenScenes` and marks opened scenes
dirty when it rewrites their on-disk YAML, for the same reason.

### `unity_open_mcp_dependencies`

Returns forward and reverse asset edges, broken forward GUIDs, and dependency
cycles. Use `include_impact=true` for the transitive reverse closure. The impact
closure is offline-routed; other forms prefer live Unity and fall back where
supported.

### Scene tools

`scene_set_active`, `scene_unload`, `scene_save`, and `scene_get_data` resolve
opened scenes by asset `path` first and display `name` second. Prefer paths.
For `scene_save`, a path that does not identify an open scene is a save-as
destination.

### `unity_open_mcp_component_get`

Reads serialized fields/properties of one Component. The optional `fields`
array filters the response to entries whose leaf path/name matches (exact,
case-insensitive on the last path segment), bypassing `page_size`/`cursor` and
the `max_fields` cap — ask for one field on a 126-field component without
paging. Containers and arrays that the SerializedProperty leaf reader does not
expand render as `{"note":"container_or_array","hint":"use 'property_path' to
drill in, or 'object_get_data' to expand"}`; drill in via `property_path`, or
use `object_get_data` to expand the same array reflectively.

### `gameobject_modify`

In addition to legacy flat fields, the tool accepts:

- `gameObjectDiffs` for the target root;
- `pathPatchesPerGameObject` for descendants;
- `jsonPatchesPerGameObject` for component merge patches.

Application order is component JSON patches, descendant path patches, then
root diffs.

### Unity Hub control

The `unity_open_mcp_hub_*` family is local-routed and does not need a running
Editor. Install calls open Unity Hub through its deep link and return after the
request is accepted, not after download completion. Poll `hub_list_editors` to
confirm completion. System-level mutations are gate-free because they do not
modify project assets.

## Source references

- `mcp-server/src/tools/index.ts`
- `mcp-server/src/tool-router.ts`
- `mcp-server/src/batch-spawn.ts`
- `mcp-server/src/compressible-router.ts`
- `mcp-server/src/capabilities/build-capabilities.ts`
- `mcp-server/src/capabilities/tool-groups.ts`
- `mcp-server/src/tool-session-state.ts`
- `mcp-server/src/cli/`

## Locator and argument contract

Prefer `asset_path` for the target asset, `game_object_path` for a hierarchy
selector, `component_type` for a component type, and `property_path` for a
serialized-property patch. Role-specific locators such as `parent_path`,
`scene_path`, and asset-read subtree filters retain their distinct meanings.
Get the exact tool schema before calling: no single locator is valid for every
tool. For example, `component_modify` accepts:

```json
{
  "game_object_path": "Main Camera",
  "component_type": "UnityEngine.Transform",
  "fields": [{ "property_path": "m_LocalPosition", "value": [0, 1, -10] }],
  "paths_hint": ["Assets/Scenes/Main.unity"]
}
```

Unambiguous historical aliases remain declared with `deprecated: true` and
`x-alias-for`; MCP responses emit deprecation notes (direct HTTP exposes
`X-Unity-Open-MCP-Deprecations`). Canonical keys map to existing handler keys
through schema-owned `x-wire-key` metadata. Mixing aliases for one selector is
rejected, as are multiple host selectors or a component ID alongside a type
selector that would otherwise be ignored. Unknown keys, invalid types, enums and bounds fail before dispatch
with `invalid_arguments`; a missing required argument may be reported by the
MCP entrypoint as `missing_required_argument`. Batch preflight validates every
nested command before step zero. Arbitrary patch values remain opaque.

`scan_paths` enumerates valid rule IDs in `categories`, `include_rules`, and
`exclude_rules`. The offline-only `offline_integrity` reporting category is
not a selectable VerifyRunner rule and is excluded from those enums. C# snippets inject `using Object = UnityEngine.Object;` unless
an explicit caller alias already exists. They still run in a separate assembly;
internal-member access requires reflection or a suitable test assembly.

## Project-owned commands

See [Project command catalog](project-commands.md) for the always-visible
`unity_open_mcp_project_commands` list/describe surface and C# authoring contract.
