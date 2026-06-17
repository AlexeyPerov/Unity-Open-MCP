# MCP Tools

MCP tools are registered in `mcp-server/src/tools/index.ts` and exposed by the stdio server in `mcp-server/src/index.ts`.

## Quick lookup

| Question | Section |
|---|---|
| Which tool names are available? | Tool catalog |
| How do I discover everything at once? | Capability discovery |
| How route selection works? | Route policy |
| Which tools can run in batch? | Batch support |
| Which tools are offline-first? | Offline/compressible reads |

## Tool catalog

### Core tools (M2 + M2.5)

- `unity_open_mcp_ping`
- `unity_open_mcp_execute_csharp`
- `unity_open_mcp_invoke_method`
- `unity_open_mcp_execute_menu`
- `unity_open_mcp_find_members`
- `unity_open_mcp_compile_check`
- `unity_open_mcp_editor_status`

### Gate and validation tools (M3 + M5)

- `unity_open_mcp_validate_edit`
- `unity_open_mcp_checkpoint_create`
- `unity_open_mcp_delta`
- `unity_open_mcp_find_references`
- `unity_open_mcp_scan_paths`
- `unity_open_mcp_apply_fix`
- `unity_open_mcp_scan_all`
- `unity_open_mcp_baseline_create`
- `unity_open_mcp_regression_check`

### Asset intelligence tools (M9)

- `unity_open_mcp_reserialize`
- `unity_open_mcp_read_asset`
- `unity_open_mcp_search_assets`
- `unity_open_mcp_list_assets`

### Agent senses tools (M10)

- `unity_agent_run_tests` — EditMode + PlayMode test runner with per-test pass/fail, filter by assembly/namespace/class/method, domain-reload-safe PlayMode via file handoff.
- `unity_agent_screenshot` — Capture Scene view, Game view, or isolated 2×2 composite (Front/Right/Back/Top) of a single GameObject with layer culling. Returns saved PNG file path.
- `unity_agent_read_console` — Read Unity console entries via reflection on internal `LogEntries`. Filter by type (error/warning/log/all), user-code stack filter, optional clear, token-bounded output.
- `unity_agent_profiler_capture` — Read the Unity Profiler frame hierarchy via `ProfilerDriver.GetHierarchyFrameDataView`. Drill-down by parent ID / root name-substring / depth, multi-frame averaging, token-bounded top-N by self/total/calls.
- `unity_agent_profiler_memory` — Live memory allocator stats (allocated/reserved/unused/temp/managed heap) with optional GC first.
- `unity_agent_profiler_rendering` — Rendering environment batch: GPU/SystemInfo, active render pipeline, QualitySettings, screen resolution, target frame rate, Time stats.
- `unity_agent_spatial_query` — Physics-based spatial reasoning (raycast / overlap / bounds / ground_check / nearest) against the live scene. Targets addressed by instance_id/path/name; returns hit object instanceId/name/path.

### Capability discovery

- `unity_open_mcp_capabilities` — Returns the full capability surface in one call: every tool with its input schema and route policy, every verify rule with applicable asset kinds and issue severities, and every available fix. Each capability carries an `implemented` boolean; planned-but-unbuilt items return with `status: "planned"` and guidance instead of failing. Call this first to learn what is available.
- `unity_agent_generate_skill` — Generates a project-specific SKILL.md reflecting the actual project state: Unity version, installed packages (including bridge/verify versions), available tools and verify rules, key MonoBehaviour/ScriptableObject types discovered from source, and the mutate→gate→fix workflow. Set `write: true` to persist the file into `.claude/skills/`, `.cursor/skills/`, `.opencode/skills/`, or `.agents/skills/`. Regenerate after package or script changes.

### Typed editor tools (M16 planned)

M16 adds a curated typed surface on top of existing meta-tools. Duplicates are intentionally avoided:
- keep `unity_open_mcp_execute_csharp`, `unity_open_mcp_invoke_method`, `unity_open_mcp_find_members` as core
- keep M9 read/list/search/reserialize as the asset intelligence baseline
- keep M10 sense tools for screenshots/test run/profiler capture/memory/rendering/spatial

Planned categories:

- **Project & Asset Management:** typed asset CRUD/material/prefab stage helpers (`unity_open_mcp_assets_*`)
- **GameObject & Components:** typed hierarchy/component lifecycle (`unity_open_mcp_gameobject_*`)
- **Scene Management:** typed scene lifecycle/data (`unity_open_mcp_scene_*`)
- **Package Manager:** `unity_open_mcp_package_list`, `unity_open_mcp_package_add`, `unity_open_mcp_package_remove`, `unity_open_mcp_package_search`
- **Console + Editor state/selection:** `unity_open_mcp_console_clear`, `unity_open_mcp_editor_set_state`, `unity_open_mcp_selection_get`, `unity_open_mcp_selection_set`
- **Reflection/scripts/object data:** `unity_open_mcp_type_schema`, `unity_open_mcp_script_read`, `unity_open_mcp_script_write`, `unity_open_mcp_script_delete`, `unity_open_mcp_object_get_data`, `unity_open_mcp_object_modify`
- **Profiler & Diagnostics session:** `unity_open_mcp_profiler_*` session/module/save/load/clear helpers (non-duplicate with M10 capture/memory/rendering)
- **Gate intelligence:** `unity_open_mcp_impact_preview`, `unity_open_mcp_gate_budget_estimate`, `unity_open_mcp_mutation_explain`

## Capability discovery

`unity_open_mcp_capabilities` lets an agent self-discover the entire tool + rule + fix surface in a single call, including what is planned but not yet built.

- Implementation: `mcp-server/src/capabilities/build-capabilities.ts`, `mcp-server/src/capabilities/rule-catalog.ts`.
- Routes locally (`_source: "local"`) — never hits the live bridge or batch Unity.

### Response shape

```json
{
  "tools": [
    {
      "name": "unity_open_mcp_scan_paths",
      "implemented": true,
      "status": "implemented",
      "category": "gate-and-verify",
      "routePolicy": "live",
      "batchCapable": false,
      "inputSchema": { "type": "object", "properties": { "...": "..." } }
    },
    {
      "name": "unity_open_mcp_type_schema",
      "implemented": false,
      "status": "planned",
      "category": "reflection",
      "guidance": "Planned reflection surface. Use find_members ..."
    }
  ],
  "rules": [
    {
      "id": "missing_references",
      "implemented": true,
      "status": "implemented",
      "title": "Missing references",
      "applicableAssetKinds": ["prefab", "scene", "scriptable_object"],
      "issues": [
        { "code": "missing_script", "severity": "Error", "fixIds": ["remove_missing_script"] }
      ]
    },
    {
      "id": "materials",
      "implemented": false,
      "status": "planned",
      "guidance": "Not yet ported. Use find_references ..."
    }
  ],
  "fixes": [
    { "id": "remove_missing_script", "implemented": true, "safe": true, "rules": ["missing_references"] }
  ],
  "counts": {
    "toolsImplemented": 28,
    "toolsPlanned": 14,
    "rulesImplemented": 2,
    "rulesPlanned": 7,
    "fixesImplemented": 1,
    "fixesPlanned": 0
  },
  "routing": {
    "liveDefault": true,
    "batchFallback": true,
    "batchRequirements": ["UNITY_PATH", "UNITY_PROJECT_PATH"],
    "batchBlocked": [
      { "tool": "unity_open_mcp_execute_csharp", "reason": "Requires a live Editor compile context." },
      { "tool": "unity_open_mcp_invoke_method", "reason": "Requires a live Editor reflection context." },
      { "tool": "unity_open_mcp_execute_menu", "reason": "Menu execution needs the Editor UI; most menus fail in -batchmode." }
    ],
    "liveOnlyCategories": ["agent-senses"],
    "perToolFlag": "batchCapable"
  },
  "_source": "local"
}
```

### `routing` summary

The top-level `routing` object is a one-shot narrative for agents so a single `unity_open_mcp_capabilities` call gives the batch/route story without reading these docs. It is independent of the `kind` filter — asking for `kind: "rules"` still returns `routing`.

| Field | Meaning |
|---|---|
| `liveDefault` | `true` — most tools prefer the live bridge when connected. |
| `batchFallback` | `true` — when the live bridge is unavailable, only `batchCapable` tools fall back to a headless Unity spawn. |
| `batchRequirements` | Env vars a headless batch spawn requires (`UNITY_PATH`, `UNITY_PROJECT_PATH`). |
| `batchBlocked` | Mutating meta-tools intentionally rejected in batch, each with a short `reason`. |
| `liveOnlyCategories` | Tool categories that have no batch form (e.g. `agent-senses`). |
| `perToolFlag` | The per-tool flag name (`batchCapable`) agents should read for the authoritative per-tool answer. |

Per-tool route details live on each tool entry (`routePolicy`, `batchCapable`, `category`); the `routing` object only summarizes them.

### Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `kind` | `"tools"` \| `"rules"` \| `"fixes"` | (all) | Filter to a single surface. |
| `include_planned` | boolean | `true` | Set false to see only implemented items. |

### Planned-vs-implemented contract

- Every registered tool ships `implemented: true`.
- Planned typed tools and planned verify rules ship `implemented: false` with `status: "planned"` and a `guidance` string explaining the fallback — they never raise hard errors.
- The rule catalog is versioned with the package, so the `implemented` flags reflect what ships in the matching bridge/verify release.

## Skill generation

`unity_agent_generate_skill` produces a project-specific SKILL.md that gives the LLM up-to-date context for the specific project — installed tool versions, available verify rules, key MonoBehaviour/ScriptableObject types, and the core workflow.

- Implementation: `mcp-server/src/skill/generate-skill.ts`.
- Routes locally (`_source: "local"`) — reads `ProjectSettings/ProjectVersion.txt`, `Packages/manifest.json`, and scans `.cs` files under `Assets/` for type declarations. Never hits the live bridge or batch Unity.

### Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `write` | boolean | `false` | When `true`, write the generated skill to client skill directories. |
| `clients` | string[] | `["claude"]` | Which client skill dirs to write to. Only used when `write: true`. Allowed values are derived from the single-source manifest at `skills/client-paths.json` (`cursor`, `claude`, `opencode`, `agents`). `agents` writes to `.agents/skills/` for ZCode and other `.agents`-aware clients. |

### Response shape

```json
{
  "skill": "# Unity Agent Skill — MyGame\n...",
  "project": {
    "projectName": "MyGame",
    "unityVersion": "6000.0.1f1",
    "packages": [{ "id": "com.unity.ugui", "version": "2.0.0" }],
    "bridgeVersion": "0.3.0",
    "verifyVersion": "0.3.0",
    "monoBehaviours": [{ "name": "PlayerController", "namespace": "MyGame", "filePath": "Assets/Scripts/PlayerController.cs" }],
    "scriptableObjects": []
  },
  "written": [
    { "client": "claude", "relativePath": ".claude/skills/unity-open-mcp/SKILL.md", "absolutePath": "/path/.claude/skills/unity-open-mcp/SKILL.md", "written": true, "existed": false }
  ],
  "_source": "local"
}
```

When `write: false` (default), `written` is an empty array and the skill content is returned as a preview string.

## Object handle system

Live `UnityEngine.Object` values returned by `invoke_method` and `execute_csharp` are serialized as object handles (instance ID + type + fallback locators) instead of reflected JSON. This lets agents pass live objects back in subsequent tool calls:

- `invoke_method` — `object_id` parameter targets a live object for instance methods; args that are handle JSON are auto-resolved for `UnityEngine.Object` parameters.
- `execute_csharp` — `object_ids` parameter injects resolved objects as `Snippet.Refs[i]` / `Snippet.Ref<T>(i)`.

Handles include fallback locators (`path`, `assetPath`, `assetGuid`, `gameObjectPath`) so they degrade gracefully after domain reload. See [bridge-http.md](bridge-http.md#object-handles) for the wire format and resolution priority.

## Route policy

Route selection is implemented in `mcp-server/src/tool-router.ts`.

- `unity_open_mcp_list_assets`: always offline route.
- `unity_open_mcp_capabilities`: always local route (static catalog).
- `unity_agent_generate_skill`: always local route (reads project files from disk).
- `unity_open_mcp_find_references`: live when available, otherwise offline reader.
- `unity_open_mcp_read_asset` and `unity_open_mcp_search_assets`: compressible router with offline-first behavior and live fallback.
- `unity_open_mcp_compile_check`: **always** routes to batch (a fresh Unity recompiling from scratch), even when the live bridge is connected — running it against an Editor that already compiled would never surface a broken build. Response `_route.fallbackReason` is `"compile_check_always_batch"`.
- Other tools:
  - prefer live bridge when connected,
  - use batch fallback only for tools in batch-eligible set,
  - return batch-style ping result when live is unavailable and tool is `unity_open_mcp_ping`.

Tool responses include route metadata under `_route`:
- live: `{ route: "live" }`
- batch fallback: `{ route: "batch", fallbackReason: "live_unavailable" }`
- compile check: `{ route: "batch", fallbackReason: "compile_check_always_batch" }`

## Batch support

Batch tool allow-list is defined by `BATCH_TOOL_NAMES` in `mcp-server/src/batch-spawn.ts`.

Supported operations:
- `unity_open_mcp_scan_all`
- `unity_open_mcp_baseline_create`
- `unity_open_mcp_regression_check`
- `unity_open_mcp_find_members`
- `unity_open_mcp_compile_check` — headless compile check; **always** routes to batch (spawns a fresh Unity that recompiles from scratch), even when the live bridge is available. Returns structured compiler errors (`status`, `errorCount`, `errors[]` with `code`/`file`/`line`/`message`). When the bridge assembly itself fails to compile, the JSON markers never print — batch-spawn then extracts `error CSxxxx` lines from the Unity log and surfaces them in the rejection so every batch tool self-diagnoses a broken build.

Recognized but intentionally blocked in batch mode (`batch_not_supported`):
- `unity_open_mcp_execute_csharp`
- `unity_open_mcp_invoke_method`
- `unity_open_mcp_execute_menu`

Batch runtime requirements:
- `UNITY_PATH` set to Unity executable.
- `UNITY_PROJECT_PATH` set to project root.

## Offline/compressible reads

`mcp-server/src/compressible-router.ts` handles:
- `unity_open_mcp_read_asset`
- `unity_open_mcp_search_assets`

Behavior:
- Parse text-serialized assets offline first.
- Fall back to live bridge for binary formats or offline parse failures.
- Return source marker (`_source: "offline"` or `_source: "live"`).
- `read_asset` uses LRU model cache and returns cache marker (`_cache: "hit" | "miss"`).

## Tool naming and contract notes

- Tool names use `unity_open_mcp_*`.
- Input schema and descriptions live in each tool file under `mcp-server/src/tools/`.
- Errors are returned as JSON text payloads with `error.code` and `error.message`.

## Source-of-truth files

- `mcp-server/src/index.ts`
- `mcp-server/src/tools/index.ts`
- `mcp-server/src/tool-router.ts`
- `mcp-server/src/batch-spawn.ts`
- `mcp-server/src/compressible-router.ts`
- `mcp-server/src/capabilities/build-capabilities.ts`
- `mcp-server/src/skill/generate-skill.ts`
- `mcp-server/src/skill/client-paths.ts`
- `skills/client-paths.json` — single source of truth for project-relative skill install paths and the MCP-client → skill-target mapping (consumed by both the Hub wizard and `unity_agent_generate_skill`).
