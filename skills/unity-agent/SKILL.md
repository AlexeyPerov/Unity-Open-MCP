# Unity Agent Skill

Skill for AI agents working with Unity projects via the `unity-agent` MCP server. This skill covers the gate + verify workflow.

## Install

Copy this file to your client's skills directory:

| Client | Path |
|---|---|
| Cursor / Claude Code | `.claude/skills/unity-agent/SKILL.md` (project) or global skills dir |
| OpenCode | `.opencode/skills/unity-agent/SKILL.md` (project) or `~/.config/opencode/skills/` |

## Available tools

All tools are prefixed `unity_agent_*`.

### Mutating tools (gate-aware)

- `unity_agent_execute_csharp` — Compile and run a C# snippet in the Unity Editor.
- `unity_agent_invoke_method` — Call a method via reflection.
- `unity_agent_execute_menu` — Execute a Unity Editor menu item.
- `unity_agent_apply_fix` — Apply a verify rule fix action (e.g. remove missing script).

All mutating tools accept `gate` (`enforce` / `warn` / `off`, default `enforce`) and require a non-empty `paths_hint` array.

### Read-only tools (no gate)

- `unity_agent_ping` — Bridge health check.
- `unity_agent_find_members` — Discover types, methods, and properties.
- `unity_agent_validate_edit` — Scoped health scan without mutation.
- `unity_agent_find_references` — Reverse dependency lookup for assets.
- `unity_agent_scan_paths` — Run specific verify rules over scoped paths.

### Checkpoint tools

- `unity_agent_checkpoint_create` — Create a manual checkpoint for later delta comparison.
- `unity_agent_delta` — Compare current project health vs a checkpoint.

## Core loop: mutate → gate → fix

1. **Discover** — use `unity_agent_find_members` before blind `execute_csharp` calls.
2. **Declare scope** — always pass `paths_hint` with asset paths you intend to touch.
3. **Mutate** — call `unity_agent_execute_csharp`, `invoke_method`, or `execute_menu` with default `gate: enforce`.
4. **Read gate** — on `isError: true`, inspect `gate.delta.newIssues` and `agentNextSteps`.
5. **Fix** — address top error; optionally use `unity_agent_apply_fix` with `dry_run: true` first.
6. **Retry** — re-run mutation; confirm `gate.delta.resolvedErrors > 0` or `newErrors == 0`.

**Principle: mutation success ≠ project safe.** A successful C# compile can still break prefab references.

### Example: execute_csharp with gate enforcement

```json
// Tool call: unity_agent_execute_csharp
{
  "code": "var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(\"Assets/Prefabs/Player.prefab\");\nvar go = PrefabUtility.InstantiatePrefab(prefab);\ngo.transform.position = Vector3.zero;\nreturn go.name;",
  "usings": ["UnityEngine"],
  "paths_hint": ["Assets/Prefabs/Player.prefab"],
  "gate": "enforce"
}
```

**Success response** — gate passed, no new issues:

```json
{
  "mutation": { "success": true, "output": "Player(Clone)", "error": null },
  "gate": {
    "mode": "enforce",
    "checkpointId": "cp_8f3a2b",
    "validation": { "passed": true, "issues": [] },
    "delta": {
      "newErrors": 0, "newWarnings": 0,
      "resolvedErrors": 0, "resolvedWarnings": 0,
      "newIssues": [], "resolvedIssues": []
    }
  },
  "agentNextSteps": []
}
```

**Failure response** — gate detected new errors (`isError: true` in MCP):

```json
{
  "mutation": { "success": true, "output": "Player(Clone)", "error": null },
  "gate": {
    "mode": "enforce",
    "checkpointId": "cp_a1c7e4",
    "validation": {
      "passed": false,
      "issues": [
        {
          "severity": "Error",
          "code": "MISSING_SCRIPT",
          "assetPath": "Assets/Prefabs/Player.prefab",
          "description": "MonoBehaviour 'PlayerController' has missing script GUID",
          "fixId": "remove_missing_script",
          "fixSafe": true,
          "agentHint": "Remove the missing script component to restore prefab health"
        }
      ]
    },
    "delta": {
      "newErrors": 1, "newWarnings": 0,
      "resolvedErrors": 0, "resolvedWarnings": 0,
      "newIssues": [
        "missing_references|Error|Assets/Prefabs/Player.prefab|MISSING_SCRIPT"
      ],
      "resolvedIssues": []
    }
  },
  "agentNextSteps": [
    "New error: missing_references MISSING_SCRIPT on Assets/Prefabs/Player.prefab",
    "Fix available: use unity_agent_apply_fix with fix_id=\"remove_missing_script\"",
    "Verify after fix: use unity_agent_validate_edit on the affected paths"
  ]
}
```

### Example: apply_fix with dry_run

```json
// Tool call: unity_agent_apply_fix
{
  "fix_id": "remove_missing_script",
  "issue_id": "missing_references|Error|Assets/Prefabs/Player.prefab|MISSING_SCRIPT",
  "dry_run": true
}
```

**Dry-run response** — no mutation applied, describes what will happen:

```json
{
  "fixDescription": "Remove MonoBehaviour with missing script GUID from Player.prefab",
  "affectedAssets": ["Assets/Prefabs/Player.prefab"],
  "safe": true
}
```

After confirming, apply for real (`"dry_run": false`). The tool runs through the gate envelope like other mutating tools:

```json
{
  "mutation": { "success": true, "output": "Removed 1 missing script component(s)", "error": null },
  "gate": {
    "mode": "enforce",
    "checkpointId": "cp_d5f9c1",
    "validation": { "passed": true, "issues": [] },
    "delta": {
      "newErrors": 0, "newWarnings": 0,
      "resolvedErrors": 1, "resolvedWarnings": 0,
      "newIssues": [],
      "resolvedIssues": [
        "missing_references|Error|Assets/Prefabs/Player.prefab|MISSING_SCRIPT"
      ]
    }
  },
  "agentNextSteps": []
}
```

### Example: find_references before delete

Before deleting or moving an asset, check who depends on it:

```json
// Tool call: unity_agent_find_references
{ "asset_path": "Assets/Materials/PlayerMat.mat", "max_results": 50 }
```

```json
{
  "queriedAssetPath": "Assets/Materials/PlayerMat.mat",
  "queriedAssetGuid": "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6",
  "referencedBy": [
    { "assetPath": "Assets/Prefabs/Player.prefab", "guid": "11223344556677889900aabbccddeeff" },
    { "assetPath": "Assets/Scenes/Main.unity", "guid": "aabbccddeeff00112233445566778899" }
  ],
  "totalCount": 2
}
```

### Example: validate_edit (read-only check)

Run a scoped health scan without any preceding mutation — useful for pre-commit verification:

```json
// Tool call: unity_agent_validate_edit
{ "paths": ["Assets/Prefabs/Player.prefab"], "detail": "normal" }
```

```json
{
  "passed": true,
  "issues": [],
  "categoriesRun": ["missing_references", "scene_prefab_health"],
  "durationMs": 142
}
```

### Example: scan_paths with explicit categories

```json
// Tool call: unity_agent_scan_paths
{ "paths": ["Assets/Prefabs/", "Assets/Scenes/"], "categories": ["missing_references"] }
```

```json
{
  "passed": false,
  "issues": [
    {
      "severity": "Error",
      "code": "MISSING_SCRIPT",
      "assetPath": "Assets/Prefabs/Enemy.prefab",
      "description": "MonoBehaviour 'AIController' has missing script GUID",
      "fixId": "remove_missing_script",
      "fixSafe": true,
      "agentHint": "Remove the missing script component"
    }
  ],
  "categoriesRun": ["missing_references"],
  "durationMs": 210
}
```

## Gate modes

| Mode | When to use |
|---|---|
| `enforce` (default) | Normal agent edits — fail fast on new errors |
| `warn` | Exploratory changes — read `gate.delta` but continue |
| `off` | Trusted scripts only — no checkpoint/validate; use sparingly |

### Example: warn mode

```json
// Tool call: unity_agent_execute_csharp
{
  "code": "Selection.activeGameObject = GameObject.Find(\"Camera\");\nreturn Selection.activeGameObject != null;",
  "paths_hint": ["Assets/Scenes/Main.unity"],
  "gate": "warn"
}
```

Even if gate detects new issues, `isError` is `false` in MCP — the agent reads `gate.delta` and decides whether to proceed or fix.

### Example: gate off

```json
// Tool call: unity_agent_execute_csharp
{
  "code": "EditorApplication.Exit(0);",
  "paths_hint": [],
  "gate": "off"
}
```

No checkpoint, no validation, no delta. Use only for trusted administrative scripts.

## `paths_hint` rules

- Include every asset path the mutation may touch (prefabs, scenes, scripts, materials).
- If unsure, include the parent folder's key assets rather than leaving empty.
- Empty `paths_hint` fails immediately with error code `paths_hint_required`.

## Manual checkpoint workflow

For large refactors:

1. `unity_agent_checkpoint_create` with scoped paths.
2. Run trusted mutations with `gate: off` if needed.
3. `unity_agent_delta` against checkpoint — single verification pass.

### Example: checkpoint → mutate → delta

```json
// Step 1: create checkpoint
// Tool call: unity_agent_checkpoint_create
{ "paths": ["Assets/Prefabs/"], "label": "before-refactor" }
```

```json
{ "checkpointId": "cp_k4m8n2", "timestamp": "2026-06-15T14:00:00Z", "fingerprint": { "issueCount": 3 } }
```

```json
// Step 2: run mutations (gate: off for bulk, or enforce per-call)
// ... execute_csharp / invoke_method calls ...

// Step 3: delta check
// Tool call: unity_agent_delta
{ "checkpoint_id": "cp_k4m8n2" }
```

```json
{
  "passed": false,
  "summary": {
    "newErrors": 1, "newWarnings": 2,
    "resolvedErrors": 3, "resolvedWarnings": 0
  },
  "newIssues": [
    "missing_references|Error|Assets/Prefabs/Enemy.prefab|MISSING_SCRIPT"
  ],
  "resolvedIssues": [
    "missing_references|Error|Assets/Prefabs/Player.prefab|MISSING_SCRIPT",
    "scene_prefab_health|Warning|Assets/Scenes/Main.unity|BROKEN_PREFAB_INSTANCE",
    "missing_references|Warning|Assets/Scenes/Main.unity|MISSING_SCRIPT"
  ]
}
```

## Setup without Hub

1. Add `com.alexeyperov.unity-agent-bridge` and `com.alexeyperov.unity-agent-verify` to `Packages/manifest.json`.
2. Configure MCP client with `unity-agent` server entry.
3. Open project in Unity 6; confirm `unity_agent_ping` returns `connected: true`.

### Example MCP client config

**Cursor / Claude Desktop** (`mcp.json`):

```json
{
  "mcpServers": {
    "unity-agent": {
      "command": "node",
      "args": ["/path/to/unity-ai-hub/mcp-server/dist/index.js"],
      "env": {
        "UNITY_PROJECT_PATH": "/path/to/MyGame",
        "UNITY_AGENT_BRIDGE_PORT": "19120"
      }
    }
  }
}
```

**OpenCode** (`opencode.json`):

```json
{
  "mcp": {
    "unity-agent": {
      "type": "local",
      "command": ["node", "/path/to/unity-ai-hub/mcp-server/dist/index.js"],
      "enabled": true,
      "environment": {
        "UNITY_PROJECT_PATH": "/path/to/MyGame",
        "UNITY_AGENT_BRIDGE_PORT": "19120"
      }
    }
  }
}
```
