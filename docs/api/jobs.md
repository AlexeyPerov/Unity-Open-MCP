# Asynchronous jobs

`unity_open_mcp_jobs` is an always-visible, local MCP tool. Observation remains
available when Unity is offline or reloading. It owns jobs in one MCP server
session; it does not turn synchronous tool calls into background work.

Supported targets are `unity_senses_run_tests`, available project commands
that declare `Async = true` with the async authoring signature, play-mode
operations (`unity_open_mcp_inputsim_gesture`, `unity_open_mcp_playtest_wait`,
`unity_senses_capture_series`), and Editor waits
(`unity_open_mcp_editor_status` with `wait_until`). Other targets return
`job_operation_unsupported`; builds, imports, package resolution, and baking
retain their existing contracts.

## Adopted operations

For tests, use `start` with `tool_or_command: "unity_senses_run_tests"`, the usual
test filters in `args`, and an `idempotency_key`. The job owns `run_id`; supplying
one is rejected. The existing test runner and result-file handoff run unchanged,
including PlayMode reload support. The default observation budget is 600000 ms;
`args.timeout_ms` can set 1000–600000 ms. Exhausting that budget means `orphaned`,
not proof the tests stopped. Cancellation is unsupported (`not_cancellable`).
Progress reports coarse observation phases, without invented percentages.
Terminal results retain the test summary; failing tests or an aborted run make
the job fail. Tests have no mutation gate in their existing runner contract.
Direct calls to `unity_senses_run_tests` still start and wait synchronously.
Native execution is deferred to a one-shot Editor update after acknowledgement;
starting a run does not depend on an inspector repaint. Import workers do not
manage native test markers. Cleanup preserves markers owned by another live
Editor process, and reload reattaches only the current Editor's markers. Entering
PlayMode aborts only an existing EditMode run; the requested PlayMode run retains
its callbacks/marker for reload and result-file handoff.

For a project command, pass its exact catalog id as `tool_or_command` and an
invocation envelope as `args`, for example:

```json
{
  "action": "start",
  "tool_or_command": "project.demo.long_write",
  "idempotency_key": "authoring-pass-1",
  "args": {"args": {"seconds": 60}, "gate": "enforce"}
}
```

After start, call `{"action":"status","job_id":"<returned id>"}`, then
`{"action":"wait","job_id":"<returned id>","timeout_ms":10000}` until terminal.
A queued/running response is not completion. Inspect the terminal result and gate,
including failed or cancelled jobs; keep the same routing identity for every call.

The nested `args` holds typed command parameters. `schema_version`, `paths_hint`,
`gate`, `ignore_scene_dirty`, and `confirm_bypass` belong alongside it. Starts
require an idempotency key, including read-only async commands. Describe first;
the bridge rechecks declaration, schema, enabled state, deny rules and scope.
Mutations capture one checkpoint before invoking user code and validate once
when it finishes, including partial failure or acknowledged cancellation. The
terminal `result` retains the gate envelope and project identity with `jobId`.
Status, wait, and duplicate starts never repeat validation.

Project commands run on the Editor synchronization context and must yield between
bounded steps. `ProjectCommandContext.ReportPhase` supplies phase-only progress;
`CancellationToken` lets commands acknowledge cancellation at a safe boundary.
The job remains `cancel_requested` until acknowledged; completion may win the
race. Commands without `Cancellable = true` reject cancellation. Other bridge
mutations and test starts are refused with `job_busy` during a project job, so
they cannot contaminate its checkpoint/validation interval. This includes the
gate-free Editor-state writes, alone or as `batch_execute` steps: play-mode
changes, undo/redo, selection changes, PlayerPrefs/EditorPrefs writes and console
clears. `execute_csharp` is refused even with `read_only: true`, because the
bridge cannot verify that a snippet writes nothing. The `job_busy` error names the
running job (`error.job.jobId`, `commandId`, `state`, `phase`) and carries
`agentNextSteps`: the starting agent can `wait` on that id, other agents get
`job_not_found` for it. Reads, console logging,
profiler recording and the job's own status/cancel calls stay available. Operator edits and other Editor plugins remain outside this
scheduling guarantee.

Only settled EditMode starts and `None` (read-only) / `EditorSettle` (mutating)
lifecycles are supported. Project-command execution is domain-local: a reload
that loses the bridge's job record becomes `orphaned`, never an automatic restart.
A busy or briefly unreachable Editor is not lost ownership: a failed status poll
(connection error, timeout or HTTP 5xx) is retried with backoff, with lifecycle `disconnected`
until the bridge answers again (`connected`). A cancel request is sent once; later
polls show whether it was acknowledged. The job becomes `orphaned` when the bridge
answers no poll for 5 minutes or reports no terminal state within 30 minutes of
start. The bridge retains at most 256 records for 30 minutes after completion; the
MCP session owns observation and idempotency. See [authoring](project-commands.md#asynchronous-authoring).

## Play-mode operations

Tools whose work runs across real play-mode frames share one bridge-side
scheduler and one generic job adapter (`playmode_operation`):
`unity_open_mcp_inputsim_gesture`, `unity_open_mcp_playtest_wait` and
`unity_senses_capture_series`. Pass the tool's ordinary arguments as `args`:

```json
{
  "action": "start",
  "tool_or_command": "unity_open_mcp_playtest_wait",
  "args": {"condition": {"log": {"pattern": "Wave \\d+ cleared"}}, "time_scale": 2, "timeout_ms": 120000}
}
```

The operations are read-only, so `idempotency_key` is optional (it still
deduplicates retries). The job owns `operation_id` (supplying one is rejected):
the job id is the bridge operation id. The direct tool call starts a job by itself
when the operation's worst case exceeds the 40 s inline budget — a gesture's last
keyframe or capture, a wait's `timeout_ms`, a capture series' `start_on` wait plus
its schedule plus encoding — and returns that job snapshot; an `operation_id`
passed to that direct call becomes the job's `idempotency_key`.

A direct call whose operation turns out slower than its schedule (`gameplay` time
at a low `Time.timeScale`, a slow frame rate) is never cancelled: once the
observed pace (`progress.elapsedMs` against wall time) says it cannot end inside
the inline limit (40 s plus 10 s of grace), or at that limit, the call hands the
running operation to a job and answers `status: "running"` with the `job_id`
(the operation id itself), `progress` (`clock`, `elapsed_ms`, `expected_ms`,
`rate`, `eta_ms`), what it has so far (a series: `series` with the frames
captured) and the `operation` snapshot. That job is a continuation: it observes
the running operation and starts nothing (a record lost to a reload fails it as
`operation_lost`, never a restart), and it holds no execution slot. The CLI has
no host timeout and its jobs end with the process, so there these operations run
inline for their whole duration.

The adapter starts the operation through the tool (the bridge validates, routes
input and refuses as usual — a refusal is a `failed` job: `error` carries the
bridge's code and message, `result.error` the full refusal object with its detail
such as `detected`, and `result.agentNextSteps` the tool's recovery steps), then
polls `POST /playmode-operations`.
Progress reports the scheduler phase, `elapsedMs / expectedMs` as a fraction and
`progress.detail`: `clock`, `elapsed_ms`, `expected_ms`, `rate` (operation-clock
ms per wall ms), `eta_ms` (remaining wall time, encoding included), `stalled_ms`
and, for a capture series, `captured` / `requested` (for a series `elapsed_ms`
is the schedule offset reached, 0 while `start_on` waits). The operation
snapshot names the clock of `progress.elapsedMs` as
`progress.clock` (`realtime` for a wait, whose `timeout_ms` is wall-clock). The
tool result's own `time` is the clock of its `ms` conditions / keyframes and of
its `elapsed_ms`; `realtime_ms` is always wall time.
Operations are cancellable: `cancel` is forwarded once and the job becomes
`cancelled` after the bridge acknowledges it, having released held input and
restored a wait's time scale. Terminal results keep the operation snapshot under
`operation` plus the tool's own result (a gesture: `trace`, `frames`, `errors`,
`released`, `captures`; a wait: `outcome`, `fired`, `condition`, `time_scale`,
`errors`; a series: `series.frames`, `contact_sheet`). An operation that ends on
its own terms without success fails with the operation's `error_code` — a wait
that `timed_out` or stopped on `exception_thrown` / `error_logged`, a series whose
`start_on` timed out — and keeps the full result (every leaf's last observed
state). An operation that made no progress for 15 s (a paused Editor, a
`gameplay` clock at `Time.timeScale` 0) is ended by the bridge and the job fails
with the tool's stalled code (`capture_series_stalled`, `wait_stalled`,
`gesture_stalled`); `result.operation.stall.reason` says why (`editor_paused`,
`time_scale_zero`, `no_frames`, `no_progress`). Exiting play mode ends a running
operation; the job then fails with code `play_mode_exited` (cleanup has run:
input released, time scale restored, frames captured so far written; the message
says how many). Records are domain-local: after a domain reload the
bridge answers `operation_not_found` (its cleanup already ran) and the job becomes
`orphaned` — never restarted. A status poll that cannot reach the bridge is retried
with backoff (lifecycle `disconnected` until it answers); after 2 minutes without
an answer, or without a terminal state by the deadline — 10 minutes, or the
operation's worst case plus 2 minutes when longer (a 10-minute wait), counted
from the last progress, so a slow operation is never cut off — the job is
`orphaned` (a cancel is sent first).

## Editor waits

An [`editor_status`](mcp-tools.md#unity_open_mcp_editor_status) wait whose
`timeout_ms` outlasts its 40 s inline window continues as a job: the call returns
`status: "waiting"` with the `job_id`, and the job's result is the wait's answer
(`reached`, `mode`, `transitionsSeen`, `compile: {generation, status,
errorCount}`). `recompile_scripts` and a compile-starting `assets_refresh` start
the same job when their compile outlasts the inline window (they answer
`status: "compiling"` with its `job_id`). It can also be started directly:

```json
{
  "action": "start",
  "tool_or_command": "unity_open_mcp_editor_status",
  "args": { "wait_until": "idle", "after_generation": 42, "timeout_ms": 300000 }
}
```

The wait polls the instance lock, `/ping` and `/compile-state` from the server, so
a domain reload is something it waits through, not a lost owner. A wait that is
not reached fails with the wait's code (`wait_timeout`, `wait_not_reached`,
`bridge_compile_failed` — only with `Editor.log` evidence, `bridge_offline`); `cancel` stops it at once. It is an
**observer**: it changes nothing, needs no `idempotency_key`, and holds no
project or agent execution slot — it never queues behind a running test job nor
blocks one. Up to eight observers run at a time.

## Calls

| Action | Arguments | Result |
|---|---|---|
| `start` | `tool_or_command`, optional `args`, `idempotency_key` (required for mutations) | Queued job snapshot, returned before execution starts |
| `status` | `job_id` | Current snapshot |
| `wait` | `job_id`, optional `timeout_ms` (default 10000, range 0–30000) | Snapshot on terminal completion or observation timeout |
| `cancel` | `job_id` | Snapshot after requesting cooperative cancellation |
| `list` | Optional `state`, `operation`, `limit` (1–100, default 20), `cursor` | `jobs` and `next_cursor` |

Arguments belonging to another action are rejected. Each snapshot includes
`job_id`, operation, project/agent owner, state, creation/update/start/finish
Unix timestamps in milliseconds, progress when known, declared cancellability,
lifecycle state/evidence (`not_started` until the adapter reports transport state),
up to 32 recent events, and terminal result/error when
available. A job that has to be orphaned while its adapter is still returning
(ownership evidence lost) keeps that last result and records the adapter's own
outcome as an `adapter_outcome` event; `orphaned` stays the authoritative state.
All responses use the normal local source/error envelope.

Jobs follow `queued` → `running` → `succeeded`, `failed`, or `orphaned`.
Cooperative cancellation adds `cancel_requested` → `cancelled`; completion may
win a cancellation race and report success or failure. Terminal states never
revert. Queued cancellation prevents dispatch. An operation without a cancellation
contract returns `not_cancellable` without changing state, even while queued.
A running job reaches `cancelled` only after the adapter acknowledges the request.

`wait` is only an observer. Its timeout, client cancellation, or disconnected
caller does not cancel execution. Retrieve the same job id on a later call.

## Identity, limits, and retention

Ownership uses the configured project plus per-call bridge port override and the
tool arguments' `_meta.agentId` (default: MCP process identity). Every action checks
ownership. These are trusted local routing identities, not authentication
credentials; clients sharing a process must use distinct agent ids consistently.
Port overrides have separate namespaces; use the same routing metadata on every
call. A call made through `manage_tools(action: "invoke")` is owned by the
`manage_tools` call's routing metadata, exactly like a direct call. Jobs from
separate MCP processes are not shared.

Idempotency keys are scoped to that owner. Identical target and arguments return
the original job, including terminal/orphaned jobs; different arguments return
`idempotency_conflict`. Keys are required for mutations and may be used for reads.
A project-command start with a known key is answered from that record without
reading the command catalog, so the retry works while the Editor reloads or after
a reload changed the command's declaration; only a new key needs the catalog.
Validation and key reservation precede scheduling, so concurrent retries cannot
start the same mutation twice.

Defaults are four executing jobs globally, one per project/port and one per
project/agent (Editor waits, which only observe, run beside them with their own
limit of eight). Additional jobs queue in creation order; an eligible independent
project can proceed while another project is busy. An orphan whose adapter has
not returned keeps its execution slot, because its work may still be running.

The server retains up to 256 records, with terminal results and keys retained for
30 minutes after completion. Active/unresolved records are never evicted. At
capacity, starts fail with `job_capacity`; unexpired keys are not sacrificed.
Cleanup runs at least once per minute and before reads/starts. Arguments and
terminal payloads have a 256 KiB JSON limit; there are at most 128 simultaneous
waiters. Event/phase text is bounded to 1024 characters. Oversized terminal data
cannot be reported as success: the result becomes an unknown outcome.

List cursors are bound to owner, filters and server session. Pages follow creation
order and exclude jobs created after the first page. State filters reflect the
current state on each page; records may expire between pages.

After eviction the id/key no longer exists and reuse can start new work. A server
restart loses all records and idempotency protection: an old id returns
`job_not_found`. There is no persistence, automatic replay, or cross-process
resumption. Do not retry an uncertain mutation simply because its record is gone;
inspect operation-specific evidence first.

## Adapter contract

`mcp-server/src/jobs/job-manager.ts` exports `JobOperation`, `JobContext` and
`JobOutcome`. Register an explicit adapter with the server router's job manager.
The adapter declares mutation/cancellation support, validates arguments without
side effects, and provides an asynchronous `run(args, context)` function.

Execution must yield promptly and retain the owning operation's argument,
agent/project routing, scope, checkpoint, gate and terminal validation contracts.
The manager never invokes arbitrary tools and never retries execution. Unity APIs
still run on Unity's main thread; progress must use native callbacks or coarse
observable phases, not a blocking polling loop. `context.report` accepts a phase
and an optional actual fraction in [0,1]; omit percentages when unknown.

Adapters report reload/disconnect/reconnect through `context.lifecycle` with
operation-specific ownership evidence. Proven continuing ownership retains the
running (or cancel-requested) state; a later explicit result completes normally.
Lost ownership becomes terminal `orphaned`. Late callbacks cannot overwrite it.
Thrown transport/runtime errors also become `orphaned`, because a broken socket
does not prove remote failure. Only an explicit failed outcome produces `failed`.
An adapter must detect ownership loss itself; a general bridge heartbeat cannot
prove which native operation still belongs to a job.

Cancellation uses `context.signal`; return `cancelled` only after a safe boundary
acknowledges it. Server shutdown does not imply that remote work was cancelled.
