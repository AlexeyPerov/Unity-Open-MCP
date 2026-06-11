# M2 Plan 2 — Meta-tools and gate wiring

**Spec:** [M2-bridge-mcp.md](./M2-bridge-mcp.md) §Meta-tools + gate wiring  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** [execution-plan-1-bridge-http.md](./execution-plan-1-bridge-http.md) complete

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: Implement M2 meta-tool handlers (M2-5) [Score:9] [Agent:heavy] — **DONE**

**Required context**

1. [mcp-tools.md](../../architecture/mcp-tools.md) §M2 tools
2. [bridge.md](../../packages/bridge.md) §Meta-tool dispatch
3. [M2-bridge-mcp.md](./M2-bridge-mcp.md)

- Implement `execute_csharp`, `invoke_method`, `execute_menu`, and `find_members` in bridge dispatch.
- Add stable mutation output/error fields per combined response envelope.
- Ensure `execute_csharp` uses Roslyn path expected by Unity 6 and documented defaults.

**Acceptance checklist**

- All four tools execute successfully in live Editor flow.
- Tool-specific validation errors are typed/actionable.
- Mutation envelope shape is consistent across all handlers.

Dependencies: Plan 1 complete.

---

#### Task 2: Strict `paths_hint` validation for mutating tools (M2-6) [Score:5] [Agent:easy] — **DONE**

**Required context**

1. [questions-2.md](../../questions/questions-2.md) Q1
2. [gate-policy.md](../../architecture/gate-policy.md)
3. [bridge.md](../../packages/bridge.md)

- Enforce non-empty `paths_hint` for mutating tools in M2.
- Return deterministic MCP-facing input error when missing/empty.
- Document behavior in bridge and tool docs.

**Acceptance checklist**

- Empty/missing `paths_hint` fails early with clear error guidance.
- No fallback whole-project scan path exists in M2.
- Docs explicitly state strict behavior.

Dependencies: Task 1.

---

#### Task 3: `execute_menu` allowlist and validate-skip rule (M2-7) [Score:7] [Agent:medium]

**Required context**

1. [questions-2.md](../../questions/questions-2.md) Q6
2. [bridge.md](../../packages/bridge.md)
3. [gate-policy.md](../../architecture/gate-policy.md)

- Add read-only menu allowlist.
- Implement rule: when menu is allowlisted and `paths_hint` empty, skip validate stage.
- Keep non-allowlisted menus on normal gate path.

**Acceptance checklist**

- Allowlisted read-only menus avoid unnecessary validate runs.
- Non-allowlisted menus still require normal gate flow.
- Allowlist is documented and testable.

Dependencies: Tasks 1–2.

---

#### Task 4: Minimal `packages/verify` skeleton via `VerifyGateAdapter` (M2-8) [Score:8] [Agent:heavy]

**Required context**

1. [questions-2.md](../../questions/questions-2.md) Q2 and Q11
2. [verify.md](../../packages/verify.md)
3. [gate-policy.md](../../architecture/gate-policy.md)

- Introduce minimal verify package skeleton for M2 with `missing_references` baseline/check path.
- Wire bridge gate path through `VerifyGateAdapter` instead of pure inline stub.
- Ensure enforce mode maps new gate errors to MCP `isError: true`.

**Acceptance checklist**

- Gate path computes real before/after `missing_references` deltas.
- Enforce mode failures propagate through bridge response and MCP `isError`.
- Scope stays intentionally minimal (no broad M3 rule expansion).

Dependencies: Tasks 1–3.

---

## Dependency graph

```text
Plan 1 → Task 1 → Task 2
Task 1 + Task 2 → Task 3
Tasks 1–3 → Task 4
```

## Plan 2 exit criteria

- [ ] All four M2 meta-tools are implemented and callable.
- [ ] Mutating tools enforce strict `paths_hint` behavior.
- [ ] `execute_menu` allowlist + skip-validate rule works as specified.
- [ ] Minimal verify skeleton is active through `VerifyGateAdapter`.

**Next:** [execution-plan-3-mcp-server-live.md](./execution-plan-3-mcp-server-live.md)
