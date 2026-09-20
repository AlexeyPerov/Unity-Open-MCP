# Asynchronous jobs

`unity_open_mcp_jobs` is an always-visible, local MCP tool. Observation remains
available when Unity is offline or reloading. It owns jobs in one MCP server
session; it does not turn synchronous tool calls into background work.

Supported targets are `unity_senses_run_tests` and available project commands
that declare `Async = true` with the async authoring signature. Other targets
return `job_operation_unsupported`; builds, imports, package resolution, and
baking retain their existing contracts.

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
they cannot contaminate its checkpoint/validation interval. Operator edits and
other Editor plugins remain outside this scheduling guarantee.

Only settled EditMode starts and `None` (read-only) / `EditorSettle` (mutating)
lifecycles are supported. Project-command execution is domain-local: a reload or
transport loss becomes `orphaned`, never an automatic restart. The bridge retains
at most 256 records for 30 minutes after completion; the MCP session owns
observation and idempotency. See [authoring](project-commands.md#asynchronous-authoring).

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
available. All responses use the normal local source/error envelope.

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
call. Jobs from separate MCP processes are not shared.

Idempotency keys are scoped to that owner. Identical target and arguments return
the original job, including terminal/orphaned jobs; different arguments return
`idempotency_conflict`. Keys are required for mutations and may be used for reads.
Validation and key reservation precede scheduling, so concurrent retries cannot
start the same mutation twice.

Defaults are four executing jobs globally, one per project/port and one per
project/agent. Additional jobs queue in creation order; an eligible independent
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
