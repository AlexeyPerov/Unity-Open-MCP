# Hexa Sort Benchmark (v1)

The first prompt-template benchmark for Unity Open MCP. An agent builds a small,
fully-specified 3D sorting puzzle from a **fixed canonical spec**, while every
step's outcome is checked with MCP tools. The run produces a score and a
release verdict.

This is the **happy path** only — one deterministic build of one level, scored
against a frozen definition. Noisy/recovery scenarios are out of scope for v1.

## Files

| File | Purpose |
| ---- | ------- |
| [`canonical-spec.md`](canonical-spec.md) | The frozen game the agent must build: rules, seed board, solved-state, reference solution. **Read first.** |
| [`prompts.md`](prompts.md) | The ordered prompt steps (A-00 preflight → A-05 validation), each with machine-checkable `done_when`. |
| [`rubric.md`](rubric.md) | How a run is scored; the release PASS/FAIL verdict. |
| [`triage.md`](triage.md) | Failure taxonomy and auto-tag → operator-confirm flow. |
| [`setup.md`](setup.md) | How to prepare a clean project and reset between runs. |
| [`artifacts.md`](artifacts.md) | Required evidence per step + `run-log.jsonl` / `feedback.md` schemas. |

## What it scores

Whether an agent, driven only by these prompts and the canonical spec, can:

1. plan the level;
2. create the scene skeleton;
3. implement the board model and move rules so they **compile** and match the
   spec (including the reference solution and negative cases);
4. add a single-fire win condition; and
5. produce machine-checkable validation evidence.

The score combines task completion, tool success, retries, time, manual
interventions, and acceptance-test pass rate, gated by critical-failure checks.
Full detail in [`rubric.md`](rubric.md).

## How to run

1. Prepare a clean project per [`setup.md`](setup.md) §Setup. Confirm the
   prerequisites checklist and dismiss first-time editor dialogs.
2. Run [`prompts.md`](prompts.md) **in order**, starting with **A-00 preflight**.
   Do not begin A-01 until preflight Overall is PASS.
3. Send each step's `prompt` verbatim to the agent. Let it produce the listed
   artifacts and verify its own `done_when` with the named MCP tools.
4. After A-05, the operator writes [`feedback.md`](artifacts.md) and the
   analysis pass scores the run per [`rubric.md`](rubric.md).

Evidence goes under `Assets/_Benchmark/Run/` in the project under test.

## Scope and limits (v1)

- **Happy path only.** No intentional-break recovery scenario in this version.
- **Loose tool categories.** `allowed_tools` is prose (e.g. "compile/status
  tools"); tool *selection* is not scored — only whether calls succeeded and
  produced the expected artifacts. See [`rubric.md`](rubric.md) §Tool-selection.
- **One model/client.** Pin one configuration per run to minimize variance.
- **Operator-driven.** No automation harness in v1; a human runs and observes.

For the design and methodology behind the whole suite, see
[`../docs/methodology.md`](../docs/methodology.md).
