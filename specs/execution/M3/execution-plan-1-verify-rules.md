# M3 Plan 1 — Verify rules and Unity-Scanner extraction

**Spec:** [M3-verify-gate.md](../M3-verify-gate.md) §Verify rules  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** M2.5 complete; [questions-3.md](../../questions/questions-3.md) resolved

How to use this plan: each task lists **Required context** — read only those docs for that task. **Read Unity-Scanner source first** at `/Users/alexeyperov/Projects/Unity-Scanner` before editing verify.

## Task Breakdown

#### Task 1: EXTRACTION.md baseline and shared Internals (M3-1) [Score:6] [Agent:medium] **DONE**

**Required context**

1. [questions-3.md](../../questions/questions-3.md) Q3
2. [verify.md](../../packages/verify.md) §Extraction manifest
3. [M3-verify-gate.md](../M3-verify-gate.md) §Unity-Scanner source

- Open `/Users/alexeyperov/Projects/Unity-Scanner` and record current **commit hash** in `packages/verify/EXTRACTION.md`.
- Port shared utilities referenced by M3 scanners (`Internals/` — Serialization, RegexPatterns, etc. per manifest).
- Apply adapt-on-copy rules: namespace `UnityAgentVerify.*`, header `// Extracted from Unity-Scanner: <path>`.
- Do **not** import orchestrator, UI, MCP, batch, or cache layers.

**Acceptance checklist**

- `EXTRACTION.md` exists with commit hash and initial file list.
- Internals compile; no Unity-Scanner UI or window dependencies.
- Only files required by M3 rule ports are included (minimal Internals).

Dependencies: M3 start.

---

#### Task 2: Port `missing_references` from Unity-Scanner (M3-2) [Score:9] [Agent:heavy] **DONE**

**Required context**

1. Unity-Scanner `Editor/Categories/MissingReferences/` (scanner, mapper, models)
2. [verify.md](../../packages/verify.md) §Extraction manifest
3. Existing `packages/verify/Editor/Rules/MissingReferences/` (replace/adapt greenfield stub)

- Copy and adapt `MissingReferencesScanner`, `IssueMapper`, `ResultModels` per manifest.
- Implement `IVerifyRule` with id `missing_references`.
- Support `VerifyRunMode.Checkpoint` (counts + issue keys only) and `Validate`/`Full` (full issues).
- Preserve issue keys: `{ruleId}|{severity}|{assetPath}|{issueCode}`.

**Acceptance checklist**

- Rule scans scoped `VerifyScope.Paths` (no whole-project fallback in gate paths).
- Checkpoint mode emits counts + keys without full issue payloads.
- Issue codes align with Unity-Scanner category IDs for gate delta stability.
- Greenfield stub replaced or clearly superseded.

Dependencies: Task 1.

---

#### Task 3: Port `scene_prefab_health` from Unity-Scanner (M3-3) [Score:9] [Agent:heavy] **DONE**

**Required context**

1. Unity-Scanner `Editor/Categories/ScenePrefabHealth/` (scanner, mapper)
2. [questions-3.md](../../questions/questions-3.md) Q6
3. [gate-policy.md](../../architecture/gate-policy.md) path-mapping table

- Copy and adapt `ScenePrefabHealthScanner` + `IssueMapper` per manifest.
- Implement `IVerifyRule` with id `scene_prefab_health`.
- Full rule logic scoped to `paths_hint` only (Q6 A); Checkpoint mode counts+keys only.
- Exclude tab drawers and UI shell.

**Acceptance checklist**

- Rule registered and callable via `VerifyRunner` with scoped paths.
- Checkpoint mode stays lightweight (no full issue list).
- Path-mapping auto-select includes this rule for prefab/scene paths.

Dependencies: Task 1.

---

#### Task 4: `VerifyRunner`, rule registry, and core types (M3-4) [Score:7] [Agent:medium]

**Required context**

1. [verify.md](../../packages/verify.md) §Verify runner, §Issue keys
2. [gate-policy.md](../../architecture/gate-policy.md)
3. Plan 1 Tasks 2–3 rule implementations

- Ensure `VerifyRunner.RunScoped(scope, ruleIds, mode)` dispatches to registered rules.
- Implement `IssueKey.cs`, `VerifyIssue`, `VerifyResult`, `VerifyScope` if not complete.
- Unknown `ruleIds` return error with `availableRules` list (mcp-tools §M3).
- Log duration; warn when scoped Checkpoint exceeds **2000 ms** (Q12).

**Acceptance checklist**

- Both M3 rules run through `VerifyRunner` in Checkpoint, Validate, and Full modes.
- Issue key format is strict and stable for delta.
- Perf warning logged when Checkpoint budget exceeded.

Dependencies: Tasks 2–3.

---

#### Task 5: EditMode tests for M3 rules (M3-5) [Score:7] [Agent:medium]

**Required context**

1. [questions-3.md](../../questions/questions-3.md) Q10
2. Plan 1 Tasks 2–4
3. `packages/verify/Tests~/` (create if missing)

- Add EditMode tests for `missing_references` and `scene_prefab_health` on fixture assets.
- Cover Checkpoint mode (counts+keys) vs Validate mode (full issues).
- Assert issue key format and expected severities on known broken fixtures.

**Acceptance checklist**

- Tests pass in Unity Test Runner for both rules.
- Fixture assets are referenced from demo or `Tests~/Fixtures/`.
- Regression guard for Unity-Scanner port behavior.

Dependencies: Tasks 2–4. Demo fixtures (Plan 6 Task 1) can land in parallel; tests may use inline fixtures until then.

---

## Dependency graph

```text
Task 1 → Tasks 2, 3 (parallel after Task 1)
Tasks 2, 3 → Task 4
Task 4 → Task 5
```

## Plan 1 exit criteria

- [ ] `EXTRACTION.md` has commit hash + ported file list
- [ ] `missing_references` and `scene_prefab_health` ported from Unity-Scanner
- [ ] `VerifyRunner` dispatches both rules with Checkpoint/Validate/Full modes
- [ ] EditMode tests cover both rules on fixtures

**Next:** [execution-plan-2-reference-graph.md](./execution-plan-2-reference-graph.md) (parallel-friendly after Task 1) and [execution-plan-3-gate-policy.md](./execution-plan-3-gate-policy.md) (after Plan 1 complete)
