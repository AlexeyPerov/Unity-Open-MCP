# Unity Open MCP Benchmark Suite

Repeatable prompt-template benchmarks for Unity Open MCP. Each template has an
agent build a real Unity project from a **fixed canonical spec**, while every
step's outcome is checked with MCP tools. Runs produce a score and a release
verdict, so the same template run across versions answers: *is this release
better, worse, or unchanged in practical productivity?*

The suite doubles as a **discovery loop** — failures are tagged with a taxonomy
and routed to bug reports, docs/prompt improvements, or tool-quality work.

## What's here

```
benchmarks/
  README.md              this file
  docs/
    methodology.md       design: flow, scoring, gates, taxonomy, setup approach
  hexa-sort/
    README.md            run instructions for the Hexa Sort template
    canonical-spec.md    the frozen game the agent must build (scoring anchor)
    prompts.md           ordered prompt steps with machine-checkable outcomes
    rubric.md            how a run is scored; PASS/FAIL verdict
    triage.md            failure taxonomy + auto-tag/manual-confirm
    setup.md             project setup and reset between runs
    artifacts.md         required evidence + run-log/feedback schemas
```

## Current templates

| Template | Status | What it exercises |
| -------- | ------ | ----------------- |
| [Hexa Sort](hexa-sort/README.md) | v1 (happy path) | read tools, scene creation, script editing + compile, win-condition logic, validation against a frozen spec |

## How to run

1. Read [`docs/methodology.md`](docs/methodology.md) for the design.
2. Open the template you want (start with [Hexa Sort](hexa-sort/README.md)).
3. Follow the template's `setup.md`, then run its `prompts.md` in order
   (starting with the A-00 preflight).
4. After the run, score it with the template's `rubric.md`.

## v1 scope and limits

- **Happy path only** — no intentional-break recovery scenario yet.
- **One model/client per run** to minimize variance.
- **Loose tool categories** in prompts — tool *selection* is not scored, only
  whether calls succeeded and produced expected artifacts (see each template's
  rubric).
- **Operator-driven** — no automation harness; a human runs and observes.

See [`docs/methodology.md`](docs/methodology.md) §Future expansion for what
comes next.
