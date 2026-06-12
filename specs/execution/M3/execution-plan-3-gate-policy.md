# M3 Plan 3 — Gate swap and GatePolicy

**Spec:** [M3-verify-gate.md](../M3-verify-gate.md) §Gate swap  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** [execution-plan-1-verify-rules.md](./execution-plan-1-verify-rules.md) complete

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: Bridge hard dependency on verify package (M3-9) [Score:4] [Agent:easy] ~~DONE~~

**Required context**

1. [questions-3.md](../../questions/questions-3.md) Q8
2. [bridge.md](../../packages/bridge.md)
3. `packages/bridge/package.json`

- Add UPM dependency on `com.alexeyperov.unity-agent-verify` in bridge `package.json`.
- Ensure demo `manifest.json` already wires `file:` verify (align if needed).
- No soft-degraded gate path when verify missing (Q8 A).

**Acceptance checklist**

- Bridge package.json lists verify dependency.
- Project fails fast at compile if verify not installed (expected for M3+).
- [bridge.md](../../packages/bridge.md) documents hard dependency.

Dependencies: Plan 1 complete.

---

#### Task 2: Expand `VerifyGateAdapter` — checkpoint, validate, delta (M3-10) [Score:8] [Agent:heavy] ~~DONE~~

**Required context**

1. [verify.md](../../packages/verify.md) §Bridge adapter
2. [gate-policy.md](../../architecture/gate-policy.md) §Gate flow
3. `packages/bridge/Editor/Gate/VerifyGateAdapter.cs`

- Implement `CreateCheckpoint`, `ValidatePaths`, `ComputeDelta` per verify.md contract.
- Checkpoint uses `VerifyRunMode.Checkpoint`; validate uses full issues for delta.
- Wire path-mapping auto-select for rule IDs from gate-policy table.

**Acceptance checklist**

- Adapter is thin facade — no gate policy logic inside verify.
- Delta compares issue keys between checkpoint fingerprint and post-mutation validate.
- Both M3 rules participate in default gate path for prefab/scene `paths_hint`.

Dependencies: Plan 1 complete.

---

#### Task 3: Full `GatePolicy` state machine in bridge (M3-11) [Score:8] [Agent:heavy] ~~DONE~~

**Required context**

1. [gate-policy.md](../../architecture/gate-policy.md)
2. [questions-3.md](../../questions/questions-3.md) Q2
3. `packages/bridge/Editor/Gate/GatePolicy.cs` (or equivalent)

- Replace M2 inline/minimal gate stub with full enforce/warn/off flow through adapter.
- **Big-bang cutover** — no `USE_VERIFY_GATE` feature flag (Q2 A).
- Remove or delete M2 stub path after cutover; update bridge.md.

**Acceptance checklist**

- enforce: checkpoint → mutate → validate → delta; MCP `isError` on new errors.
- warn/off modes behave per gate-policy.
- No dual-path maintenance between stub and adapter.

Dependencies: Task 2.

---

#### Task 4: `agentNextSteps` generation in bridge (M3-12) [Score:6] [Agent:medium] ~~DONE~~

**Required context**

1. [questions-3.md](../../questions/questions-3.md) Q4
2. [gate-policy.md](../../architecture/gate-policy.md) §agentNextSteps

- Bridge `GatePolicy` generates heuristic `agentNextSteps` from issue context on gate failure.
- MCP passes through unchanged; MCP sets `isError` only from gate envelope.

**Acceptance checklist**

- Failed enforce gate includes non-empty `agentNextSteps` for typical `missing_references` delta.
- Hints reference actionable tools (`find_references`, `validate_edit`, `apply_fix` when applicable).
- MCP server does not post-process or duplicate hint generation.

Dependencies: Task 3.

---

#### Task 5: Checkpoint store — ring buffer + optional disk persistence (M3-13) [Score:6] [Agent:medium]

**Required context**

1. [questions-3.md](../../questions/questions-3.md) Q5
2. [gate-policy.md](../../architecture/gate-policy.md) §Checkpoint persistence

- In-memory ring buffer (default N = 20) keyed by `checkpointId`.
- Optional write to `{project}/.unity-agent/checkpoints/{checkpointId}.json` — **off by default**, gitignored.
- Support manual `checkpoint_create` / `delta` tool backing.

**Acceptance checklist**

- Gate auto-checkpoints use ring buffer only by default.
- Disk persistence can be enabled via bridge setting or env (document path).
- Checkpoint JSON shape matches gate-policy sketch.

Dependencies: Task 2.

---

#### Task 6: Strict issue key validation on delta (M3-14) [Score:5] [Agent:medium]

**Required context**

1. [questions-3.md](../../questions/questions-3.md) Q11
2. [verify.md](../../packages/verify.md) §Issue keys

- Reject or fail delta when issues lack `{ruleId}|{severity}|{assetPath}|{issueCode}` format (Q11 A).
- Fail fast in dev/editor builds; log clearly for malformed keys.

**Acceptance checklist**

- Malformed keys do not silently enter delta sets.
- Both ported rules emit keys that pass validation.

Dependencies: Tasks 2–3.

---

#### Task 7: Checkpoint perf budget logging (M3-15) [Score:4] [Agent:easy]

**Required context**

1. [questions-3.md](../../questions/questions-3.md) Q12
2. Plan 1 Task 4 perf logging in verify

- Ensure gate checkpoint path logs duration and warns above **2000 ms** scoped budget.
- Document budget in bridge or verify docs if not already.

**Acceptance checklist**

- Warning appears in Unity console when budget exceeded on typical prefab scope.
- No hard abort in M3 (warn only per Q12 B).

Dependencies: Tasks 2–3.

---

## Dependency graph

```text
Plan 1 complete → Task 1
Plan 1 complete → Task 2 → Task 3 → Tasks 4, 6, 7
Task 2 → Task 5
```

## Plan 3 exit criteria

- [ ] Bridge depends on verify UPM package (hard dependency)
- [ ] Full GatePolicy through `VerifyGateAdapter`; M2 stub removed
- [ ] `agentNextSteps` generated in bridge on gate failure
- [ ] Checkpoint ring buffer + optional disk persistence (off by default)
- [ ] Strict issue key validation on delta
- [ ] 2000 ms checkpoint perf warning

**Next:** [execution-plan-4-mcp-tools.md](./execution-plan-4-mcp-tools.md)
