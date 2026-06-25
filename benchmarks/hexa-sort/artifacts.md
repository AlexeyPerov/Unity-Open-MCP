# Hexa Sort — Run Artifacts (v1)

Defines what evidence each step must produce, and the schemas for the two run
outputs (`run-log.jsonl`, `feedback.md`). The rubric ([`rubric.md`](rubric.md))
and triage ([`triage.md`](triage.md)) read from these.

All evidence lives under `Assets/_Benchmark/Run/`.

## Per-step required evidence

| Step | Artifact(s) | `done_when` assertion + verifying MCP tool |
| ---- | ----------- | ------------------------------------------ |
| A-00 | `A-00-preflight.md`, `run-log.jsonl` (run-start) | ping `connected:true`; capabilities non-empty + groups `core`/`gate-and-verify`/`typed-editor` available; `list_groups` sees them; `read_compile_errors` = 0. |
| A-01 | `A-01-plan.md` | File exists with all six sections; references canonical board (`SLOT_COUNT=4`,`CAP=2`,`TARGET=2`) and §3 seed verbatim. |
| A-02 | scene + `A-02-scene-summary.md` | `scene_get_data`/`gameobject_find` show `BoardRoot`,`SlotsRoot`,`PiecesRoot`,`GameManager` + a camera + a light; summary file exists. |
| A-03 | scripts under `Assets/_Benchmark/HexaSort/` + `A-03-gameplay-notes.md` | `compile_check` (fallback `read_compile_errors`) = 0 errors; `script_read` confirms script files exist; notes file documents classes + each `MoveResult`. |
| A-04 | updated scripts + `A-04-win-condition.md` | `compile_check` = 0 errors; doc states exact solved predicate (§6) + duplicate-fire guard. |
| A-05 | `A-05-validation.md`; `run-log.jsonl` run-end | `compile_check` = 0; `scene_get_data` confirms roots; §7 reference solution replays to `IsSolved==true`,`SolvedFiredCount==1`; §7 negatives return expected `MoveResult`; validation doc has PASS/FAIL per check. |

## `run-log.jsonl` — machine event log

One JSON object per line (JSON Lines). Append-only through the run. Every event
has `ts` (ISO-8601), `run_id`, `type`, and type-specific fields.

### `run_start` (written by A-00)
```json
{ "ts": "...", "run_id": "2026-06-25T10-00-00", "type": "run_start",
  "suite": "hexa-sort", "suite_version": "1",
  "open_mcp_version": "<from ping>", "unity_version": "<from ping>",
  "model": "<model id>", "client": "<client>", "temperature": 0.0 }
```

### `step_start` (first event of each step)
```json
{ "ts": "...", "run_id": "...", "type": "step_start", "step": "A-02",
  "attempt": 1 }
```

### `tool_call` (one per MCP tool invocation)
```json
{ "ts": "...", "run_id": "...", "type": "tool_call", "step": "A-02",
  "tool": "unity_open_mcp_gameobject_create",
  "ok": true, "error_code": null, "duration_ms": 412 }
```
`ok:false` events carry an `error_code` (the server/tool error code, or
`"transport"`/`"timeout"`). Triage reads `ok`/`error_code` to auto-tag.

### `retry` (when a step is retried within its `max_retries`)
```json
{ "ts": "...", "run_id": "...", "type": "retry", "step": "A-03",
  "attempt": 2, "reason": "compile errors after first pass" }
```

### `intervention` (operator stepped in — manual fix, dialog dismissed, etc.)
```json
{ "ts": "...", "run_id": "...", "type": "intervention", "step": "A-02",
  "by": "operator", "summary": "dismissed package import dialog" }
```

### `step_end` (last event of each step)
```json
{ "ts": "...", "run_id": "...", "type": "step_end", "step": "A-02",
  "attempt": 1, "passed": true, "duration_ms": 19830,
  "failure_code": null, "root_cause": null }
```
On failure: `passed:false`, `failure_code` = the **provisional** auto-tag from
[`triage.md`](triage.md) (confirmed/corrected by the operator into the
scorecard), `root_cause` = one-line note.

### `run_end` (written by A-05)
```json
{ "ts": "...", "run_id": "...", "type": "run_end",
  "steps_passed": 6, "steps_total": 6,
  "critical_gate_failures": [], "verdict": "pending-analysis" }
```
`verdict` is `pending-analysis` here; the final PASS/FAIL comes from the
analysis pass per [`rubric.md`](rubric.md).

## `feedback.md` — human narrative

Written by the **operator** after the run (not the agent). Free-form markdown,
but must include these sections so the analysis pass can find them:

- **Run header** — `run_id`, date, operator.
- **Model / client / temperature** — the exact configuration used (mirrors
  `run_start`; lets the analysis pass correlate narrative with config).
- **What was attempted** — one or two sentences per step.
- **What worked** — steps or sub-behaviors that went smoothly.
- **What felt awkward / failed** — anything not visible in the structured log:
  confusing prompts, surprising tool output, repeated agent mistakes.
- **Manual interventions** — bullet list, one per `intervention` event, with the
  step and what was done.
- **Notes for next version** — prompt/setup/rubric improvement ideas discovered
  during the run.

## Artifact ownership summary

| Output               | Who writes it        | When                          |
| -------------------- | -------------------- | ----------------------------- |
| `run-log.jsonl`      | agent (per A-00..A-05) | opened in A-00, closed in A-05 |
| `A-0X-*.md` evidence | agent                | end of each step              |
| `feedback.md`        | operator             | after the run                 |
| scorecard            | analysis pass        | after the run (see methodology) |
