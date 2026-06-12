# M3 Plan 6 — Demo fixtures, tests, and E2E validation

**Spec:** [M3-verify-gate.md](../M3-verify-gate.md) §E2E  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** Plans 1–5 integration-ready

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: Demo fixture prefabs for gate scenarios (M3-24) [Score:5] [Agent:easy] [DONE]

**Required context**

1. [questions-3.md](../../questions/questions-3.md) Q10 suggested doc changes (demo fixtures)
2. [M2 manual checklist](../M2/m2-manual-e2e-checklist.md) gate steps
3. `demo/` project layout

- Add fixture prefab(s) for broken/missing script and restorable reference scenarios.
- Document paths in `demo/README.md` for E2E and EditMode tests.
- Ensure `file:` bridge + verify wired in demo manifest.

**Acceptance checklist**

- Controlled break/fix cycle possible without ad-hoc asset creation.
- Fixture paths referenced in tests and manual checklist.
- Demo opens in target Unity version with M3 packages.

Dependencies: Plan 1 rule behavior stable.

---

#### Task 2: Consolidate EditMode test coverage (M3-25) [Score:6] [Agent:medium] [DONE]

**Required context**

1. [questions-3.md](../../questions/questions-3.md) Q10 (answer **C**)
2. Plan 1 Task 5 and Plan 2 Task 3 tests
3. Task 1 demo fixtures

- Ensure `packages/verify/Tests~/` covers both M3 rules on fixtures.
- Add bridge gate integration tests if missing: delta detects new `missing_references` after bad edit.
- All EditMode suites pass in Unity Test Runner.

**Acceptance checklist**

- Both rules tested on fixture assets.
- Gate delta test proves new error detection post bad prefab edit.
- No flaky domain-reload dependencies without documentation.

Dependencies: Plans 1–3; Task 1 fixtures.

---

#### Task 3: Manual MCP E2E checklist (M3-26) [Score:6] [Agent:medium] [DONE]

**Required context**

1. [m3-manual-e2e-checklist.md](./m3-manual-e2e-checklist.md)
2. [M2 manual checklist](../M2/m2-manual-e2e-checklist.md) gate steps (reuse)
3. Plan 4 MCP tools

- Author M3 manual E2E checklist: break prefab → gate fails (`isError: true`) + `agentNextSteps` → fix (`apply_fix` or mutation) → pass.
- Include `validate_edit`, `find_references`, `checkpoint_create` + `delta` smoke steps.
- Record expected response fields for quick verification.

**Acceptance checklist**

- Manual flow reproducible with explicit commands/prompts.
- Gate failure/success transitions demonstrated end-to-end.
- Checklist references demo fixture paths.

Dependencies: Plans 1–5; Task 1.

---

#### Task 4: Doc updates — bridge.md and EXTRACTION.md audit (M3-27) [Score:4] [Agent:easy] [DONE]

**Required context**

1. [questions-3.md](../../questions/questions-3.md) suggested doc changes table
2. [bridge.md](../../packages/bridge.md)
3. `packages/verify/EXTRACTION.md`

- Update bridge.md: UPM verify dependency; remove M2 inline stub section after cutover (Q2).
- Confirm EXTRACTION.md has final commit hash + complete ported file list.
- Cross-link execution plans from M3-verify-gate if needed.

**Acceptance checklist**

- bridge.md matches big-bang gate swap (no stub dual path).
- EXTRACTION.md complete for all ported Unity-Scanner files.
- Pending doc rows from questions-3 table resolved or explicitly deferred.

Dependencies: Plans 1–3.

---

#### Task 5: Deferrals audit and backlog sync (M3-28) [Score:4] [Agent:easy] [DONE]

**Required context**

1. [questions-3.md](../../questions/questions-3.md)
2. [packages/backlog.md](../../packages/backlog.md) §M3 deferrals
3. [M3-verify-gate.md](../M3-verify-gate.md) §Out of scope

- Verify all postponed M3 scope is in packages backlog only.
- Ensure M3 docs do not reintroduce deferred items as required scope.
- Remove pulled-in items from backlog if any were implemented.

**Acceptance checklist**

- Backlog entries exist for each deferred M3 decision.
- M3 execution plans remain internally consistent with questions-3 answers.
- No hidden M4/M5/M7 scope mislabeled as M3 required.

Dependencies: Task 3.

---

## Dependency graph

```text
Plan 1 stable → Task 1
Tasks 1, Plans 1–3 → Task 2
Plans 1–5, Task 1 → Task 3
Plans 1–3 → Task 4
Task 3 → Task 5
```

## Plan 6 exit criteria

- [x] Demo fixtures for broken/missing script scenarios
- [x] EditMode tests pass for both M3 rules + gate delta integration
- [x] Manual E2E checklist complete and exercised
- [x] bridge.md + EXTRACTION.md updated
- [x] M3 deferrals audited in packages backlog

**Plan 6 complete.**
