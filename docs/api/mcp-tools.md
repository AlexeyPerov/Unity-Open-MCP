# MCP Tools API

This page summarizes the MCP tool surface exposed by `unity-open-mcp`.

For exact schemas, see tool files in `mcp-server/src/tools/` and use `unity_open_mcp_capabilities`.

## Tool families

- **Core runtime**: ping, C# execution, method invoke, menu calls, reflection, compile checks, editor status.
- **Gate and validation**: validate edit, checkpoints, deltas, reference scan, path scan, regression baseline/check, fixes.
- **Asset intelligence**: reserialize, read/search/list assets.
- **Agent senses**: tests, screenshots, console read, profiler capture, memory/rendering snapshots, spatial queries, event pull.
- **Typed editor surface**: scenes, GameObjects, components, packages, profiler session controls, build/project settings, script/object helpers.
- **Extension domains**: navigation, input system, probuilder, particle system, animation.
- **Discovery utilities**: capabilities, rules list, skill generation, manage_tools.

## Tool groups and session visibility

Sessions start with only the **`core`** group enabled. Every other group is hidden from `ListTools` until the agent activates it via `unity_open_mcp_manage_tools`. This keeps the prompt surface small (the full tool set is ~160 tools) and mirrors Coplay's session-visibility model.

### Groups

| Group | Default | Description |
|---|---|---|
| `core` | on | ping, execute_csharp, invoke_method, find_members, execute_menu, editor_status |
| `gate-and-verify` | off | validate_edit, checkpoint_create, delta, find_references, scan_paths, apply_fix, scan_all, baseline_create, regression_check |
| `asset-intelligence` | off | reserialize, read_asset, search_assets, list_assets |
| `typed-editor` | off | M16 Plans 1–6, 9 typed editor surface (assets, materials, shaders, prefabs, GameObjects, components, scenes, packages, console, selection, undo, tags, layers, reflection, scripts, object data) |
| `diagnostics` | off | Profiler session controls + per-frame capture/memory/rendering reads |
| `gate-intelligence` | off | impact_preview, gate_budget_estimate, mutation_explain |
| `build-settings` | off | Build pipeline + ProjectSettings reads and mutators |
| `navigation` | off | NavMesh tools — compile-gated on `com.unity.ai.navigation` |
| `input-system` | off | Input System tools — compile-gated on `com.unity.inputsystem` |
| `probuilder` | off | ProBuilder modeling tools — compile-gated on `com.unity.probuilder` |
| `particle-system` | off | Particle System tools — compile-gated on `UnityEngine.ParticleSystemModule` |
| `animation` | off | AnimationClip + AnimatorController tools — compile-gated on `com.unity.modules.animation` |
| `agent-senses` | off | run_tests, screenshot, read_console, profiler capture/memory/rendering, spatial_query (live-only) |

Always-visible meta-tools (no group assignment): `unity_open_mcp_capabilities`, `unity_open_mcp_list_rules`, `unity_open_mcp_generate_skill`, `unity_open_mcp_manage_tools`, `unity_open_mcp_pull_events` / `unity_senses_pull_events`, `unity_open_mcp_read_compile_errors`.

### manage_tools actions

```json
// List every group with active flag, description, and tool roster.
{ "action": "list_groups" }

// Activate a group — its tools appear in subsequent ListTools responses.
{ "action": "activate", "group": "navigation" }

// Deactivate a group — its tools disappear from ListTools.
{ "action": "deactivate", "group": "navigation" }

// Restore the default active set (`core` only).
{ "action": "reset" }
```

### State lifecycle

- **Ephemeral, per session.** The MCP server holds the state in memory; it is not persisted.
- **Resets to `core`-only on MCP-server restart.** Each agent session starts fresh.
- **Per-session independent.** Two concurrent agent sessions do not share activation state.
- **List-changed notifications.** The server declares `tools.listChanged: true`. When `activate`, `deactivate`, or `reset` actually changes the filtered `ListTools` surface, the server emits `notifications/tools/list_changed`. MCP clients should re-issue `tools/list` to refresh their tool descriptors (no server restart required). Idempotent activate/deactivate and no-op reset do not emit a notification.

### Compiled-state availability vs session activation

Two distinct concerns, intentionally not conflated:

- **Compiled-state availability** — whether the bridge compiled a domain in (e.g. `UNITY_OPEN_MCP_EXT_NAVIGATION` when `com.unity.ai.navigation` is installed). Reported in:
  - `unity_open_mcp_capabilities` → `toolGroups[].available` (probed from the bridge `GET /tools` endpoint when live; `null` when the bridge is offline).
  - `unity_open_mcp_manage_tools(action="list_groups")` → `groups[].available` (same probe).
- **Session activation** — whether the current session has activated the group (managed by manage_tools). Reported in `manage_tools(list_groups)` → `groups[].active`.

An agent can activate a group whose dependency is missing; its tools will appear in `ListTools` but error at call time. `capabilities` is the authoritative compiled-state source.

## Discover tools programmatically

Call `unity_open_mcp_capabilities` first.

Example:

```json
{
  "kind": "tools",
  "include_planned": true
}
```

Use the response fields:

- `tools[].name`
- `tools[].category`
- `tools[].group` — tool-group id (or null for always-visible meta-tools)
- `tools[].routePolicy`
- `tools[].batchCapable`
- `tools[].inputSchema`
- `toolGroups[]` — per-group catalog (compiled-state availability, default-enabled flag, tool roster, usage hint)

## Route policy

The router chooses one of:

- `live`: calls Unity bridge (`/tools/{name}`).
- `batch`: headless Unity fallback for supported tools.
- `offline`: local readers/parsers for selected tools.
- `local`: no Unity dependency (capabilities, catalog-style tools).

Common route behavior:

- Prefer `live` when available.
- Use `batch` only for tools marked `batchCapable`.
- Keep some tools route-pinned:
  - `unity_open_mcp_compile_check` always uses batch.
  - `unity_open_mcp_read_compile_errors` always uses offline.
  - `unity_open_mcp_capabilities`, `unity_open_mcp_generate_skill`, and `unity_open_mcp_manage_tools` are local.

## Batch support notes

Batch is intended for non-interactive scenarios and fallback operation.

- Typical batch-friendly tools: scan/regression surfaces, compile check, member lookup.
- Tools that require live editor state (for example direct C# execution) are not batch-enabled.
- Required environment for batch fallback:
  - `UNITY_PROJECT_PATH`
  - `UNITY_PATH` (when editor auto-discovery is unavailable)

## Output shaping

Many tools support output controls to reduce token usage:

- `detail`: `summary | normal | verbose`
- pagination and limits (`max_results`, `max_entries`, `max_items`, `max_nodes`, etc.)
- explicit truncation indicators in the response

## Error contract

Errors are returned as JSON with:

- `error.code`
- `error.message`

Examples: `bridge_unavailable`, `batch_not_supported`, `validation_failed`, `scene_dirty`.

## Source references

- `mcp-server/src/tools/index.ts`
- `mcp-server/src/tool-router.ts`
- `mcp-server/src/batch-spawn.ts`
- `mcp-server/src/compressible-router.ts`
- `mcp-server/src/capabilities/build-capabilities.ts`
- `mcp-server/src/capabilities/tool-groups.ts` — canonical tool-group catalog (single source of truth).
- `mcp-server/src/tool-session-state.ts` — per-session visibility store + ListTools filter.
