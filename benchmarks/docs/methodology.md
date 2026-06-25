# Benchmark Methodology

This document describes the approach behind the Unity Open MCP benchmark suite:
a lightweight **release-quality gate** and **discovery loop** that evaluates
Unity Open MCP on a real Unity project using repeatable prompt templates,
structured evidence, and a post-run analysis pass.

For the ready-to-run template, see [`../hexa-sort/`](../hexa-sort/).

## Why this exists

After implementation and manual feature testing, we still need answers to:

- Does Unity Open MCP work reliably in realistic end-to-end agent workflows?
- Which failures are tool failures vs prompt quality vs model behavior?
- Is each release better, worse, or unchanged in practical productivity?

The suite is designed as a lightweight release-quality gate and discovery loop.

## Product decisions (v1)

- **Primary goal:** regression gate before releases, plus feature/opportunity
  discovery.
- **Evaluation unit:** end-to-end template runs, each step with a
  machine-checkable outcome.
- **Prompt strictness:** deterministic scripted prompts (low variance).
- **Model/client scope:** one model/client per run (reduce noise).
- **Triage policy:** auto-tagged failure taxonomy with manual confirmation.
- **Pass policy:** score threshold + critical-failure gates.
- **Setup approach:** mixed manual + scripted.
- **First template scope:** Hexa Sort (3D, one level), happy path only.

## Core concept

Each benchmark template is a package containing:

- a **fixed canonical spec** the agent must implement to (rules, board,
  solved-state) — the scoring anchor, not something the agent invents;
- a fixed sequence of prompts for the build workflow;
- scenario acceptance criteria and a scoring rubric;
- setup/reset instructions (scripted where stable, manual where fragile);
- run logging and feedback instructions.

The same template is run repeatedly across versions to compare results.

## v1 scope

- A single template: **Hexa Sort (3D, one level)** — see
  [`../hexa-sort/canonical-spec.md`](../hexa-sort/canonical-spec.md).
- **Happy path only** in v1 (no intentional-break recovery scenario).
- One model/client path per run.

## Benchmark flow

1. Operator prepares a fresh Unity project from the template prerequisites
   ([`../hexa-sort/setup.md`](../hexa-sort/setup.md)).
2. Operator applies the template package.
3. Operator configures Unity Open MCP for that project.
4. **A-00 preflight** confirms the bridge and tool surface are healthy before
   any timed work.
5. Operator runs the prompt sequence exactly as authored.
6. For each step, the agent captures required artifacts and verifies its own
   `done_when` with the named MCP tools.
7. The run writes a machine log (`run-log.jsonl`) and the operator writes a human
   narrative (`feedback.md`).
8. An analysis pass scores run quality and emits bug/improvement tasks.

## Data outputs

Two outputs per run (see [`../hexa-sort/artifacts.md`](../hexa-sort/artifacts.md)
for schemas):

- `run-log.jsonl` (machine-oriented, one event per line): step start/end
  timestamps; prompt id; tools invoked with success/failure; retries; manual
  interventions; failure code.
- `feedback.md` (human narrative): what was attempted; what worked or felt
  awkward; quality notes not visible in structured logs; the exact model/client/
  temperature used.

This split supports both automated aggregation and useful human context.

## Scoring and gate model

Each run produces a score and a gate verdict. Full detail in
[`../hexa-sort/rubric.md`](../hexa-sort/rubric.md).

- **Scored dimensions:** task completion rate; tool success rate; retry count
  (inverted); time-to-complete; manual-intervention count; acceptance-test pass
  rate.
- **Critical gates (must be zero):** bridge/transport hard failures; invalid
  tool contracts; destructive or unsafe mutation outside expected scope.
- **Release verdict:** PASS if score ≥ threshold (v1: 0.80) and all critical
  gates pass; FAIL otherwise.

## Failure taxonomy (auto-tag + manual confirm)

Every failed step is tagged with one code; auto-tagging proposes the first
label, the operator confirms or corrects during triage. Codes:

- `transport_failure` (bridge/network/process/lifecycle)
- `tool_contract_mismatch` (schema mismatch, invalid args/shape)
- `tool_quality_issue` (tool runs but produces wrong/low-quality result)
- `prompt_ambiguity` (instruction too vague/conflicting)
- `agent_reasoning_failure` (model misunderstanding or poor planning)
- `project_state_issue` (Unity state/import/recompile/environment drift)

See [`../hexa-sort/triage.md`](../hexa-sort/triage.md) for the auto-tag
heuristics and confirm flow.

## Deterministic prompt protocol

Every prompt step in a template defines:

- `goal`
- `allowed_tools` (loose prose categories in v1)
- `expected_artifacts`
- `done_when` — **machine-checkable**, each naming the MCP tool that verifies it
- `timeout`
- `max_retries`

This keeps runs comparable between versions. Note that `allowed_tools` is
deliberately loose in v1; tool *selection* is not scored, only whether calls
succeeded and produced expected artifacts.

## Mixed manual + scripted setup

### What "scripted setup" means here

A predefined deterministic sequence that prepares project state the same way
every run, for example:

- fixture folder creation;
- deterministic config patches (fixed game constants baked into the canonical
  spec);
- MCP preflight calls to confirm a healthy baseline;
- setup/reset logging.

### Why mixed is best for v1

- manual-only is too noisy and non-repeatable (first-time dialogs, import timing
  vary across machines and Unity versions);
- fully automated (headless, scripted dialogs) is expensive for v1 and brittle;
- mixed provides reproducibility where it matters without blocking startup.

See [`../hexa-sort/setup.md`](../hexa-sort/setup.md) for the concrete steps.

## Analysis-pass output

After the run, the analysis pass produces:

- a benchmark scorecard (run score + per-dimension sub-scores + gate verdict);
- top failure clusters by taxonomy code;
- mapped actions:
  - bug reports (transport / contract / quality / state issues);
  - docs and prompting improvements (`prompt_ambiguity`);
  - tool quality improvements;
  - follow-up benchmark additions.

## Versioning and comparison

Every run stamps `run-log.jsonl` with the Unity Open MCP version (from ping),
the Unity version, and the model/client/temperature. Comparisons across
versions are only valid when the canonical spec and prompt pack are unchanged;
changing either requires a benchmark version bump.

## Future expansion

After Hexa Sort is stable:

- add a noisy path (small controlled issue) and a recovery path
  (intentional break + expected fix behavior) to the Hexa Sort template;
- add second and third templates;
- tighten `allowed_tools` to exact group ids / wire names and add a
  tool-selection-fidelity scoring dimension;
- add an A/B mode for comparing tool-surface variants;
- add lightweight aggregate trend reporting across versions.
