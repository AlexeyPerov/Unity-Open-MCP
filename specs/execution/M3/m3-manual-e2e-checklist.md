# M3 Manual E2E Checklist

Manual end-to-end verification for verify gate, M3 MCP tools, and agent loop. Run against the `demo/` project with Unity Editor open and the bridge listener active.

**Prerequisites:**

Complete M2 and M2.5 prerequisites first (Node.js >= 18, MCP server built, `demo/` project open in Unity 6, bridge + verify packages via `file:`). See [M2 E2E checklist](../M2/m2-manual-e2e-checklist.md) for base setup.

Verify the bridge is running:

```bash
curl -s http://127.0.0.1:19120/ping | python3 -m json.tool
# Expect JSON with "connected": true
```

**Fixture assets** (see `demo/README.md` §Gate Test Fixtures for full descriptions):

| Fixture | Path | Scenario |
|---|---|---|
| HealthyFixture | `Assets/Fixtures/HealthyFixture.prefab` | Clean baseline — should pass all rules |
| MissingScriptFixture | `Assets/Fixtures/MissingScriptFixture.prefab` | MonoBehaviour with nonexistent script GUID — triggers `missing_script` |
| BrokenRefFixture | `Assets/Fixtures/BrokenRefFixture.prefab` | MeshFilter with nonexistent mesh GUID — triggers `missing_guid` |
| RestorableRefFixture | `Assets/Fixtures/RestorableRefFixture.prefab` | Healthy cube prefab — edit GUID to break, revert to fix |

---

## Step 1: `unity_agent_validate_edit` — scoped health check

Call validate without mutation (HTTP or MCP client):

```bash
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_validate_edit \
  -H "Content-Type: application/json" \
  -d '{"paths": ["Assets/Fixtures/HealthyFixture.prefab"]}' | python3 -m json.tool
```

**Expected:**

| Field | Expected |
|---|---|
| `passed` | `true` |
| `issues` | empty array |
| `categoriesRun` | includes `missing_references` and `scene_prefab_health` |

Then validate the broken fixture for contrast:

```bash
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_validate_edit \
  -H "Content-Type: application/json" \
  -d '{"paths": ["Assets/Fixtures/MissingScriptFixture.prefab"]}' | python3 -m json.tool
```

**Expected:** `passed: false`, `issues` contains `missing_script` entry with `fixId: "remove_missing_script"`.

---

## Step 2: `unity_agent_find_references` — reverse deps

Before deleting or breaking a script, confirm references:

```bash
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_find_references \
  -H "Content-Type: application/json" \
  -d '{"asset_path": "Assets/Fixtures/HealthyFixture.prefab"}' | python3 -m json.tool
```

**Expected:** response lists `referencedBy` array with `assetPath` + `guid` entries. For the HealthyFixture, expect the demo Main.unity scene if the prefab is placed in it.

---

## Step 3: Break prefab — enforce gate fails

Break the RestorableRefFixture by mutating its mesh GUID to a fake value:

```bash
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_execute_csharp \
  -H "Content-Type: application/json" \
  -d '{
    "code": "var path = \"Assets/Fixtures/RestorableRefFixture.prefab\"; var lines = System.IO.File.ReadAllLines(path); for (int i = 0; i < lines.Length; i++) { if (lines[i].Contains(\"m_Mesh:\") && lines[i].Contains(\"0000000000000000e000000000000000\")) { lines[i] = lines[i].Replace(\"0000000000000000e000000000000000\", \"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"); } } System.IO.File.WriteAllLines(path, lines); UnityEditor.AssetDatabase.ImportAsset(path); return \"broke\";",
    "paths_hint": ["Assets/Fixtures/RestorableRefFixture.prefab"],
    "gate": "enforce"
  }' | python3 -m json.tool
```

**Expected (MCP client):**

| Field | Expected |
|---|---|
| `isError` | `true` |
| `gate.delta.summary.newErrors` | `> 0` |
| `agentNextSteps` | non-empty array with actionable hints |
| `gate.delta.newIssues` | includes `missing_references` or `scene_prefab_health` issue |

---

## Step 4: `unity_agent_apply_fix` — dry run then apply

If Step 3 issues expose a `fixId` (or use `remove_missing_script` on the MissingScriptFixture):

```bash
# Dry run — remove missing script from MissingScriptFixture
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_apply_fix \
  -H "Content-Type: application/json" \
  -d '{"fix_id": "remove_missing_script", "issue_id": "missing_references|ERROR|Assets/Fixtures/MissingScriptFixture.prefab|missing_script", "dry_run": true}' | python3 -m json.tool

# Apply
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_apply_fix \
  -H "Content-Type: application/json" \
  -d '{"fix_id": "remove_missing_script", "issue_id": "missing_references|ERROR|Assets/Fixtures/MissingScriptFixture.prefab|missing_script", "dry_run": false}' | python3 -m json.tool
```

**Expected:** dry run describes change; apply succeeds without throwing.

---

## Step 5: Re-mutate or validate — gate passes

Re-run validate on the fixed fixture:

```bash
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_validate_edit \
  -H "Content-Type: application/json" \
  -d '{"paths": ["Assets/Fixtures/MissingScriptFixture.prefab"]}' | python3 -m json.tool
```

Or restore the RestorableRefFixture:

```bash
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_execute_csharp \
  -H "Content-Type: application/json" \
  -d '{
    "code": "var path = \"Assets/Fixtures/RestorableRefFixture.prefab\"; var lines = System.IO.File.ReadAllLines(path); for (int i = 0; i < lines.Length; i++) { if (lines[i].Contains(\"m_Mesh:\") && lines[i].Contains(\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\")) { lines[i] = lines[i].Replace(\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\", \"0000000000000000e000000000000000\"); } } System.IO.File.WriteAllLines(path, lines); UnityEditor.AssetDatabase.ImportAsset(path); return \"restored\";",
    "paths_hint": ["Assets/Fixtures/RestorableRefFixture.prefab"],
    "gate": "enforce"
  }' | python3 -m json.tool
```

**Expected:**

| Field | Expected |
|---|---|
| `passed` | `true` |
| MCP `isError` on enforce mutation | `false` when no new errors |

---

## Step 6: Manual checkpoint + delta workflow

```bash
# Create checkpoint
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_checkpoint_create \
  -H "Content-Type: application/json" \
  -d '{"paths": ["Assets/Fixtures/RestorableRefFixture.prefab"], "label": "m3-e2e"}' | python3 -m json.tool

# After optional mutations with gate: off, compare delta
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_delta \
  -H "Content-Type: application/json" \
  -d '{"checkpoint_id": "<checkpointId from above>"}' | python3 -m json.tool
```

**Expected:** `checkpointId` returned; delta reports `newIssues` / `resolvedIssues` consistent with changes.

---

## Step 7: `unity_agent_scan_paths` — explicit rule scan

```bash
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_scan_paths \
  -H "Content-Type: application/json" \
  -d '{
    "paths": ["Assets/Fixtures/BrokenRefFixture.prefab"],
    "categories": ["missing_references"]
  }' | python3 -m json.tool
```

**Expected:** issues array with `missing_guid` entry + `durationMs`. Test unknown category:

```bash
curl -s -X POST http://127.0.0.1:19120/tools/unity_agent_scan_paths \
  -H "Content-Type: application/json" \
  -d '{"paths": ["Assets/Fixtures/HealthyFixture.prefab"], "categories": ["nonexistent_rule"]}' | python3 -m json.tool
```

**Expected:** error with `availableRules` listing `missing_references`, `scene_prefab_health`.

---

## Step 8: MCP client integration (Cursor / Claude / OpenCode)

1. Restart MCP client so M3 tool schemas load.
2. Confirm `ListTools` includes M3 tools (`validate_edit`, `checkpoint_create`, `delta`, `find_references`, `scan_paths`, `apply_fix`).
3. Prompt agent: "Validate Assets/Fixtures/MissingScriptFixture.prefab, then use apply_fix to remove the missing script, then validate again with gate enforce."
4. Verify agent receives `agentNextSteps` on failure and completes fix loop.

**Expected:** full mutate → gate → fix loop without manual curl.

---

## Step 9: M2 + M2.5 regression

Re-run critical M2 meta-tool and M2.5 typed tool smoke from [M2 E2E](../M2/m2-manual-e2e-checklist.md) and [M2.5 E2E](../M2.5/m2.5-manual-e2e-checklist.md) Step 2 (`unity_agent_editor_status`).

**Expected:** no regressions in meta-tool gate envelope or typed tool dispatch.

---

## Sign-off

| Check | Pass |
|---|---|
| validate_edit on healthy fixture | |
| find_references returns reverse deps | |
| enforce gate fails with `isError` + `agentNextSteps` | |
| apply_fix dry_run + apply | |
| post-fix validate / gate passes | |
| checkpoint_create + delta | |
| scan_paths explicit rule | |
| MCP client M3 tools listed | |
| M2 / M2.5 regression | |
