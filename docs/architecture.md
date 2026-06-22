# Architecture

Unity Open MCP has four runtime parts:

- **Unity project** with bridge and verify packages installed.
- **Bridge** running inside Unity Editor over local HTTP.
- **MCP server** exposing tools/resources over stdio to AI clients.
- **Unity Hub Pro** for setup and project operations.

## Repository map

- `mcp-server/` — MCP stdio server, tool registry, routing.
- `packages/bridge/` — Unity HTTP bridge and typed tool handlers. Shipped domain tools live under `Editor/TypedTools/Extensions/` and compile-gate on their Unity dependency (see [Extensions](extensions.md)).
- `packages/verify/` — validation rules and fixes used by gate flows.
- `hub/` — desktop app (Tauri + SvelteKit).

## Runtime flow

1. AI client calls an MCP tool.
2. MCP server chooses route policy.
3. Call goes to:
   - live bridge (preferred), or
   - batch Unity fallback (supported tools), or
   - offline/local readers (supported tools).
4. Response includes route metadata.

## Route types

- `live` — Unity Editor bridge is running and reachable.
- `batch` — headless Unity fallback for a supported subset.
- `offline` — disk readers for selected asset/tool operations.
- `local` — no Unity call required (catalog-style operations).

## Core source files

- `mcp-server/src/index.ts`
- `mcp-server/src/tool-router.ts`
- `mcp-server/src/batch-spawn.ts`
- `mcp-server/src/offline.ts`
- `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- `packages/bridge/Editor/Bridge/BridgeInstanceLock.cs`

## Related docs

- [MCP tools API](api/mcp-tools.md)
- [Bridge HTTP API](api/bridge-http.md)
- [Extensions](extensions.md)
