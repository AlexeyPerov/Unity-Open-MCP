# M3 Plan 4 — MCP tools (gate + verify)

**Spec:** [M3-verify-gate.md](../M3-verify-gate.md) §MCP tools  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** [execution-plan-3-gate-policy.md](./execution-plan-3-gate-policy.md) interfaces stable

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: `unity_agent_validate_edit` (M3-16) [Score:6] [Agent:medium] ~~DONE~~

**Required context**

1. [mcp-tools.md](../../architecture/mcp-tools.md) §`unity_agent_validate_edit`
2. Plan 3 gate/adapter wiring
3. [mcp-server.md](../../packages/mcp-server.md)

- Bridge HTTP route + handler for scoped validate without mutation.
- MCP TS schema in `mcp-server/src/tools/`; register in tool index.
- Auto-select rule IDs from paths when `categories` omitted.

**Acceptance checklist**

- Tool returns `passed`, `issues`, `categoriesRun`, `durationMs` per schema.
- Unknown category returns error with `availableRules`.
- Callable from MCP client live flow.

Dependencies: Plan 3 complete.

---

#### Task 2: `unity_agent_checkpoint_create` and `unity_agent_delta` (M3-17) [Score:6] [Agent:medium] ~~DONE~~

**Required context**

1. [mcp-tools.md](../../architecture/mcp-tools.md) §checkpoint_create, §delta
2. Plan 3 Task 5 checkpoint store
3. [gate-policy.md](../../architecture/gate-policy.md) §Example C

- Implement manual checkpoint create and delta compare tools.
- Delta uses validate + key comparison against stored fingerprint.
- MCP schemas + bridge routes.

**Acceptance checklist**

- `checkpoint_create` returns `checkpointId` + fingerprint.
- `delta` returns summary counts, `newIssues`, `resolvedIssues`.
- Manual refactor workflow (checkpoint → mutate with gate off → delta) works.

Dependencies: Plan 3 Tasks 2, 5.

---

#### Task 3: `unity_agent_find_references` (M3-18) [Score:5] [Agent:medium] ~~DONE~~

**Required context**

1. [mcp-tools.md](../../architecture/mcp-tools.md) §`unity_agent_find_references`
2. [execution-plan-2-reference-graph.md](./execution-plan-2-reference-graph.md)
3. Plan 3 adapter wiring

- Bridge route calling `ReferenceGraph` / adapter.
- MCP TS schema; support `asset_path` or `guid` input.
- Respect `detail` and `max_results` where implemented.

**Acceptance checklist**

- Reverse deps returned for known fixture asset.
- On-demand graph build (no M7 cache).
- MCP live client can call tool successfully.

Dependencies: Plan 2 complete; Plan 3 stable.

---

#### Task 4: `unity_agent_scan_paths` (M3-19) [Score:5] [Agent:medium] ~~DONE~~

**Required context**

1. [mcp-tools.md](../../architecture/mcp-tools.md) §`unity_agent_scan_paths`
2. Plan 1 `VerifyRunner` Full mode

- Explicit rule ID scan over scoped paths (`categories: ["<ruleId>"]`).
- No separate `scan_category` tool (use categories array).

**Acceptance checklist**

- Both M3 rules callable via explicit categories.
- Full mode issues returned with timing.
- Unknown rule ID errors with `availableRules`.

Dependencies: Plan 1 complete; Plan 3 stable.

---

#### Task 5: `unity_agent_apply_fix` — minimal safe fix (M3-20) [Score:7] [Agent:medium]

**Required context**

1. [questions-3.md](../../questions/questions-3.md) Q1 (answer **A** — required in M3)
2. [mcp-tools.md](../../architecture/mcp-tools.md) §`unity_agent_apply_fix`
3. Unity-Scanner missing-reference fix patterns (if any) — port minimal path only

- Ship **at least one** safe fix for agent loop demo (e.g. remove missing script component on prefab).
- Support `dry_run: true` before apply.
- Broader per-rule fix providers remain backlog (M3+).

**Acceptance checklist**

- `apply_fix` callable from MCP with fix id from issue payload.
- Dry run reports planned change without mutation.
- E2E loop can use fix → re-gate → pass (Plan 6).

Dependencies: Plans 1–3; Tasks 1–4 for issue `fixId` wiring.

---

#### Task 6: MCP `isError` mapping regression (M3-21) [Score:4] [Agent:easy]

**Required context**

1. [mcp-server.md](../../packages/mcp-server.md)
2. [gate-policy.md](../../architecture/gate-policy.md)
3. M2 MCP gate mapping (regression baseline)

- Ensure new tools do not break M2 meta-tool gate envelope mapping.
- enforce failures → `isError: true`; `agentNextSteps` passed through.

**Acceptance checklist**

- M2 meta-tools still return combined mutation+gate envelope.
- New M3 tools return correct MCP error semantics.
- `npm run build` clean in mcp-server.

Dependencies: Tasks 1–5.

---

## Dependency graph

```text
Plan 3 complete → Tasks 1, 4 (parallel)
Plan 3 Task 5 → Task 2
Plan 2 complete → Task 3
Tasks 1–4 → Task 5
Tasks 1–5 → Task 6
```

## Plan 4 exit criteria

- [ ] M3 tools registered: `validate_edit`, `checkpoint_create`, `delta`, `find_references`, `scan_paths`
- [ ] `apply_fix` with at least one safe fix (Q1 A)
- [ ] MCP TS schemas + bridge routes for all above
- [ ] M2 gate `isError` mapping unchanged

**Next:** [execution-plan-5-agent-skill.md](./execution-plan-5-agent-skill.md)
