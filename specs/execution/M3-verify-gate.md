# M3 — Verify Gate

**Status:** **DONE**

**Outcome:** Gated mutations end-to-end (USP).

**Prerequisite:** [questions/questions-3.md](../questions/questions-3.md) — **resolved** (2026-06-13).

**Execution plan:** [M3/execution-plan.md](./M3/execution-plan.md) (agent task breakdown in sub-plans 1–6).

## Spec links

- [packages/verify.md](../packages/verify.md)
- [architecture/gate-policy.md](../architecture/gate-policy.md)
- [architecture/mcp-tools.md](../architecture/mcp-tools.md) §M3
- [agents/agent-skill.md](../agents/agent-skill.md)

## Unity-Scanner source (read / copy upstream)

M3 rule ports are **copied and adapted** from the maintainer's Unity-Scanner project — not reimplemented from scratch unless a file is listed under "Rewrite" in [verify.md](../packages/verify.md).

| | |
|---|---|
| **Local path** | `/Users/alexeyperov/Projects/Unity-Scanner` |
| **Access** | Same machine checkout; agents **open, read, and copy** source files directly from this repo during implementation |
| **Record keeping** | After each extraction batch, append commit hash + file list to `packages/verify/EXTRACTION.md` |
| **Adapt on copy** | Namespace → `UnityAgentVerify.*`; file header `// Extracted from Unity-Scanner: <relative-path>`; drop UI/tab drawers per extraction manifest |

**Primary port targets (M3):**

| Unity-Scanner source | Verify destination |
|---|---|
| `Editor/Categories/MissingReferences/MissingReferencesScanner.cs` (+ mapper, models) | `packages/verify/Editor/Rules/MissingReferences/` |
| `Editor/Categories/ScenePrefabHealth/ScenePrefabHealthScanner.cs` (+ mapper) | `packages/verify/Editor/Rules/ScenePrefabHealth/` |
| `Editor/FindReferencesWindow.RefsMapBuilder` (or equivalent under FindReferences) | `packages/verify/Editor/References/ReferenceGraph.cs` |
| Shared utilities referenced by ported scanners | `packages/verify/Editor/Internals/` |

Do **not** import Unity-Scanner orchestrator, UI, MCP, batch, or cache layers — see verify.md §Rewrite.

## Task order

Execute in this order (each task should land a testable increment):

1. **Verify rules** — port `missing_references` and `scene_prefab_health` from Unity-Scanner; replace greenfield stubs where needed; EditMode tests on fixture assets.
2. **`ReferenceGraph`** — port `RefsMapBuilder`; wire for `unity_agent_find_references`.
3. **Gate swap** — `VerifyGateAdapter` + full `GatePolicy` in bridge (big-bang; no feature flag); `agentNextSteps` in bridge; checkpoint ring buffer + optional disk persistence (off by default).
4. **MCP tools** — `validate_edit`, `checkpoint_create`, `delta`, `find_references`, `scan_paths` (no `apply_fix` unless cheap — optional per Q1).
5. **Agent skill** — generate `skills/unity-agent/SKILL.md` from [agent-skill.md](../agents/agent-skill.md).
6. **E2E** — mutation breaks reference → `isError: true` + `agentNextSteps` → fix → pass (reuse [M2 manual checklist](../execution/M2/m2-manual-e2e-checklist.md) gate steps).

## Tasks

- [x] Port `missing_references` from Unity-Scanner → `packages/verify` (replace/adapt existing greenfield rule)
- [x] Port `scene_prefab_health` from Unity-Scanner → `packages/verify`
- [x] `ReferenceGraph` for `unity_agent_find_references` (port from Unity-Scanner)
- [x] `VerifyGateAdapter` + full GatePolicy in bridge (`agentNextSteps`, checkpoint store, strict issue keys)
- [x] M3 MCP tools: `validate_edit`, `checkpoint_create`, `delta`, `find_references`, `scan_paths`
- [x] `skills/unity-agent/SKILL.md` generated from agent-skill spec
- [x] EditMode tests (`packages/verify/Tests~/`) + manual E2E gate loop
- [x] `packages/verify/EXTRACTION.md` — Unity-Scanner commit hash + ported file list

## Done when

- [x] Gate delta detects new `missing_references` errors after bad prefab edit
- [x] `agentNextSteps` populated on gate failure (bridge-generated)
- [x] Agent skill documents mutate → gate → fix loop
- [x] EditMode tests pass for both M3 rules on fixture assets
- [x] Manual E2E: break prefab → gate fails → fix → pass

## Agent implementation notes

When implementing any M3 verify rule or `ReferenceGraph`:

1. **Read first** — open the corresponding file under `/Users/alexeyperov/Projects/Unity-Scanner` and understand Scanner behavior before editing verify.
2. **Copy then adapt** — port the scanner/mapper/models; do not pull Editor windows, tabs, or MCP shims.
3. **Preserve issue keys** — `{ruleId}|{severity}|{assetPath}|{issueCode}` must match gate-policy and Scanner category IDs.
4. **Checkpoint mode** — gate calls use counts + issue keys only; full issue payloads on validate/delta failure paths.
5. **Perf** — scoped gate checkpoint budget: **2000 ms** (log warning if exceeded; Q12).
