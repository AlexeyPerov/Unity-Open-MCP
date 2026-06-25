# Hexa Sort — Scoring Rubric (v1)

How a Hexa Sort run is scored and how the release verdict is reached. Reads its
inputs from the run artifacts in `Assets/_Benchmark/Run/` (see
[`artifacts.md`](artifacts.md)); tags failures per [`triage.md`](triage.md).

## Inputs

- `run-log.jsonl` — machine events (step outcomes, tool calls, retries,
  interventions, timestamps).
- `Assets/_Benchmark/Run/A-0X-*.md` — per-step evidence (preflight, plan, scene,
  gameplay, win condition, validation).
- `feedback.md` — operator narrative (manual interventions, anything not visible
  in the structured log).

## Scored dimensions

Each dimension yields a 0–1 normalized sub-score; the run score is the weighted
sum. v1 weights are equal (1/6 each) unless the project decides otherwise.

| # | Dimension                 | What it measures                                                       | Measurement source                                         |
| - | ------------------------- | ---------------------------------------------------------------------- | ---------------------------------------------------------- |
| 1 | Task completion rate       | Share of steps (A-00..A-05) whose `done_when` fully passed.            | `run-log.jsonl` step-end events (`passed: true`).          |
| 2 | Tool success rate          | Share of MCP tool calls that succeeded (no error envelope).            | `run-log.jsonl` tool-call events.                          |
| 3 | Retry count (inverted)     | Fewer retries is better.                                               | `run-log.jsonl` retry counts per step.                     |
| 4 | Time-to-complete           | Wall-clock from A-00 start to A-05 end.                                | `run-log.jsonl` run-start / run-end timestamps.            |
| 5 | Manual-intervention count  | Fewer operator interventions is better.                                | `run-log.jsonl` `intervention` events + `feedback.md`.     |
| 6 | Acceptance-test pass rate  | Share of canonical-spec checks that passed (reference solution, negatives, solved predicate, compile, scene objects). | `A-05-validation.md` check rows. |

Normalization guidance:

- Dimensions 1, 2, 6: `subscore = passed / total`.
- Dimension 3: `subscore = max(0, 1 − retries / totalStepRetriesAllowed)`.
- Dimension 4: compare against a prior baseline run (or a fixed v1 budget). No
  prior baseline ⇒ record raw minutes and score qualitatively as 1.0 if under
  budget, else 0.5.
- Dimension 5: `subscore = max(0, 1 − interventions / allowedInterventions)`.

`totalStepRetriesAllowed` and `allowedInterventions` are the sum of `max_retries`
across steps and a v1 cap of **2 manual interventions** respectively.

## Critical gates (must be zero)

A run fails regardless of score if any of these fire. Each gate maps to one or
more failure-taxonomy codes (see [`triage.md`](triage.md)).

| Gate                       | Must hold                                              | Related taxonomy codes                              |
| -------------------------- | ------------------------------------------------------ | --------------------------------------------------- |
| Transport health           | No bridge/transport hard failure during the run.        | `transport_failure`                                |
| Tool-contract validity     | No invalid tool contract (schema mismatch, bad shape). | `tool_contract_mismatch`                           |
| Mutation safety            | No destructive or unsafe mutation outside expected scope (`Assets/_Benchmark/`, the HexaSort scripts, the benchmark scene). | `project_state_issue` (unsafe), `tool_quality_issue` |

A preflight FAIL (A-00 not PASS) is an automatic critical-gate failure under
`transport_failure` / `project_state_issue` and aborts scoring of later steps.

## Release verdict

- **PASS** if `run_score >= THRESHOLD` (v1: `0.80`) **and** every critical gate
  is clean.
- **FAIL** otherwise.

Record the verdict, the run score, per-dimension sub-scores, and any triaged
failures in the analysis-pass scorecard (see `benchmarks/docs/methodology.md`
§Analysis-pass output).

## Tool-selection scoring (note on v1 looseness)

`allowed_tools` in [`prompts.md`](prompts.md) is deliberately **loose prose**
for v1 (e.g. "compile/status tools"). Consequence: tool *selection* is **not**
scored as a dimension this release — we only score whether the tool calls that
were made succeeded and produced the expected artifacts. When the project later
tightens `allowed_tools` to exact group ids / wire names, a "tool-selection
fidelity" dimension can be added here without changing the rest of the rubric.

## What is explicitly not scored in v1

- Visual fidelity / aesthetics (a cube piece is acceptable per
  `canonical-spec.md` §8).
- GUI input handling (the benchmark drives the board-model API; §8).
- Performance / frame rate (no play-mode profiling in v1).
- Anything in `canonical-spec.md` §9 (out of scope).
