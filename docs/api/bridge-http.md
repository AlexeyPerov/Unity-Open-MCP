# Bridge HTTP API

Unity bridge HTTP endpoints are served by `packages/bridge/Editor/Bridge/BridgeHttpServer.cs` on `127.0.0.1`.

## Endpoint summary

| Endpoint | Method | Purpose |
|---|---|---|
| `/ping` | `GET` | Bridge/editor health snapshot. |
| `/tools/{toolName}` | `POST` | Execute bridge tool by name. |
| `/resources` | `GET` | List bridge-registered resources. |
| `/resources/{route}` | `GET` | Read one bridge resource payload. |

## Listener and port

- Default bind address: `127.0.0.1`
- Default port: `19120`
- Port overrides:
  - env var `UNITY_OPEN_MCP_BRIDGE_PORT`
  - Unity arg `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`

## `/ping` response

Success payload fields:
- `connected`
- `projectPath`
- `unityVersion`
- `bridgeVersion`
- `mode`
- `compiling`
- `isPlaying`

If the bridge session is not initialized yet, endpoint may return `503` with a fallback payload where `connected` is `false`.

## `/tools/{toolName}` behavior

### Dispatch flow

1. Validate HTTP method (`POST` required).
2. Check tool exists in built-in tool set or `BridgeToolRegistry`.
3. Execute on Unity main thread via dispatcher.
4. Return JSON payload envelope.

### Common tool-level error codes

- `tool_not_found`
- `method_not_allowed`
- `paths_hint_required` (mutating tool without scope hints)
- `timeout`
- `execution_error`
- `bridge_internal_error`

### Gate envelope behavior

Mutating tools return a gate-aware envelope with:
- `mutation` (success/output/error)
- `gate` (mode/checkpoint/validation/delta/skipped flags)
- `agentNextSteps` (actionable guidance)

Non-mutating direct-response tools return tool payloads directly (or direct error JSON).

## Known built-in tool names

- `unity_open_mcp_execute_csharp`
- `unity_open_mcp_invoke_method`
- `unity_open_mcp_execute_menu`
- `unity_open_mcp_find_members`
- `unity_open_mcp_validate_edit`
- `unity_open_mcp_checkpoint_create`
- `unity_open_mcp_delta`
- `unity_open_mcp_find_references`
- `unity_open_mcp_scan_paths`
- `unity_open_mcp_apply_fix`
- `unity_open_mcp_reserialize`
- `unity_open_mcp_read_asset`
- `unity_open_mcp_search_assets`

Typed tools discovered via `BridgeToolRegistry` are also callable through `/tools/{toolName}`.

## `/resources` and `/resources/{route}`

- `GET /resources` returns list of registered resource metadata: `name`, `route`, `mimeType`, `description`.
- `GET /resources/{route}` executes the mapped resource provider and returns content with provider mime type.
- Resource errors use JSON envelope with codes such as `resource_not_found` and `execution_error`.

## Source-of-truth files

- `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- `packages/bridge/Editor/Bridge/Registry/BridgeToolRegistry.cs`
- `packages/bridge/Editor/Bridge/Registry/BridgeResourceRegistry.cs`
