# MCP Tool Catalog

MCP tools exposed by **unity-agent-mcp** (Node server, stdio transport). Tools are prefixed `unity_agent_*` to avoid collision with [Unity Scanner MCP tools](https://github.com/AlexeyPerov/Unity-Scanner) (`unity_scanner_*`) and other bridges.

See also: [gate-policy.md](gate-policy.md), [../idea.md](../idea.md), [../packages/verify.md](../packages/verify.md), [../packages/mcp-server.md](../packages/mcp-server.md).

## Conventions

### Naming

- Tool names: `unity_agent_<action>` (snake_case).
- MCP server name in config: `unity-agent` (recommended).

### Mutating tools and `gate`

All mutating meta-tools accept an optional `gate` parameter:

| Value | Default? | Behavior |
|---|---|---|
| `enforce` | yes | Run checkpoint → mutate → validate → delta; fail MCP if new errors |
| `warn` | | Run full gate; return delta but do not set `isError` |
| `off` | | Raw mutation result only |

### Mutating tools and `paths_hint` (M2 strict)

All mutating tools (`execute_csharp`, `invoke_method`, `execute_menu`) require a **non-empty** `paths_hint` array. Missing or empty `paths_hint` fails immediately with error code `paths_hint_required` and a clear guidance message. There is no whole-project scan fallback. Read-only tools (`find_members`) do not require `paths_hint`.

### Combined response shape (mutating tools)

Every mutating tool returns this envelope (in addition to tool-specific fields):

```json
{
  "mutation": {
    "success": true,
    "output": null,
    "error": null
  },
  "gate": {
    "mode": "enforce",
    "checkpointId": "cp_8f3a2b",
    "validation": {
      "passed": true,
      "issues": []
    },
    "delta": {
      "newErrors": 0,
      "newWarnings": 0,
      "resolvedErrors": 0,
      "resolvedWarnings": 0,
      "newIssues": [],
      "resolvedIssues": []
    }
  },
  "agentNextSteps": []
}
```

### Live vs batch routing

| Symbol | Meaning |
|---|---|
| **live** | Requires open Editor with bridge HTTP listener |
| **batch** | Spawns `Unity -batchmode -executeMethod ...` |
| **both** | Prefers live; falls back to batch if Editor not connected |

---

## M2 — Core meta-tools

Live only. Basic gate stub (checkpoint + fixed `missing_references` on `paths_hint`).

### `unity_agent_ping`

Bridge health check.

**Transport:** both (batch returns project path from config only)

**Input schema:**

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "properties": {},
  "additionalProperties": false
}
```

**Output schema:**

```json
{
  "type": "object",
  "required": ["connected", "projectPath", "unityVersion"],
  "properties": {
    "connected": { "type": "boolean" },
    "projectPath": { "type": "string" },
    "unityVersion": { "type": "string" },
    "bridgeVersion": { "type": "string" },
    "mode": { "enum": ["live", "batch"] },
    "compiling": { "type": "boolean" },
    "isPlaying": { "type": "boolean" }
  }
}
```

---

### `unity_agent_execute_csharp`

Compile and run a C# snippet in the Editor (Roslyn). Primary escape hatch — covers most Editor APIs without typed tools.

**Transport:** live (batch deferred to M5)

**Gate:** yes (`enforce` default)

**Input schema:**

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["code"],
  "properties": {
    "code": {
      "type": "string",
      "description": "C# source. Use return x; to produce output."
    },
    "usings": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Extra using directives beyond defaults (UnityEngine, UnityEditor, etc.)"
    },
    "paths_hint": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Asset paths likely touched; drives scoped gate validation"
    },
    "gate": {
      "enum": ["enforce", "warn", "off"],
      "default": "enforce"
    },
    "timeout_ms": {
      "type": "integer",
      "default": 30000,
      "minimum": 1000,
      "maximum": 300000
    }
  },
  "additionalProperties": false
}
```

**Output schema:** combined mutating response; `mutation.output` is serialized return value or null.

---

### `unity_agent_invoke_method`

Call a method via reflection.

**Transport:** live

**Gate:** yes

**Input schema:**

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["type_name", "method_name"],
  "properties": {
    "type_name": {
      "type": "string",
      "description": "Fully qualified type name"
    },
    "method_name": { "type": "string" },
    "args": {
      "type": "array",
      "description": "JSON-serializable arguments",
      "items": {}
    },
    "is_static": { "type": "boolean", "default": false },
    "assembly_name": {
      "type": "string",
      "description": "Optional assembly simple name if type is ambiguous"
    },
    "paths_hint": {
      "type": "array",
      "items": { "type": "string" }
    },
    "gate": { "enum": ["enforce", "warn", "off"], "default": "enforce" },
    "timeout_ms": { "type": "integer", "default": 30000 }
  },
  "additionalProperties": false
}
```

**Output schema:** combined mutating response; `mutation.output` = method return value.

---

### `unity_agent_execute_menu`

Execute a Unity Editor menu item.

**Transport:** live

**Gate:** yes (lighter — may skip validate if menu is read-only; bridge classifies menu paths)

**Input schema:**

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["menu_path"],
  "properties": {
    "menu_path": {
      "type": "string",
      "description": "e.g. Assets/Refresh, File/Save Project"
    },
    "paths_hint": {
      "type": "array",
      "items": { "type": "string" }
    },
    "gate": { "enum": ["enforce", "warn", "off"], "default": "enforce" }
  },
  "additionalProperties": false
}
```

**Blocked menus (hard deny):** `File/Quit`, destructive batch deletes without confirmation.

---

### `unity_agent_find_members`

Discover types, methods, and properties for agent planning (reduces blind `execute` calls).

**Transport:** live

**Gate:** no (read-only)

**Input schema:**

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "properties": {
    "query": {
      "type": "string",
      "description": "Substring filter on type or member name"
    },
    "kind": {
      "enum": ["type", "method", "property", "all"],
      "default": "all"
    },
    "assembly_filter": { "type": "string" },
    "include_unity_editor": { "type": "boolean", "default": true },
    "include_project": { "type": "boolean", "default": true },
    "max_results": { "type": "integer", "default": 50, "maximum": 200 }
  },
  "additionalProperties": false
}
```

**Output schema:**

```json
{
  "type": "object",
  "properties": {
    "members": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["kind", "name", "declaring_type"],
        "properties": {
          "kind": { "enum": ["type", "method", "property"] },
          "name": { "type": "string" },
          "declaring_type": { "type": "string" },
          "signature": { "type": "string" },
          "summary": { "type": "string", "description": "XML doc summary if available" }
        }
      }
    }
  }
}
```

---

## M3 — Gate + Verify

Full GatePolicy. Backed by [Unity Agent Verify](../packages/verify.md) (`packages/verify`). Only **ported** rules are callable; requesting an unknown rule returns an error with `availableRules` from the verify registry.

Rule IDs match [Unity-Scanner](https://github.com/AlexeyPerov/Unity-Scanner) category IDs where a rule is ported — naming stability only, not a promise that all 21 categories exist in verify.

### Verify rule registry

| Milestone | Callable rule IDs | Notes |
|---|---|---|
| **M3** | `missing_references`, `scene_prefab_health` | Gate default for prefab/scene paths; path-mapping auto-select |
| **M3+** | `asmdef_audit`, `materials`, `textures`, `shader_analysis`, `sprite_2d_analysis`, `animation_analysis`, `audio_analysis`, `project_health`, … | Ported incrementally when gate-policy path table or agent workflows need them |
| **M3+** | fix providers (per rule) | e.g. `materials` null-slot fixes — backs `unity_agent_apply_fix` when implemented |
| **M3+ / M7** | `dependencies` | Unreferenced assets; optional forward-deps for M7 resource |
| **M5** | all **currently ported** rules | `unity_agent_scan_all` runs verify registry only — count grows as rules are added |
| **Not a rule** | `regression_trend` | Use M5 `unity_agent_baseline_create` + `unity_agent_regression_check` instead |

**Planned IDs** (Unity-Scanner parity target; not callable until ported): `terrain_analysis`, `font_text_analysis`, `build_platform_readiness`, `addressables`, `ui_canvas_analysis`, `physics_analysis`, `lod_analysis`, `lighting_analysis`, `particle_analysis`.

**Single-rule scans:** use `unity_agent_scan_paths` with `categories: ["<ruleId>"]` — there is no separate `scan_category` tool.

### `unity_agent_validate_edit`

Scoped health check without a preceding mutation. Used by agents for manual verification or pre-commit checks.

**Transport:** both

**Gate:** N/A (this *is* validation)

**Input schema:**

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["paths"],
  "properties": {
    "paths": {
      "type": "array",
      "items": { "type": "string" },
      "minItems": 1
    },
    "categories": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Verify rule IDs; auto-selected from paths if omitted"
    },
    "platform_profile": {
      "enum": ["mobile", "console", "desktop"],
      "default": "desktop"
    },
    "detail": {
      "enum": ["summary", "normal", "verbose"],
      "default": "normal"
    }
  },
  "additionalProperties": false
}
```

**Output schema:**

```json
{
  "type": "object",
  "required": ["passed", "issues"],
  "properties": {
    "passed": { "type": "boolean", "description": "true if no Error-severity issues" },
    "issues": { "type": "array", "items": { "$ref": "#/definitions/issue" } },
    "categoriesRun": { "type": "array", "items": { "type": "string" } },
    "durationMs": { "type": "number" }
  },
  "definitions": {
    "issue": {
      "type": "object",
      "required": ["severity", "code", "assetPath", "description"],
      "properties": {
        "severity": { "enum": ["Error", "Warning", "Info", "Verbose"] },
        "code": { "type": "string" },
        "assetPath": { "type": "string" },
        "description": { "type": "string" },
        "fixId": { "type": "string" },
        "fixSafe": { "type": "boolean" },
        "agentHint": { "type": "string" }
      }
    }
  }
}
```

---

### `unity_agent_checkpoint_create`

Create a manual checkpoint for later delta comparison.

**Transport:** live

**Input schema:**

```json
{
  "type": "object",
  "properties": {
    "paths": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Scope; empty = whole project summary (expensive)"
    },
    "label": { "type": "string" }
  }
}
```

**Output schema:**

```json
{
  "type": "object",
  "required": ["checkpointId"],
  "properties": {
    "checkpointId": { "type": "string" },
    "timestamp": { "type": "string", "format": "date-time" },
    "fingerprint": { "type": "object" }
  }
}
```

---

### `unity_agent_delta`

Compare current project health vs a checkpoint.

**Transport:** live

**Input schema:**

```json
{
  "type": "object",
  "required": ["checkpoint_id"],
  "properties": {
    "checkpoint_id": { "type": "string" },
    "paths": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Re-validate scope; defaults to checkpoint paths"
    }
  }
}
```

**Output schema:**

```json
{
  "type": "object",
  "required": ["passed", "summary", "newIssues", "resolvedIssues"],
  "properties": {
    "passed": { "type": "boolean" },
    "summary": {
      "type": "object",
      "properties": {
        "newErrors": { "type": "integer" },
        "newWarnings": { "type": "integer" },
        "resolvedErrors": { "type": "integer" },
        "resolvedWarnings": { "type": "integer" }
      }
    },
    "newIssues": { "type": "array" },
    "resolvedIssues": { "type": "array" }
  }
}
```

---

### `unity_agent_find_references`

Reverse dependency lookup for assets (`packages/verify` `ReferenceGraph`; algorithms from Unity-Scanner).

**Transport:** both (batch uses YAML GUID indexer where possible)

**Input schema:**

```json
{
  "type": "object",
  "properties": {
    "asset_path": { "type": "string" },
    "guid": { "type": "string", "pattern": "^[0-9a-fA-F]{32}$" },
    "detail": { "enum": ["summary", "normal", "verbose"], "default": "normal" },
    "max_results": { "type": "integer", "default": 100 }
  },
  "oneOf": [
    { "required": ["asset_path"] },
    { "required": ["guid"] }
  ]
}
```

---

### `unity_agent_scan_paths`

Run one or more **ported** verify rules scoped to paths. For a single rule, pass `categories: ["missing_references"]`.

**Transport:** both

**Input schema:**

```json
{
  "type": "object",
  "required": ["paths"],
  "properties": {
    "paths": { "type": "array", "items": { "type": "string" } },
    "categories": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Verify rule IDs; auto-selected from paths if omitted. Unknown IDs error with availableRules."
    },
    "platform_profile": { "enum": ["mobile", "console", "desktop"] },
    "fail_on_severity": {
      "enum": ["error", "warn", "info", "verbose", "never"],
      "default": "never"
    }
  }
}
```

---

### `unity_agent_apply_fix` (M3+ optional)

Apply a verify rule fix action (ported from Unity-Scanner fix providers where available).

**Transport:** live

**Gate:** yes

**Input schema:**

```json
{
  "type": "object",
  "required": ["fix_id", "issue_id"],
  "properties": {
    "fix_id": { "type": "string" },
    "issue_id": { "type": "string" },
    "dry_run": { "type": "boolean", "default": true },
    "gate": { "enum": ["enforce", "warn", "off"], "default": "enforce" }
  }
}
```

---

## M4 — Hub-assisted configuration

No new MCP tools. Document the **generated MCP client config** and environment variables the Hub wizard writes.

### Example Cursor `mcp.json`

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

### Example OpenCode `opencode.json`

OpenCode uses a different envelope: root key `mcp`, explicit `type: "local"`, a single `command` array, and `environment` instead of `env`.

Global config: `~/.config/opencode/opencode.json`. Project config: `opencode.json` in the project root (safe to commit).

```json
{
  "$schema": "https://opencode.ai/config.json",
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

Optional: gate Unity MCP tools per agent when token budget is tight (OpenCode registers tools as `unity-agent_<tool>`):

```json
{
  "tools": {
    "unity-agent_*": false
  },
  "agent": {
    "unity": {
      "tools": {
        "unity-agent_*": true
      }
    }
  }
}
```

### Client config paths

| Client | Global | Project-scoped | Merge key |
|---|---|---|---|
| Cursor | `~/.cursor/mcp.json` | `.cursor/mcp.json` | `mcpServers.unity-agent` |
| Claude Desktop | OS-specific `claude_desktop_config.json` | — | `mcpServers.unity-agent` |
| Claude Code | — | — | CLI: `claude mcp add` |
| OpenCode | `~/.config/opencode/opencode.json` | `opencode.json` | `mcp.unity-agent` |

Hub wizard (M4) generates client-specific JSON from shared inputs (server path, `UNITY_PROJECT_PATH`, bridge port). Manual equivalents live under `templates/mcp-config/` when added.

### Environment variables

| Variable | Description |
|---|---|
| `UNITY_PROJECT_PATH` | Target project (required for multi-instance) |
| `UNITY_AGENT_BRIDGE_PORT` | Live bridge HTTP port (default `19120`) |
| `UNITY_PATH` | Unity Editor executable (batch fallback) |
| `UNITY_AGENT_GATE_DEFAULT` | `enforce` / `warn` / `off` |

---

## M5 — Batch + CI

### `unity_agent_scan_all`

Full project scan (all enabled verify rules ported to `packages/verify`).

**Transport:** batch (live optional for open Editor)

**Input schema:**

```json
{
  "type": "object",
  "properties": {
    "platform_profile": { "enum": ["mobile", "console", "desktop"] },
    "fail_on_severity": {
      "enum": ["error", "warn", "info", "verbose", "never"],
      "default": "warn"
    },
    "output_path": {
      "type": "string",
      "description": "Optional JSON report path inside project"
    }
  }
}
```

**Output:** Verify batch result + `exitCode` (0 = pass, 1 = issues above threshold).

---

### `unity_agent_baseline_create`

Run full scan and save baseline JSON for regression.

**Transport:** batch

**Input schema:**

```json
{
  "type": "object",
  "properties": {
    "baseline_path": {
      "type": "string",
      "default": "CI/unity-agent-baseline.json"
    },
    "platform_profile": { "enum": ["mobile", "console", "desktop"] }
  }
}
```

---

### `unity_agent_regression_check`

Compare current scan vs baseline; fail on regression.

**Transport:** batch

**Input schema:**

```json
{
  "type": "object",
  "required": ["baseline_path"],
  "properties": {
    "baseline_path": { "type": "string" },
    "regression_threshold": {
      "type": "integer",
      "default": 0,
      "description": "Max allowed increase in Error count"
    },
    "platform_profile": { "enum": ["mobile", "console", "desktop"] }
  }
}
```

---

### Meta-tools batch fallback (M5)

| Tool | Batch support | Notes |
|---|---|---|
| `execute_csharp` | limited | Headless Editor; no play mode |
| `invoke_method` | limited | Same constraints |
| `execute_menu` | partial | Menus that need UI may fail |
| `find_members` | yes | Reflection over loaded assemblies |
| `ping` | yes | Process-only health |

---

## M6 — Bring-your-own-bridge

No new MCP tools or resources. Documentation and skill templates for using **external** mutation bridges while keeping Hub verify as the safety layer.

| Adapter | Role |
|---|---|
| **unity-cli** | Shell `exec` + `reserialize`; run `unity_agent_validate_edit` (or gate-wrapped Hub meta-tools) after |
| **UCP** | CLI mutations; post-hook via `unity_agent_validate_edit` |
| **Unity official MCP** | Optional verify-only sidecar — mutate with official tools, validate with Hub |
| **IvanMurzak MCP** | Optional verify-only sidecar — same pattern |

Pattern: **act with any bridge, verify with Unity Hub Pro stack.**

Deliverables: adapter docs in `specs/` or `skills/`, example post-mutation scripts, no duplicate typed tools in `unity-agent-mcp`.

---

## M7 — MCP Resources

Read-only MCP Resources for passive agent context. Prefer M3 **tools** (`find_references`, `validate_edit`) when an active call is acceptable.

### Shipped (M7)

| URI | Description | Backing |
|---|---|---|
| `unity-agent://health/summary` | Latest scan summary (cached) | Verify runner cache / last `scan_paths` or gate validation |
| `unity-agent://health/baseline` | Current baseline stats if present | M5 `baseline_create` output on disk |
| `unity-agent://bridge/status` | Live bridge connection info | Bridge HTTP ping metadata |

Resources return JSON documents; no gate required.

### Deferred (use tools instead)

| URI | Defer to | Reason |
|---|---|---|
| `unity-agent://references/{guid}` | `unity_agent_find_references` | Tool already covers reverse deps; resource adds little |
| `unity-agent://categories` | error `availableRules` on scan tools | Registry is small and dynamic; no static catalog doc |
| `unity-agent://dependencies/{assetPath}` | M7+ after `dependencies` rule ported | Forward-deps not in M3 verify scope |

---

## Tool count summary

| Milestone | Tools | Resources |
|---|---|---|
| M1 | 0 | 0 |
| M2 | 5 | 0 |
| M3 | +6 | 0 |
| M4 | 0 (config only) | 0 |
| M5 | +3 | 0 |
| M6 | 0 (docs only) | 0 |
| M7 | 0 | 3 |

M3 tools added: `validate_edit`, `checkpoint_create`, `delta`, `find_references`, `scan_paths`, `apply_fix` (optional).

Total MCP tools: **~14** (vs 50+ in generic MCP bridges).
