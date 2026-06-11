# M2 Plan 3 — MCP server live routing

**Spec:** [M2-bridge-mcp.md](./M2-bridge-mcp.md) §MCP server (live mode)  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** [execution-plan-1-bridge-http.md](./execution-plan-1-bridge-http.md) complete; Plan 2 interfaces stable

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: `unity-agent-mcp` stdio scaffold + tool registration (M2-9) [Score:7] [Agent:medium] — **DONE**

**Required context**

1. [mcp-server.md](../../packages/mcp-server.md)
2. [mcp-tools.md](../../architecture/mcp-tools.md) §M2
3. [M2-bridge-mcp.md](./M2-bridge-mcp.md)

- Implement stdio MCP server entry and register M2 tool schemas.
- Use TypeScript + `@modelcontextprotocol/sdk`, ESM, Node 18+.
- Keep routing layer split for future batch mode (M5).

**Acceptance checklist**

- MCP server boots in stdio and advertises M2 tools.
- Tool input schemas match spec docs.
- Code layout supports separate live-client and future batch routes.

Dependencies: Plan 1 complete.

---

#### Task 2: Live HTTP client and routing policy (M2-10) [Score:8] [Agent:heavy] — **DONE**

**Required context**

1. [bridge-http-api.md](../../architecture/bridge-http-api.md)
2. [mcp-server.md](../../packages/mcp-server.md) §Routing
3. [mcp-tools.md](../../architecture/mcp-tools.md)

- Add live bridge HTTP client for `/ping` and `/tools/{tool}` calls.
- Route M2 tools live-only, with clear bridge-offline failures.
- Implement compile-wait/retry behavior for `ping.compiling === true`.

**Acceptance checklist**

- Requests are correctly serialized/deserialized against bridge contract.
- Compile transition retry does not create infinite wait loops.
- Offline bridge returns actionable MCP errors.

Dependencies: Task 1.

---

#### Task 3: `isError` mapping and envelope pass-through (M2-11) [Score:6] [Agent:medium] — **DONE**

**Required context**

1. [mcp-server.md](../../packages/mcp-server.md) §`isError` mapping
2. [gate-policy.md](../../architecture/gate-policy.md)
3. [questions-2.md](../../questions/questions-2.md) Q11

- Implement MCP `isError` derivation from mutation/gate results.
- Preserve full bridge payload for agent diagnostics (`agentNextSteps`, deltas, errors).
- Ensure warn/off gate modes behave per policy.

**Acceptance checklist**

- Mutation failures set `isError: true`.
- Enforce mode new gate errors set `isError: true`; warn mode does not.
- Returned payload remains complete and stable for agent loops.

Dependencies: Tasks 1–2; Plan 2 Task 4.

---

## Dependency graph

```text
Task 1 → Task 2 → Task 3
Plan 2 Task 4 → Task 3
```

## Plan 3 exit criteria

- [ ] MCP server live mode works end-to-end against bridge HTTP.
- [ ] M2 tools are registered with correct schemas.
- [ ] `isError` mapping matches GatePolicy semantics.
- [ ] Compile-wait/retry behavior is documented and implemented.

**Next:** [execution-plan-4-demo-validation.md](./execution-plan-4-demo-validation.md)
