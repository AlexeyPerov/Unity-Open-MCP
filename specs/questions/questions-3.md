# M3 — Verify Gate

Resolve these before expanding `execution/M3-verify-gate.md` and porting Unity-Scanner algorithms.

**Status:** **resolved** (2026-06-13 — all recommended answers; Q3 uses local Unity-Scanner checkout)

**Spec links:** [packages/verify.md](../packages/verify.md), [architecture/gate-policy.md](../architecture/gate-policy.md), [architecture/mcp-tools.md](../architecture/mcp-tools.md) §M3, [agents/agent-skill.md](../agents/agent-skill.md), [execution/M3-verify-gate.md](../execution/M3-verify-gate.md)

**Unity-Scanner source (upstream algorithms):** `/Users/alexeyperov/Projects/Unity-Scanner` — same maintainer; agents **read and copy** port files directly from this checkout during M3 implementation (see [verify.md](../packages/verify.md) extraction manifest and [M3-verify-gate.md](../execution/M3-verify-gate.md) §Unity-Scanner source).

## Questions

### 0. Start M3 now, or finish M2.5 first?

- **A)** Finish M2.5 exit criteria + commit attribute registration work first.
- **B)** Start M3 in parallel (verify/gate work is mostly separate packages).
- **C)** Skip M2.5 entirely and go straight to M3.

**Recommended: A** — M2.5 attribute dispatch is foundation for future typed verify tools; avoid overlapping bridge churn.

**Answer: A** — M2.5 complete (2026-06-13); proceed to M3.

### 1. Is `unity_agent_apply_fix` in M3 scope?

- **A)** **M3 required** — at least one safe fix (e.g. remove missing script) for agent loop demo.
- **B)** **M3 optional** — ship if cheap; USP is gate delta, not fixes.
- **C)** **Defer to M3+** — validate + find_references only.

**Recommended: B** — mcp-tools marks apply_fix optional; gate delta + agentNextSteps deliver USP without fix-provider porting pressure.

**Answer: A**

### 2. Replace M2 inline gate stub how?

- **A)** Big-bang swap to `VerifyGateAdapter` when verify package merges.
- **B)** Feature flag in bridge: `USE_VERIFY_GATE=true` during transition.
- **C)** Keep stub as fallback when verify package not installed.

**Recommended: A** — demo always has verify via `file:`; no dual-path maintenance; bridge depends on verify UPM in M3.

**Answer: A**

### 3. Unity-Scanner extraction baseline?

Upstream repo: `**/Users/alexeyperov/Projects/Unity-Scanner`** (local checkout; no release tags at resolution time — pin **commit hash** in `packages/verify/EXTRACTION.md`).

- **A)** Copy from local Unity-Scanner checkout at extraction date; record commit hash + file list in `packages/verify/EXTRACTION.md`. Agents read source files directly from that path.
- **B)** Extract from a specific release tag when tagged; pin tag + hash.
- **C)** Reimplement rules from scratch using gate-policy issue key format only.

**Recommended: A** — direct read/copy from the maintainer's local Unity-Scanner repo; reproducible via commit hash; no tag required.

**Answer: A** — source path `/Users/alexeyperov/Projects/Unity-Scanner`; pin commit at extraction in `EXTRACTION.md`.

### 4. `agentNextSteps` — who generates them?

- **A)** Bridge `GatePolicy` (has issue context).
- **B)** MCP server post-processes gate payload.
- **C)** Split: bridge generates issue hints; MCP adds tool-name suggestions.

**Recommended: A** — gate-policy placement table assigns bridge; MCP stays thin transport (`isError` only).

**Answer: A**

### 5. Checkpoint persistence to `.unity-agent/checkpoints/` in M3?

- **A)** In-memory only (ring buffer) in M3.
- **B)** Optional disk write, gitignored, off by default.
- **C)** Always persist last checkpoint for Hub history (M4 prep).

**Recommended: B** — gate-policy describes optional persistence; enables manual delta without Hub UI requirement.

**Answer: B**

### 6. `scene_prefab_health` scan cost in scoped gate?

- **A)** Full rule logic scoped to `paths_hint` only.
- **B)** Lightweight subset (missing script, broken prefab link) for Checkpoint mode.
- **C)** Defer `scene_prefab_health` to M3+; ship `missing_references` only in M3.

**Recommended: A** — both rules are M3 deliverables per verify.md; implement `VerifyRunMode.Checkpoint` as counts+keys only to keep gate fast.

**Answer: A**

### 7. `find_references` graph build strategy?

- **A)** Build graph on demand per request (M3).
- **B)** Incremental project index cached in memory after first call.
- **C)** Persist graph cache under `.unity-agent/` (M7 overlap).

**Recommended: A** — simplest correct behavior; optimize in M7 if perf issues appear.

**Answer: A**

### 8. Bridge package dependency on verify?

- **A)** Hard UPM dependency: bridge `package.json` depends on verify.
- **B)** Soft dependency: bridge runs without verify, gate degraded.
- **C)** Merge verify into bridge package (monolith).

**Recommended: A** — architecture separates packages but M3+ gate requires verify; matches wizard installing both.

**Answer: A**

### 9. Agent skill delivery in M3?

- **A)** Ship `skills/unity-agent/SKILL.md` in repo; agents install manually.
- **B)** Keep content only in `agents/agent-skill.md` (no `skills/` file yet).
- **C)** M3 generates `skills/unity-agent/SKILL.md` from `agent-skill.md`; M4 wizard copies into user projects.

**Recommended: C** — `agent-skill.md` is the spec source; `skills/unity-agent/SKILL.md` is the repo copy for M3 done criteria; wizard installs in M4.

**Answer: C**

### 10. M3 automated test bar?

- **A)** EditMode tests in `packages/verify/Tests~/` for MissingReferences + ScenePrefabHealth on fixture assets.
- **B)** Manual E2E only: break prefab → gate fails → fix → pass.
- **C)** Both A and B required for done.

**Recommended: C** — USP needs trustworthy rules; EditMode tests prevent Scanner port regressions.

**Answer: C**

### 11. Issue key format — strict validation on delta?

- **A)** Reject issues that don't match `{ruleId}|{severity}|{assetPath}|{issueCode}`.
- **B)** Best-effort keying; log malformed keys.

**Recommended: A** — delta correctness is the product differentiator; fail fast in dev builds.

**Answer: A**

### 12. `VerifyRunMode.Checkpoint` perf budget (max ms per gate call)?

- **A)** 500 ms hard cap.
- **B)** 2000 ms hard cap (log warning above).
- **C)** No hard cap in M3; log duration only.

**Recommended: B** — enough headroom for scoped prefab scans; tune after M3 E2E.

**Answer: B**

## Suggested doc additions/changes (before / during M3 execution)


| Doc                             | Change                                                                                  | Status                                      |
| ------------------------------- | --------------------------------------------------------------------------------------- | ------------------------------------------- |
| `packages/verify.md`            | Unity-Scanner source path + extraction manifest; `EXTRACTION.md` with commit hash (Q3). | done                                        |
| `packages/verify.md`            | Define `VerifyRunMode.Checkpoint` perf budget 2000 ms (Q12).                            | done                                        |
| `packages/verify/EXTRACTION.md` | Record Unity-Scanner commit hash + ported file list at extraction.                      | done                                        |
| `packages/bridge.md`            | UPM dependency on verify; remove M2 inline stub section after cutover (Q2).             | done                                        |
| `architecture/gate-policy.md`   | §agentNextSteps: bridge heuristic rules; MCP sets `isError` only (Q4).                  | done                                        |
| `agents/agent-skill.md`         | Add concrete tool call examples (JSON snippets) for mutate→gate→fix.                    | done                                        |
| `demo/`                         | Add fixture prefabs for broken/missing script scenarios (Q10).                          | done                                        |
| `execution/M3-verify-gate.md`   | Task order + Unity-Scanner source path for agents (Q3).                                 | done                                        |


