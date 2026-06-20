# MCP Tools API

This page summarizes the MCP tool surface exposed by `unity-open-mcp`.

For exact schemas, see tool files in `mcp-server/src/tools/` and use `unity_open_mcp_capabilities`.

## Tool families

- **Core runtime**: ping, C# execution, method invoke, menu calls, reflection, compile checks, editor status.
- **Gate and validation**: validate edit, checkpoints, deltas, reference scan, path scan, regression baseline/check, fixes.
- **Asset intelligence**: reserialize, read/search/list assets.
- **Agent senses**: tests, screenshots, console read, profiler capture, memory/rendering snapshots, spatial queries, event pull.
- **Typed editor surface**: scenes, GameObjects, components, packages, profiler session controls, build/project settings, script/object helpers.
- **Extension packs**: navigation, input system, probuilder, particle system, animation.
- **Discovery utilities**: capabilities, rules list, skill generation.

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
- `tools[].routePolicy`
- `tools[].batchCapable`
- `tools[].inputSchema`

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
  - `unity_open_mcp_capabilities` and `unity_open_mcp_generate_skill` are local.

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
