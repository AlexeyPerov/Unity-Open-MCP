# M3 Execution Plan — Verify Gate (index)

**Spec:** [M3-verify-gate.md](../M3-verify-gate.md)  
**Parent:** [idea.md](../../idea.md)  
**Prerequisite:** [questions/questions-3.md](../../questions/questions-3.md) resolved (2026-06-13); M2.5 complete (Q0).

How to use this plan: each sub-plan breaks M3 into agent-sized tasks with **Required context**, acceptance checklists, and dependency links. Cross-cutting **Confidence and Risks** below applies to every task.

## Sub-plans

| Plan | Scope | Start when |
|---|---|---|
| [execution-plan-1-verify-rules.md](./execution-plan-1-verify-rules.md) | Unity-Scanner extraction, `missing_references` + `scene_prefab_health`, `VerifyRunner` | M3 start |
| [execution-plan-2-reference-graph.md](./execution-plan-2-reference-graph.md) | `ReferenceGraph` port for `find_references` | Plan 1 Task 1 (EXTRACTION baseline) complete |
| [execution-plan-3-gate-policy.md](./execution-plan-3-gate-policy.md) | Full `GatePolicy`, `VerifyGateAdapter`, checkpoints, `agentNextSteps` | Plan 1 complete |
| [execution-plan-4-mcp-tools.md](./execution-plan-4-mcp-tools.md) | M3 MCP tools + `apply_fix` (minimal safe fix) | Plan 3 interfaces stable |
| [execution-plan-5-agent-skill.md](./execution-plan-5-agent-skill.md) | `skills/unity-agent/SKILL.md` from agent-skill spec | Plan 4 tool names stable |
| [execution-plan-6-validation.md](./execution-plan-6-validation.md) | Demo fixtures, EditMode tests, manual E2E, backlog audit | Plans 1–5 integration stage |

## Resolved decisions (2026-06-13)

All answers recorded in [questions-3.md](../../questions/questions-3.md):

| Q | Decision |
|---|---|
| Q0 | M2.5 complete before M3 |
| Q1 | **`unity_agent_apply_fix` required** — at least one safe fix (e.g. remove missing script) for agent loop demo |
| Q2 | **Big-bang** swap to `VerifyGateAdapter`; no feature flag; remove M2 inline stub |
| Q3 | Copy from local Unity-Scanner at `/Users/alexeyperov/Projects/Unity-Scanner`; pin commit in `EXTRACTION.md` |
| Q4 | Bridge `GatePolicy` generates `agentNextSteps`; MCP sets `isError` only |
| Q5 | Checkpoint ring buffer + **optional** disk persistence under `.unity-agent/checkpoints/` (off by default) |
| Q6 | Full `scene_prefab_health` logic scoped to `paths_hint`; Checkpoint mode counts+keys only |
| Q7 | `find_references` builds graph **on demand** per request (M3) |
| Q8 | Hard UPM dependency: bridge `package.json` depends on verify |
| Q9 | Generate `skills/unity-agent/SKILL.md` from `agent-skill.md` (wizard install in M4) |
| Q10 | **Both** EditMode tests + manual E2E required for done |
| Q11 | **Strict** issue key format validation on delta |
| Q12 | Checkpoint perf budget **2000 ms** (log warning if exceeded) |

Deferred scope is tracked in [packages/backlog.md](../../packages/backlog.md) §M3 deferrals — not in active sub-plans.

## Assumptions

- M3 is **live Editor first**; batch transport for new tools is optional where mcp-tools marks "both".
- Unity-Scanner algorithms are **read and copied** from `/Users/alexeyperov/Projects/Unity-Scanner` — not reimplemented from scratch except pieces listed under Rewrite in [verify.md](../../packages/verify.md).
- GatePolicy stays in **bridge**; verify package remains neutral check logic.
- Only **two** verify rules ship in M3: `missing_references`, `scene_prefab_health`.
- Mutating tools still require non-empty `paths_hint` (M2 behavior unchanged).
- Demo uses `file:` references for bridge + verify (no feature-flag dual gate path).

## Confidence and Risks

Confidence: Medium.

Resolved constraints:

1. M3 question decisions are recorded in [questions-3.md](../../questions/questions-3.md).
2. Extraction manifest and adapter contract are defined in [verify.md](../../packages/verify.md).
3. Tool schemas and gate envelope are defined in [mcp-tools.md](../../architecture/mcp-tools.md) §M3 and [gate-policy.md](../../architecture/gate-policy.md).
4. M2 verify skeleton and `VerifyGateAdapter` stub provide a cutover anchor (Plan 3 big-bang swap).

Residual uncertainties:

1. Unity-Scanner port fidelity — scoped `VerifyScope` + `VerifyRunMode` may diverge from Scanner's project-wide loops; EditMode fixture tests are critical.
2. `scene_prefab_health` scoped gate cost — full rule logic on `paths_hint` may approach the 2000 ms budget on large prefab trees (Q12).
3. `ReferenceGraph` on-demand build may be slow on first call for large projects; M6 cache is backlog-tracked (Q7).
4. `apply_fix` safe-fix scope — one fix path is required (Q1 A); broader fix-provider porting is explicitly deferred.

## Agent Level Legend

- `easy`: straightforward implementation, clear requirements.
- `medium`: moderate complexity, some design decisions needed.
- `heavy`: complex logic, strong reasoning and long-context required.

## Changelog Instructions

- When a task is completed, mark it as DONE (append `[DONE]` to its title) in the sub-plan file.
- Add changes to the top of `specs/changelog.md`.
- Include date/time in each changelog title entry.

## Milestone dependency graph

```text
Plan 1 (verify rules + EXTRACTION.md)
  → Plan 2 (ReferenceGraph) — can start after Plan 1 Task 1
  → Plan 3 (gate swap + GatePolicy)
Plan 3
  → Plan 4 (MCP tools)
Plan 4
  → Plan 5 (agent skill)
Plans 1–5
  → Plan 6 (demo fixtures + tests + E2E + backlog audit)
```

## Mapping to M3-verify-gate task order

| M3-verify-gate §Task order | Execution plan |
|---|---|
| Verify rules (`missing_references`, `scene_prefab_health`) | Plan 1 |
| `ReferenceGraph` | Plan 2 |
| Gate swap (`VerifyGateAdapter`, `GatePolicy`, checkpoints) | Plan 3 |
| M3 MCP tools | Plan 4 |
| Agent skill | Plan 5 |
| E2E + EditMode tests | Plan 6 |

## M3 exit criteria

Per [M3-verify-gate.md](../M3-verify-gate.md) and [architecture/mcp-tools.md](../../architecture/mcp-tools.md) §M3:

- [x] Gate delta detects new `missing_references` errors after bad prefab edit
- [x] `agentNextSteps` populated on gate failure (bridge-generated)
- [x] Agent skill documents mutate → gate → fix loop
- [x] EditMode tests pass for both M3 rules on fixture assets
- [x] Manual E2E: break prefab → gate fails → fix → pass
- [x] `packages/verify/EXTRACTION.md` records Unity-Scanner commit hash + ported file list
- [x] Deferred M3 decisions tracked in [packages/backlog.md](../../packages/backlog.md)

**M3 complete.**

**Next milestone:** [M4-hub-wizard.md](../M4-hub-wizard.md)
