# M3 Plan 2 — ReferenceGraph for find_references

**Spec:** [M3-verify-gate.md](../M3-verify-gate.md) §ReferenceGraph  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** [execution-plan-1-verify-rules.md](./execution-plan-1-verify-rules.md) Task 1 complete

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: Port `ReferenceGraph` from Unity-Scanner (M3-6) [Score:8] [Agent:heavy] — **DONE**

**Required context**

1. Unity-Scanner `FindReferencesWindow.RefsMapBuilder` (or equivalent under FindReferences)
2. [verify.md](../../packages/verify.md) §Extraction manifest
3. [questions-3.md](../../questions/questions-3.md) Q7

- Copy and adapt graph builder to `packages/verify/Editor/References/ReferenceGraph.cs`.
- Build graph **on demand per request** (Q7 A) — no incremental index or disk cache in M3.
- Exclude EditorWindow shell, prefs UI, and context menus.
- Update `EXTRACTION.md` with ported files + commit hash if new batch.

**Acceptance checklist**

- Headless reverse-dependency lookup by asset path or GUID.
- No UI dependencies; compiles in verify package only.
- Graph build is synchronous per call (optimize in M7 if needed — backlog).

Dependencies: Plan 1 Task 1.

---

#### Task 2: Wire `VerifyGateAdapter.FindReferences` (M3-7) [Score:5] [Agent:easy] — **DONE**

**Required context**

1. [verify.md](../../packages/verify.md) §Bridge adapter
2. `packages/bridge/Editor/Gate/VerifyGateAdapter.cs`
3. Plan 2 Task 1

- Add thin facade method `FindReferences(assetPathOrGuid)` on adapter (or verify static API).
- Return shape suitable for bridge HTTP and MCP `unity_agent_find_references`.

**Acceptance checklist**

- Bridge can call verify for reverse refs without gate logic in verify.
- Response includes asset paths / GUIDs per mcp-tools schema expectations.

Dependencies: Task 1.

---

#### Task 3: ReferenceGraph EditMode tests (M3-8) [Score:5] [Agent:medium]

**Required context**

1. Plan 2 Tasks 1–2
2. Demo or test fixtures with known reference chains

- Test reverse lookup: script → prefab reference, material → mesh, etc.
- Test `max_results` truncation behavior if implemented at verify layer.

**Acceptance checklist**

- At least one fixture proves reverse dependency resolution.
- Tests pass in Unity Test Runner.

Dependencies: Tasks 1–2.

---

## Dependency graph

```text
Plan 1 Task 1 → Task 1 → Task 2 → Task 3
```

## Plan 2 exit criteria

- [ ] `ReferenceGraph` ported headlessly from Unity-Scanner
- [ ] `VerifyGateAdapter.FindReferences` (or equivalent) callable from bridge
- [ ] EditMode tests cover reverse dependency lookup

**Next:** [execution-plan-4-mcp-tools.md](./execution-plan-4-mcp-tools.md) (find_references MCP wiring)
