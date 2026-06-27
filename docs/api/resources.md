# MCP Resources

Resources are registered in `mcp-server/src/resources/index.ts` and resolved by `mcp-server/src/resource-router.ts`.

## Resource URI catalog

| URI | Name | Purpose |
|---|---|---|
| `unity-open-mcp://health/summary` | Health summary | Cached verify health summary from last scan/gate validation. |
| `unity-open-mcp://health/baseline` | Health baseline | Baseline summary from baseline file on disk. |
| `unity-open-mcp://bridge/status` | Bridge status | Cached snapshot of latest successful bridge ping state. |
| `unity-open-mcp://tool-groups` | Tool groups | Static tool-group catalog (ids, descriptions, default-enabled flags). |

## Payload behavior

### `unity-open-mcp://health/summary`

- Routed via live client resource read.
- Returns JSON summary payload when data exists.
- If no data is available yet, returns a `no_data` style payload with next-step guidance.

### `unity-open-mcp://health/baseline`

- Reads baseline file from `CI/unity-open-mcp-baseline.json` under project root.
- On success (`status: "ok"`), includes:
  - `asOf`
  - `baselinePath`
  - `schemaVersion`
  - `platformProfile`
  - `summary` (`error`, `warn`, `info`)
- If missing/unreadable (`status: "no_baseline"`), includes next-step guidance to run baseline creation tool.

### `unity-open-mcp://bridge/status`

- Uses ping cache snapshot.
- On success (`status: "ok"`), includes:
  - `asOf`
  - `connected`
  - `projectPath`
  - `bridgePort`
  - `compiling`
  - `isPlaying`
- When no cached ping exists, returns:
  - `status: "no_data"`
  - `connected: false`

### `unity-open-mcp://tool-groups`

- Static catalog (no bridge round-trip).
- Returns:
  - `groups[]` — id, description, defaultEnabled, domainDefine, unityPackage per group.
  - `defaultEnabledGroups` — array containing `"core"` only.
  - `usageHint` — points at `manage_tools` for activation and `capabilities` for the per-tool roster.
- For compiled-state availability and tool rosters, call `unity_open_mcp_capabilities`; for session activation state, call `unity_open_mcp_manage_tools(action="list_groups")`.

## Unknown URI behavior

Unknown resource URIs return a JSON payload with:
- `status: "no_data"`
- `error: "Unknown resource URI: ..."`

## Source-of-truth files

- `mcp-server/src/resources/index.ts`
- `mcp-server/src/resource-router.ts`
- `mcp-server/src/ping-cache.ts`

## Related docs

- [MCP tools API](mcp-tools.md)
- [Bridge HTTP API](bridge-http.md)
- [API index](../api.md)
