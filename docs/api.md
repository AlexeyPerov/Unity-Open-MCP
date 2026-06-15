# API and Protocol Surfaces

This file is the index for external interfaces and protocol contracts exposed by this repository.

## Domain reference

| Document | Covers |
|---|---|
| `api/bridge-http.md` | Unity bridge HTTP endpoints (`/ping`, `/tools/*`, `/resources*`), envelopes, and errors. |
| `api/mcp-tools.md` | MCP tool catalog, tool families, route policy (live/batch/offline), and fallback behavior. |
| `api/resources.md` | MCP resource URIs, payload shapes, and resource router behavior. |

## Contract boundaries

- Bridge HTTP contract source: `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- MCP server routing/registry source: `mcp-server/src/index.ts`, `mcp-server/src/tool-router.ts`
- MCP tool definitions source: `mcp-server/src/tools/`
- MCP resources source: `mcp-server/src/resources/index.ts`, `mcp-server/src/resource-router.ts`

## Contract documentation guidance

- Prefer documenting behavior and payload shapes over implementation details.
- Call out breaking changes explicitly.
- Keep examples minimal and representative.

## Update triggers

Update this index when:
- a new API/protocol doc is added,
- contract ownership moves to new modules,
- endpoint or resource domains are reorganized.
