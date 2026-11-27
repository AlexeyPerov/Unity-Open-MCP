# Bridge HTTP API

Unity bridge endpoints are served by `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`.

Default bind is loopback (`127.0.0.1`).

## Endpoints

| Endpoint | Method | Purpose |
|---|---|---|
| `/ping` | `GET` | Bridge and editor health snapshot. |
| `/instance` | `GET` | Runtime instance metadata snapshot. |
| `/events` | `GET` | SSE event stream (console/editor-state events). |
| `/events/poll` | `GET` | Pull-style event drain endpoint. |
| `/compile-state` | `GET` | Read-only main-thread CompilationPipeline generation, source-content match, current errors, and before/after assembly mtimes. Unconfirmed generations return `indeterminate`. The same snapshot can ride on a tool response instead ([Compile state on tool responses](#compile-state-on-tool-responses)). |
| `/tools` | `GET` | Compiled-state tool inventory + group→tools map (used by capabilities / manage_tools for per-group availability). |
| `/tools/{toolName}` | `POST` | Execute one bridge tool. |
| `/project-command-jobs` | `POST` | Start, inspect, or cancel an asynchronous project command job. |
| `/playmode-operations` | `POST` | Inspect or cancel an operation running on the shared play-mode scheduler (for example a gesture). |
| `/resources` | `GET` | List bridge resources. |
| `/resources/{route}` | `GET` | Read one bridge resource payload. |

Subscriber lifecycle on both event endpoints: a client-supplied `subscriber`
id persists across polls/reconnects and keeps its cursor; an id the bridge
mints (no `subscriber` param) is retired with the request/stream so anonymous
clients don't accumulate subscriber state.

## Port and discovery

- Default port is deterministic per project path.
- Optional override:
  - `UNITY_OPEN_MCP_BRIDGE_PORT`
  - Unity arg `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`
- Running bridge writes an instance lock file under `~/.unity-open-mcp/instances/`.
- MCP server uses that lock for project-to-port resolution.
- The lock doubles as the heartbeat (rewritten every 0.5 s and on every
  transition): `state` (`idle`, `compiling`, `reloading`, `entering_playmode`,
  `playing`, `exiting_playmode`), `isPlaying`, `isCompiling`, `heartbeatAt`, and
  (wire contract 8) a `compile` block — `generation`, `running`, `completed`,
  `failed`, `errorCount`, `pending` (a compile requested and not started yet)
  and `requestedAt`. It stays on disk across a domain reload, so it is what the
  MCP server reads while the listener is down. Since the heartbeat is written
  from the main thread it also stops during every reload; a frozen heartbeat
  alone does not mean the bridge is dead (see
  [lost bridge vs. long reload](routing-lifecycle.md#a-lost-response-or-a-long-reload-is-not-a-dead-bridge)).
  `state` reads `reloading` from the moment a call requests a domain reload
  until it starts (wire contract 9). The first write of a reloaded domain is
  read from the Editor, not from defaults (wire contract 15): after a reload in
  play mode it reads `playing` (or `entering_playmode` while play mode is being
  entered), never `idle` / `isPlaying: false`, and it carries the last `compile`
  block, which survives the reload.

## Authentication and bind mode

- `authMode: "none"` (default): requests accepted without bearer token.
- `authMode: "required"`: requests must send `Authorization: Bearer <token>`.
- Remote bind (`bindAddress: "0.0.0.0"`) requires `authMode: "required"`; the bridge refuses to start on a non-loopback interface without token authentication.
- If remote access is needed, terminate TLS in front of the bridge (reverse proxy or tunnel).

## Tool execution envelopes

`POST /tools/{toolName}` responses typically include:

- `ok`: success marker
- `result`: tool payload (on success)
- `error`: structured error object (on failure)
- optional lifecycle/settle metadata for mutating tools

## Request limits

`POST /tools/{toolName}` request bodies are capped at 64 MB. A body that
exceeds the cap — whether declared via `Content-Length` or streamed (chunked) —
is rejected with HTTP `413` and the standard error envelope:

```json
{ "error": { "code": "request_too_large", "message": "Request body of N bytes exceeds the 67108864-byte limit." } }
```

Legitimate JSON tool arguments are kilobytes; the cap exists so a caller cannot
OOM the Editor with an unbounded body. In `authMode: "none"` (local dev) any
caller on the bind address can reach this path.

## Gate policy

For a mutating tool, the bridge selects the effective gate mode in this order:

1. A valid request-level `gate` value (`"enforce"`, `"warn"`, or `"off"`).
2. The project default in `.unity-open-mcp/settings.json`.

An omitted, malformed, or unknown request value falls back to the project
default. The gate value declared by a registry tool is catalog/recommendation
metadata; it does not override that project default during dispatch.

`GatePolicy.Execute` then applies the selected mode to the
checkpoint → mutate → validate → delta flow. Changing this precedence or its
fallback behavior is a bridge API contract change.

A gated call that makes an asset from another (copy, move, scene save-as,
prefab save-as from the open stage) records the derivation while it runs: the
source is scanned with the gate's rules just before the destination is made,
and the delta moves the destination's matching issues out of the new counts
into `inheritedErrors` / `inheritedWarnings` / `inheritedIssues` /
`inheritedFrom` (wire contract 10). See the
[mutation and gate contract](mcp-tools.md#mutation-and-gate-contract).

Each gated mutating call runs in an undo group of its own, named after the tool
(`MCP <tool>`), and a mutating `batch_execute` in one group for all its steps
(wire contract 14), so one `editor_undo` reverts one call. A call that records
no undo leaves no group behind, and one made while there is something to redo
keeps the redo entries unless it records something (wire contract 16). See the
[mutation and gate contract](mcp-tools.md#mutation-and-gate-contract).

A call that names `in_prefab` (wire contract 12) writes that prefab asset during
the call, so its gate scope is `paths_hint` plus the asset when `paths_hint` does
not cover it, and the delta validates the saved file. See
[Editing a prefab asset in place](mcp-tools.md#editing-a-prefab-asset-in-place-in_prefab).

Two honesty rules govern the gate's response:

- `gate.delta` is `null` (never a zeroed object) whenever no delta was
  computed — the checkpoint failed, the mutation failed before validation, or
  the validate scan could not complete. A zeroed-looking delta always means
  "computed and clean".
- When a verify rule **throws** during the checkpoint or validate scan, the
  gate reports `gate.outcome: "validate_scan_failed"` with
  `gate.rulesFailed: [<ruleId>, …]` and withholds the delta: a failed rule
  contributes no issues, so its pre-existing problems would read as phantom
  "new"/"resolved" counts. The mutation itself still committed — verify health
  manually with `validate_edit` / `scan_paths`.

`paths_hint` is mandatory for every mutating call and is **not** waived by
`gate: "off"`. It is the declared mutation scope recorded in the audit trail,
not only the gate's validation scope, and there is no whole-project fallback.
The check runs before dispatch, so a missing `paths_hint` returns
`error.code: "paths_hint_required"` with `gate.skippedReason:
"request_rejected"` — nothing ran and no gate was evaluated. Tools that expose
`read_only` (today: `execute_csharp`) waive the requirement for inspection-only
requests; known disruptive calls keep it. A read-only `execute_menu` path and
an all-read batch waive it too. The project-wide operations have a declared
default scope instead: `assets_refresh` with `whole_project: true`, its menu
form `execute_menu "Assets/Refresh"`, and `recompile_scripts` scope the gate to
`["Assets"]` when `paths_hint` is omitted; `reimport_package` scopes it to
`Packages/<package_id>`.

Every project refresh a tool starts checks the open scenes first (wire contract
15): an open scene whose file changed on disk since its last import is reloaded
in place around the refresh, or the refresh is refused with
`open_scene_changed_on_disk` when that would lose edits — so Unity's native
"changed on disk" dialog never blocks the main thread. See
[Refreshes and open scenes changed on disk](mcp-tools.md#refreshes-and-open-scenes-changed-on-disk).

Rule auto-selection maps the `paths_hint` extensions to rule families. Only
**registered** rule families are ever auto-selected: image (`.png`, `.jpg`,
`.jpeg`, `.tga`) and audio (`.wav`, `.mp3`, `.ogg`) paths fall back to
`missing_references` + `dependencies`, because the `textures`,
`sprite_2d_analysis`, and `audio_analysis` families are planned-but-not-shipped
and selecting them would run zero rules (a vacuous gate pass).

## Health check example

```bash
curl -s "http://127.0.0.1:<port>/ping"
```

Use `/ping` to confirm:

- bridge reachability
- compile/play state
- readiness before running mutating tools
- the installed bridge's wire-contract revision

`/ping` also reports `transition` (wire contract 8): `compiling`,
`reloading` (wire contract 9: a call requested a domain reload that has not
started yet), `entering_playmode`, `exiting_playmode`, or `null` when settled. A reachable,
non-compiling bridge is not settled while play mode is changing. While the
session initializes after a reload `/ping` answers HTTP 503.

`/ping` adds `playRequest` (wire contract 17) while the last `editor_set_state` play request is
pending (`status: "requested"`) or was cancelled (`status: "cancelled"`,
`reason`, `requestedAt`, `cancelledAt`, `pendingDelayCalls`,
`delayCallTargets`); it is absent once play mode was entered.

`/compile-state` adds `indeterminateReason` to an `indeterminate` status
(`no_compile_this_session` / `fingerprint_pending`) and, after a
`recompile_scripts` call, `lastScriptImport: {generationAtStart, scriptImport}` —
what that call imported before requesting its compile, kept across the domain
reload that usually closes the call's response.

The response body carries `bridgeVersion` (the package semver) **and**
`wireContract` (an integer bumped for every observable change to the
request/response contract — accepted parameter keys, error codes, envelope
fields, declared schema ceilings). The semver does not move for a wire-contract
fix, so `wireContract` is what distinguishes a stale install from a regression:
`unity_open_mcp_bridge_status` compares it against the revision its MCP server
was built against and reports `wireContract.stale`. A bridge old enough to omit
the field is, by definition, older than revision 1.

## Request-scoped JSON responses

Wire contract revision 2 adds `GET /compile-state` (and its
[tool-response header](#compile-state-on-tool-responses)) and `X-Request-Id`
echoing.
JSON responses are fully validated before headers are committed. Each request
can send at most one envelope, with explicit UTF-8 content length and connection
closure; a failed write aborts the response rather than appending another error.
A late timed-out dispatch cannot send into the next request. Invalid JSON is
replaced by HTTP 500 `invalid_response_json`. Validation does not limit nesting
depth, so a deep but valid body (for example `scene_get_data` or `read_asset`
over a deep hierarchy) is sent unchanged.

Per-call console logs use Unity Console mode flags: scripting/import/compiler
warnings retain `warning` severity, compiler errors and exceptions retain `error`,
and ordinary managed logs remain `log`.

## Compile state on tool responses

A `/tools/{toolName}` request that sends `X-Unity-Open-MCP-Compile-State: 1` and
is answered with the gate envelope (`execute_csharp` and `invoke_method`
included) gets the `/compile-state` snapshot back in the response header of the
same name: base64-encoded UTF-8 JSON with the same fields, except that the
`errors` list is replaced by its `errorCount`. The bridge takes it on the main
thread the dispatch already holds, right after the tool ran, so qualifying a
result costs no extra request or main-thread hop. The header is left off when
the settle wait saw a compile after the tool ran (the snapshot would predate
it), on refusal, timeout and fault envelopes, and by bridges that predate it; a
client then reads `GET /compile-state`. The MCP server asks for it on
`execute_csharp` and `invoke_method`, the results it qualifies with
`_compileState`.

## Compile handshake and reload answers

Wire contract 8. A compile a call requests (a refresh that imported scripts,
`recompile_scripts`, `script_write`) starts on a later Editor update, and
`isCompiling` can read false in the gap. The dispatch that left such a compile
due records it — `pending: true` in the lock's `compile` block and in
`/compile-state`, cleared when the next compile starts — and the gate envelope
of calls that can leave a compile due (`assets_refresh`, `script_write` /
`script_delete`, `execute_menu "Assets/Refresh"`, `restart_then_settle` calls,
or any call that left one due) carries
`"compile": {"generationAtStart": N, "generation": N, "pending": bool, "running": bool}`.
`agentNextSteps` then names
`unity_open_mcp_editor_status {"wait_until":"idle","after_generation":N}`, the
MCP server's wait.

Calls and reloads:

- A mutation that reaches the main thread while a compile is running or due, or
  a play-mode change is requested and not finished, is not run: HTTP 503 with
  `{"error":{"code":"editor_reloading",…},"dispatched":false,"transition":…,"retryAfterMs":1000}`.
  Reads still run.
- When a domain reload starts, every tool call still in flight is answered
  before the listener stops: HTTP 503 `editor_reloading` with
  `"dispatched": false` if its work never reached the main thread, HTTP 200
  `editor_reloading` with `"dispatched": true` and
  `"compile": {"generationAtStart": N}` if it ran (its effect may have
  committed). A response a worker is writing at that moment is left to finish.
  Before contract 8 such a call saw HTTP 200 with an empty body.
- `execute_menu "Assets/Refresh"` is a refresh mutation, not a read-only menu:
  `assets_refresh`'s lifecycle and default scope (`Assets`).
- A call whose dispatch leaves a domain reload requested (wire contract 9:
  `EditorUtility.RequestScriptReload` in an `execute_csharp` snippet or an
  invoked method; Unity reports it as requested and not started) is answered
  before that reload: the bridge holds domain reloads
  (`EditorApplication.LockReloadAssemblies`) from the end of the dispatch until
  the response is written, then lets it run. The envelope carries
  `"reloadRequested": true, "reloadTiming": "after_response" | "after_play_mode"`
  and a step naming `unity_open_mcp_editor_status {"wait_until":"idle"}`. Until
  the reload starts the lock `state` and `/ping` `transition` read `reloading`
  and mutations answer 503 `editor_reloading` (`dispatched: false`).
  `after_play_mode`: in play mode under *Script Changes While Playing: Recompile
  After Finished Playing* Unity defers the reload to the end of play mode, and
  nothing is published meanwhile. An `execute_csharp` snippet that leaves a
  compile due skips the settle wait the same way: it answers with its result
  and the `compile` block instead of being cut off by the reload.

## Dispatch timeouts

`main_thread_blocked` means the main-thread dispatcher cancelled the callback
before it started. Unblocking the Editor will not run that expired callback.
If dispatch already started, `timeout` cannot interrupt Unity work; inspect the
operation's post-state before retrying. Timer cleanup still runs on completion,
domain reload, and Editor shutdown.

## Effective read-only requests and gate outcomes

The bridge derives request mutability before scope enforcement, queueing, and
checkpoint/undo setup. Inspection-only `execute_csharp(read_only:true)`, explicitly
marked `[BridgeReadOnlyMenu]` verifier menu handlers, and all-read batches skip
the gate even when the caller supplies `paths_hint`. Read-only snippets and
non-disruptive menus also skip dirty-scene checks and settle waits. The annotation
is a caller/author assertion, not a sandbox: known disruptive snippet calls keep
the conservative lifecycle; indirect calls cannot be proven safe. Because the
assertion is unverifiable, a running project job refuses every `execute_csharp`
call with `job_busy`, `read_only` or not.

`batch_execute` preflights every nested command using generated MCP schema
constraints and live tool/lifecycle availability before any dispatch. All-read
batches require no scope; mixed/mutating batches require the explicit union
`paths_hint`, even with `gate: "off"`. Invalid steps are reported together.

`unity_open_mcp_player_state` is mutating only for `restore` with `confirm: true`;
its snapshot, diff, list, delete and restore dry run are read-only for the project
(no gate, no scope). The applied restore writes no project asset and declares
`ProjectSettings/ProjectSettings.asset` (the project identity it is bound to) as its
scope; see [Restore](playtest.md#restore).

A failed mutation before validation has `gate.outcome: "skipped"`,
`skippedReason: "mutation_failed"`, `skipped: true`, and null `validation` and
`delta`. A completed checkpoint ID remains available. A refusal that names what
to do instead carries it in `agentNextSteps` (read-only, gate-off and
no-scope calls included). A registry tool reports a refusal either by returning
a failed dispatch result or by returning the JSON body
`{"error":{"code":"…","message":"…",…}}`; the dispatcher reads that body as the
same failed mutation — `mutation.success: false`, `mutation.output: null`, the
`error` object's members in `mutation.error` (any other top-level member of the
body is added to it; an `error` member of the same name wins), and a top-level
`agentNextSteps` string array as the envelope's `agentNextSteps`. Only a
top-level `error` object with a string `code` counts; a nested one, or an
`error` string or number, is ordinary output. Direct-response tools answer with
the body as the tool wrote it. A refused `batch_execute` step is reported the
same way (`status: "failed"`, `error`, `agentNextSteps`). Gate failure counters do
not count such failures; activity still records the failed operation. A partial
batch validates committed mutations and reports the actual validation outcome
independently of `mutation.success`. Existing delta and next-step fields remain.
`effectiveReadOnly` is additive response metadata; audit records also include it
and `skippedReason`. `request_rejected`, `gate_off`, `no_scope`, `read_only`, and
`play_mode` identify other skip causes. `prefab_stage_edit` means every target of
the call was in the open prefab stage: the edit is in memory (or written by
Prefab Mode auto-save after the call), while the checkpoint and the validate scan
read saved files, so no delta is claimed; `agentNextSteps` says how the edit gets
saved. A call that also touched a scene validates as usual and carries the same
advice.

`dirtyScenes` lists scene paths with unsaved changes. On a `scene_dirty` refusal
they are the scenes that blocked the call. On a call that ran they are the
scenes it left dirty — dirty after the call and either clean before it or
holding a GameObject the call resolved or created — with the open prefab stage
listed by its prefab asset path and an untitled scene as `(unsaved scene)`;
`null` when there are none (and for read-only calls). A prefab asset edited in
place (`in_prefab`) is saved and unloaded before the call returns, so it is never
listed.

`playModeOnly: true` (wire contract 16) marks a call that ran in play mode and
changed a scene in the Hierarchy — a GameObject it resolved or created there, or
a scene it marked edited (lighting settings): that change lasts until play mode
exits, and an `agentNextSteps` line names the scenes. Unity keeps no dirty state
for those scenes in play mode, so `dirtyScenes` stays `null`. The field is
omitted otherwise; an edit in the open prefab stage or an `in_prefab` asset is
not play-mode-only.

## Published argument schemas

Requests for shipped tools are validated against the generated MCP schemas
before dispatch. Unknown keys, selector conflicts, invalid types, bounds and
enums return HTTP 400 with `invalid_arguments`. An explicit `null` for an
optional argument whose schema declares a non-null type is read as omitted
(clients often serialize absent optionals that way); a required argument, an
untyped patch value and a type that lists `null` keep the value as sent.
Dispatcher-owned transport
fields remain valid at the outer request boundary; nested batch commands keep
their stricter scope/lifecycle preflight.

Canonical locator keys and their declared deprecated aliases map structurally
to handler arguments, including component patch `property_path`. Arbitrary
patch values are not rewritten. Direct HTTP alias use is reported in the
`X-Unity-Open-MCP-Deprecations` response header. See the
[locator contract](mcp-tools.md#locator-and-argument-contract) for examples.

## Project command metadata

`GET /tools?catalog=project_commands` selects the bounded, versioned
[project command catalog](project-commands.md#bridge-transport). Plain `GET /tools`
keeps the existing compiled inventory response. Duplicate built-in registry ids
are rejected for every candidate and reported in registration diagnostics.


### Project-command jobs

Authenticated `POST /project-command-jobs` is the domain-local execution endpoint
behind the MCP [jobs API](jobs.md). Start carries a UUID `job_id` and the ordinary
project-command `invocation` envelope; status/cancel carry that id. The bridge
checks the same declaration, parameter schema, scope, deny rules, and gate default
as synchronous invocation. It returns immediately, then runs the command on the
Editor context. Checkpoint and terminal validation share the normal gate policy.
Only one project job executes at a time; other bridge mutations, gate-free
Editor-state writes (play mode, undo/redo, selection, prefs, console clear),
`execute_csharp` snippets (including `read_only: true`) and test starts are
refused with `job_busy` while it owns the Editor scope. The refusal's `error`
object (`mutation.error` on the gate path) adds `job` with `jobId`, `commandId`,
`state` and `phase`, plus `conflictingSteps` (`index`, `tool`) for a batch; next
steps arrive in `agentNextSteps` (a top-level sibling of `error` on direct-response
tools). The job id is the MCP job id. Job
records are owned by `X-Agent-Id` and lost on domain reload, which the MCP
adapter reports as an unknown outcome.

### Play-mode operations

Tools that must run across real play-mode frames (`unity_open_mcp_inputsim_gesture`,
`unity_open_mcp_playtest_wait`, `unity_senses_capture_series`, and each
`unity_open_mcp_playtest_probe` probe, whose own actions answer at once) start an operation on the bridge's shared
play-mode scheduler and return at once with `operation` (`operation_id`, `kind`,
`state`, `phase`, `progress`). The scheduler is a player-loop system at the start
of `EarlyUpdate`; it owns per-frame timing (`realtime` or `gameplay`),
cancellation, and cleanup. `POST /playmode-operations` takes
`{"action":"status"|"cancel","operation_id":"…"}` and answers the same snapshot;
terminal snapshots (`succeeded`, `failed`, `cancelled`, `orphaned`) carry the
tool's `result` and a `reason` (`completed`, `failed`, `stalled`, `cancelled`,
`play_mode_exited`, `domain_reload`, `editor_quit`). An operation that ends on its
own terms without success (a wait that timed out, a game exception under
`fail_on`) is `failed` with reason `failed` and a stable `result.error_code`
(`timed_out`, `exception_thrown`, `error_logged`, `condition_error`,
`capture_failed`). `progress` carries `clock`, `elapsedMs` / `expectedMs` (for a
capture series the schedule offset reached, 0 while `start_on` waits),
`realtimeMs`, `stalledMs`, `ticks`, `startFrame`, `lastFrame` and, while a
capture series runs, `series` (`requested`, `captured`, the frames captured so
far). An operation that makes no progress for 15 s of wall-clock time — no
frame runs (a paused Editor; also checked from the Editor update loop), or its
`gameplay` clock stands still at `Time.timeScale` 0 — is `failed` with reason
`stalled` (wire contract 11), `stall: {reason, stalledMs}` (`editor_paused`, `time_scale_zero`,
`no_frames`, `no_progress`) and the tool's `result.error_code`
(`capture_series_stalled`, `wait_stalled`, `gesture_stalled`); slow progress
never ends one, and a main-thread hitch counts at most 1 s. Waiting for
`start_on`, an `at_frames` schedule and encoding always count as progress. A
`playtest_probe` operation (wire contract 13) never stalls: it waits through a
paused Editor or a zero time scale and ends on its own terms; a cancel ends the
probe `stopped` with reason `cancelled`.
Every terminal path runs the operation's cleanup exactly once,
which releases held input, restores a wait's `Time.timeScale` and encodes the
frames a series captured so far (not on reload / quit). The caller supplies
`operation_id` (8–64 letters, digits, dashes) to make a retried start idempotent;
the MCP server and its [jobs adapter](jobs.md#play-mode-operations) always do.
Operations that drive the same resource (input, the time scale, a capture
series) are exclusive (`playmode_operation_busy`). Records are domain-local and retained for 30 minutes
(at most 64); an unknown id answers `operation_not_found`.

`unity_open_mcp_game_view` `set_resolution` / `set_device` in play mode start a
short settle watch of kind `unity_open_mcp_game_view` (bridge-assigned id, carried
in the response's `settle.operation`): it ends `succeeded` with
`result.settled: true` once the game's `Screen.width`/`height` and the view's
render target report the new size for two consecutive frames, or with
`settled: false` at its time limit. A newer set call cancels the previous watch.
The MCP server polls it inline; a direct caller polls the same endpoint.

## Related docs

- [Routing and lifecycle](routing-lifecycle.md)
- [MCP client configuration](../setup/client-configuration.md)
- [Architecture](../architecture.md)
