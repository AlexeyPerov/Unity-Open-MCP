# Hexa Sort Benchmark Prompts (v1)

Deterministic prompt pack for the Hexa Sort release-quality benchmark.

> **Read [`canonical-spec.md`](canonical-spec.md) first.** Every step implements
> *that* spec. The rubric scores against it. Do not invent your own rules, board,
> or win condition — implement the one already defined.

## Run contract

- Use **one fixed model/client configuration** for all steps in this file.
  Record the exact model id, client, and temperature in `feedback.md` (see
  `artifacts.md`).
- Run steps **in order**; do not skip or reorder.
- Do not broaden scope beyond what a step asks. No animation, polish, save/load,
  or networking (see `canonical-spec.md` §9).
- Keep all evidence outputs under `Assets/_Benchmark/` only.
- If a step fails, follow its `max_retries`; then mark it failed with a reason
  and continue to the next step. Record every attempt in `run-log.jsonl`.
- Begin each step by appending a **step-start event** to `run-log.jsonl`; end
  with a **step-end event** carrying the outcome. Schema in `artifacts.md`.

## Step format

Each step lists:

- `goal` — what the step must achieve.
- `allowed_tools` — prose category of tools permitted (deliberately loose in v1;
  see [`rubric.md`](rubric.md) §Tool-selection scoring).
- `expected_artifacts` — files/objects the step must produce.
- `done_when` — **machine-checkable** assertions, each naming the MCP tool that
  verifies it.
- `timeout` — wall-clock budget.
- `max_retries` — attempts before marking the step failed.
- `prompt` — the text to send to the agent verbatim.

---

### A-00 Preflight

- goal: Confirm Unity Open MCP is wired, healthy, and exposes the surface the
  benchmark needs **before** any timed work begins.
- allowed_tools: read-only meta/discovery tools (`unity_open_mcp_capabilities`,
  `unity_open_mcp_manage_tools`, `unity_open_mcp_ping`, `unity_open_mcp_read_compile_errors`).
- expected_artifacts: `Assets/_Benchmark/Run/A-00-preflight.md`; open
  `Assets/_Benchmark/Run/run-log.jsonl` with a run-start event.
- done_when (all must hold; tool named for each):
  1. `unity_open_mcp_ping` returns `connected: true`. *(transport)*
  2. `unity_open_mcp_capabilities` returns a non-empty tool list. *(surface)*
  3. `unity_open_mcp_capabilities` reports `available: true` for the groups the
     benchmark depends on: `core`, `gate-and-verify`, `typed-editor` (these are
     default-on; assert them anyway so a regression here fails loudly).
  4. `unity_open_mcp_manage_tools` with action `list_groups` shows the session
     can see those three groups as `active` or `available`. *(session visibility)*
  5. `unity_open_mcp_read_compile_errors` reports **zero** compile errors at
     baseline. If it reports errors and the bridge is unreachable, treat as a
     `transport_failure` and stop (see [`triage.md`](triage.md)).
- timeout: 5m
- max_retries: 1
- prompt:

```text
Run the benchmark preflight for the Hexa Sort run and write the report.

Step 1 — health: call the MCP ping tool. Record whether it reports connected: true.

Step 2 — surface: call the capabilities tool. Record the total tool count and
whether each of these groups is available: core, gate-and-verify, typed-editor.

Step 3 — session visibility: call the manage_tools tool with action list_groups.
Record whether core, gate-and-verify, and typed-editor are active/available to
this session.

Step 4 — baseline compile health: read compile errors from the editor log.
Record the error count. It must be zero to proceed.

Write the report to Assets/_Benchmark/Run/A-00-preflight.md with this structure:
- Connected: PASS/FAIL + value
- Surface present: PASS/FAIL + tool count
- Required groups available: PASS/FAIL + per-group line (core, gate-and-verify,
  typed-editor)
- Session visibility: PASS/FAIL + per-group line
- Baseline compile: PASS/FAIL + error count
- Overall: PASS only if every line is PASS; otherwise FAIL with the first
  failing line quoted.

Then create Assets/_Benchmark/Run/run-log.jsonl and append a run-start event
including the run id, the Unity/Open-MCP version reported by ping, and the
date/time. Do not begin A-01 until preflight Overall is PASS.
```

### A-01 Plan

- goal: Produce a concrete plan for implementing `canonical-spec.md` — not a
  design from scratch.
- allowed_tools: read-only inspection tools; the canonical-spec read.
- expected_artifacts: `Assets/_Benchmark/Run/A-01-plan.md`.
- done_when:
  1. `Assets/_Benchmark/Run/A-01-plan.md` exists and contains all six required
     sections (manual check against the section list; the file is the artifact).
  2. The plan references the canonical board (`SLOT_COUNT=4`, `CAP=2`,
     `TARGET=2`) and the seed board from `canonical-spec.md` §3 verbatim.
- timeout: 10m
- max_retries: 1
- prompt:

```text
You are implementing a benchmark level: Hexa Sort. The full, authoritative rules
are in benchmarks/hexa-sort/canonical-spec.md. Read it and implement to it; do
not invent your own rules, board, or win condition.

Produce ONLY an implementation plan. Do not write gameplay code yet.

Write it to Assets/_Benchmark/Run/A-01-plan.md with these exact sections:
1) Scope — in/out for v1 (must match canonical-spec.md §9 out-of-scope list).
2) Data model — the HexaBoard / HexColor / MoveResult types from §2, and how the
   seed board from §3 will be represented.
3) Move rule — how you will enforce the legality rules in §4 and return the
   correct MoveResult for each failure.
4) Win condition — how the solved-state predicate (§6) and the single-fire
   completion signal (§5) will be implemented.
5) Implementation task list — ordered steps to build board model, scene skeleton,
   and wiring (A-02..A-05).
6) Risks and mitigations — at least: how you keep the board model UnityEngine-free
   so EditMode tests can run it, and how you prevent duplicate completion firing.

Keep it deterministic and simple. No save/load, networking, procedural gen, or
polish (canonical-spec.md §9).
```

### A-02 Scene skeleton

- goal: Create the baseline scene skeleton that will host the board.
- allowed_tools: Unity Open MCP mutate tools for scene/object creation.
- expected_artifacts: scene file; `Assets/_Benchmark/Run/A-02-scene-summary.md`.
- done_when (verify each with the named tool):
  1. `unity_open_mcp_scene_get_data` (or `gameobject_find`) shows root objects
     `BoardRoot`, `SlotsRoot`, `PiecesRoot`, and `GameManager` exist.
  2. `unity_open_mcp_scene_get_data` shows a camera and a directional light.
  3. `Assets/_Benchmark/Run/A-02-scene-summary.md` exists and lists the created
     hierarchy.
- timeout: 15m
- max_retries: 2
- prompt:

```text
Create the scene skeleton for Hexa Sort per the plan in A-01 and canonical-spec.md
§8 (scene representation).

Create (if missing): a main camera and a directional light.
Create root GameObjects: BoardRoot, SlotsRoot, PiecesRoot, GameManager.
Under SlotsRoot create four slot placeholders (indices 0..3) matching SLOT_COUNT.
The empty/fourth slot from the seed board must still exist as a placeholder.

After creation, write Assets/_Benchmark/Run/A-02-scene-summary.md with:
- The full created hierarchy (tree).
- Which objects/components were newly created vs already present.
- Any assumptions made.

Do not implement gameplay yet.
```

### A-03 Core gameplay scripts

- goal: Implement the board model + move logic exactly per `canonical-spec.md`
  §2–§4, and compile cleanly.
- allowed_tools: Unity Open MCP file/script-editing and compile/status tools.
- expected_artifacts: gameplay scripts under `Assets/_Benchmark/HexaSort/`;
  `Assets/_Benchmark/Run/A-03-gameplay-notes.md`.
- done_when (verify each with the named tool):
  1. `unity_open_mcp_compile_check` (fallback: `unity_open_mcp_read_compile_errors`)
     reports **zero** compile errors.
  2. `unity_open_mcp_script_read` (or equivalent read) confirms the declared
     script files exist under `Assets/_Benchmark/HexaSort/`.
  3. The notes file exists and documents class responsibilities and how each
     `MoveResult` is produced.
- timeout: 25m
- max_retries: 2
- prompt:

```text
Implement the Hexa Sort board model and move logic per canonical-spec.md §2-§4.

Place scripts under Assets/_Benchmark/HexaSort/ in namespace HexaSort.

Implement exactly:
- HexColor enum (Red, Green, Blue).
- HexaBoard with SLOT_COUNT=4, CAP=2, TARGET=2.
- LoadSeed() producing the seed board from §3 verbatim.
- Move(srcSlot, dstSlot) enforcing the legality rules in §4 and returning the
  matching MoveResult for each failure (SameSlot, EmptySource, DestFull,
  ColorMismatch, OutOfRange, Ok). On a non-Ok result the board must not change.
- IsSolved implementing the predicate in §6.

Keep the board model UnityEngine-free so EditMode tests can exercise it. Prefer
small focused classes over one large manager.

Then write Assets/_Benchmark/Run/A-03-gameplay-notes.md listing:
- Script/class list and responsibilities.
- How each MoveResult case is detected and returned.
- Any unresolved TODOs.

Do not add the win-completion signal yet (that is A-04) and do not add polish.
```

### A-04 Win condition

- goal: Implement the single-fire completion signal per §5, evaluated against the
  solved-state predicate from §6.
- allowed_tools: Unity Open MCP file/script-editing and compile/status tools.
- expected_artifacts: updated scripts; `Assets/_Benchmark/Run/A-04-win-condition.md`.
- done_when (verify each with the named tool):
  1. `unity_open_mcp_compile_check` (fallback: `unity_open_mcp_read_compile_errors`)
     reports **zero** compile errors.
  2. The win-condition doc exists and states the exact solved-state predicate and
     how duplicate firing is prevented.
- timeout: 15m
- max_retries: 2
- prompt:

```text
Add the Hexa Sort completion logic per canonical-spec.md §5-§6.

Implement:
- SolvedFiredCount, starting at 0.
- After each successful Move, evaluate IsSolved. When a legal move first makes
  IsSolved true, increment SolvedFiredCount to 1 and emit a clear completion
  signal (a log line is sufficient). It must NOT fire on the seed, on a partial
  board, on an illegal move, or more than once.

Keep it deterministic and easy to verify in logs.

Write Assets/_Benchmark/Run/A-04-win-condition.md with:
- The exact solved-state predicate (copy from §6).
- Where the completion signal is emitted.
- How duplicate firing is prevented (the guard).

Do not add animation, particles, audio, or UI.
```

### A-05 Validation pass

- goal: Produce machine-checkable evidence that the implementation is correct
  against the canonical spec, including the reference solution from §7.
- allowed_tools: Unity Open MCP diagnostics/read tools; test-running tools
  (`unity_open_mcp_compile_check`, `unity_senses_read_console`,
  `unity_senses_run_tests` if EditMode tests were authored).
- expected_artifacts: `Assets/_Benchmark/Run/A-05-validation.md`;
  close `run-log.jsonl` with a run-end event.
- done_when (verify each with the named tool):
  1. `unity_open_mcp_compile_check` reports **zero** compile errors.
  2. `unity_open_mcp_scene_get_data` confirms the required root objects still
     exist (`BoardRoot`, `SlotsRoot`, `PiecesRoot`, `GameManager`).
  3. The reference solution from `canonical-spec.md` §7 replays to `IsSolved ==
     true` and `SolvedFiredCount == 1` (run as an EditMode test if one was
     written; otherwise document a deterministic replay and its observed result).
  4. The negative cases from §7 return the expected non-`Ok` `MoveResult`.
  5. `A-05-validation.md` records each check with PASS/FAIL + evidence.
- timeout: 10m
- max_retries: 1
- prompt:

```text
Run the final validation pass for the Hexa Sort implementation against the
canonical spec.

Check and record each as PASS/FAIL with short evidence:
1) Compile status — zero compile errors.
2) Required scene objects exist — BoardRoot, SlotsRoot, PiecesRoot, GameManager.
3) Reference solution — replay the four moves from canonical-spec.md §7 from
   LoadSeed() and record whether IsSolved becomes true and SolvedFiredCount
   equals 1. Prefer an EditMode test; if you run it, include the pass/fail.
4) Negative cases — confirm each case in §7 returns the expected MoveResult
   (SameSlot, EmptySource, ColorMismatch, DestFull).
5) Solved predicate on partial boards — confirm SolvedFiredCount stays 0 on the
   seed and after each non-final move.

Write Assets/_Benchmark/Run/A-05-validation.md in this format:
- Check name | Result (PASS/FAIL) | Evidence (short) | Follow-up action if FAIL.

Then append a run-end event to run-log.jsonl summarizing the run.
```

---

## Operator notes

- **Determinism first.** If the agent diverges from the prompt or invents rules
  not in `canonical-spec.md`, stop the step, retry once with stricter wording,
  and log the divergence under `prompt_ambiguity` (see [`triage.md`](triage.md)).
- **Score what's checkable.** Each `done_when` is meant to be verified with the
  named MCP tool — that evidence feeds the rubric. Do not substitute a human
  "looks right" judgement for a machine-checkable assertion.
- **Strict boundary.** This file defines benchmark work only. No feature creep.
- **Failures are signal, not noise.** A failed step with a clean taxonomy tag is
  valuable data; do not paper over it.
