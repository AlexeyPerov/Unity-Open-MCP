# M2 Plan 1 — Bridge runtime and HTTP contract

**Spec:** [M2-bridge-mcp.md](./M2-bridge-mcp.md) §Bridge runtime + HTTP contract  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** [questions/questions-2.md](../../questions/questions-2.md) resolved

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: Bridge package scaffold + listener lifecycle (M2-1) [Score:7] [Agent:medium] **DONE**

**Required context**

1. [bridge.md](../../packages/bridge.md) — responsibilities and package layout
2. [M2-bridge-mcp.md](./M2-bridge-mcp.md) — Bridge runtime section
3. [monorepo-layout.md](../../architecture/monorepo-layout.md)

- Scaffold `packages/bridge` package structure for bridge runtime paths (`Editor/Bridge`, `Editor/MetaTools`, `Editor/Gate`).
- Implement bridge lifecycle entry points (startup/shutdown) and listener bind to localhost default port `19120`.
- Add configurable port behavior (`UNITY_AGENT_BRIDGE_PORT`) for local overrides.

**Acceptance checklist**

- Bridge listener starts and stops reliably in the Editor domain lifecycle.
- Listener binds to `127.0.0.1` only.
- Port override works and is reflected by `/ping`.

Dependencies: none.

---

#### Task 2: Main-thread dispatch queue with timeout envelope (M2-2) [Score:8] [Agent:heavy] **DONE**

**Required context**

1. [bridge.md](../../packages/bridge.md) §Main-thread dispatch
2. [mcp-tools.md](../../architecture/mcp-tools.md) §Combined response shape
3. [gate-policy.md](../../architecture/gate-policy.md)

- Implement internal queue so all Unity API/mutation work executes on main thread.
- HTTP handlers enqueue tool operations and await completion with `timeout_ms`.
- Return stable mutation error envelopes on timeout/cancellation/faults.

**Acceptance checklist**

- Background HTTP thread never directly touches Unity APIs.
- Timeout paths return actionable mutation errors (no hangs).
- Success and failure responses preserve stable response shape.

Dependencies: Task 1.

---

#### Task 3: `/ping` endpoint contract + bridge session state (M2-3) [Score:6] [Agent:medium]

**Required context**

1. [mcp-tools.md](../../architecture/mcp-tools.md) §`unity_agent_ping`
2. [mcp-server.md](../../packages/mcp-server.md) §Routing / compile state usage
3. [bridge.md](../../packages/bridge.md) §HTTP API

- Implement `/ping` GET response with connected/project/version/mode/compile/play fields.
- Add session state holder for compile/play/editor metadata used by ping and tool responses.
- Ensure ping remains cheap and safe during editor compile transitions.

**Acceptance checklist**

- Ping returns required schema fields expected by MCP.
- `compiling` and `isPlaying` report real-time state.
- Ping is stable during domain reload edge cases.

Dependencies: Tasks 1–2.

---

#### Task 4: Formal bridge HTTP API doc (`architecture/bridge-http-api.md`) (M2-4) [Score:5] [Agent:easy]

**Required context**

1. [questions-2.md](../../questions/questions-2.md) Q5 decision
2. [bridge.md](../../packages/bridge.md)
3. [mcp-tools.md](../../architecture/mcp-tools.md) M2 schemas

- Author dedicated API doc for `/ping` and `/tools/{tool_name}` contracts.
- Define request/response examples, status/error semantics, timeout behavior, and envelope mapping.
- Link doc from bridge and mcp-server specs.

**Acceptance checklist**

- Contract doc is specific enough for MCP live-client implementation without ambiguity.
- Examples align with tool schemas and mutating envelope format.
- Cross-links updated in related specs.

Dependencies: Tasks 2–3.

---

## Dependency graph

```text
Task 1 → Task 2 → Task 3
Task 3 → Task 4
```

## Plan 1 exit criteria

- [ ] Bridge listener lifecycle is stable and localhost-only.
- [ ] Main-thread dispatch queue and timeout handling are implemented.
- [ ] `/ping` endpoint returns the agreed schema.
- [ ] `architecture/bridge-http-api.md` exists and is linked.

**Next:** [execution-plan-2-meta-tools-gate.md](./execution-plan-2-meta-tools-gate.md)
