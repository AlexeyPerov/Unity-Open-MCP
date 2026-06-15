# Architecture

## Monorepo overview

`Unity-AI-Hub` is organized as a multi-area workspace with a desktop client, bridge/runtime pieces, and server-side integration components.

## Top-level boundaries

- `hub/`: desktop UI application and local orchestration experience.
- `packages/`: reusable packages, including bridge logic and tests.
- `mcp-server/`: MCP-facing server implementation.
- `demo/`: demo assets and local end-to-end scenarios.
- `templates/`: reusable repository and workflow templates.
- `references/`: imported external references for comparative analysis.

## Runtime interaction model

- The Hub is the main operator-facing surface.
- Bridge components host an HTTP server inside Unity Editor and execute tool dispatch on the main thread.
- MCP server components expose tools/resources over stdio and route calls to live bridge, batch Unity, or offline readers.
- Shared contracts stay explicit at package boundaries to avoid hidden coupling.

## Main execution paths

### Live route (Editor connected)

1. MCP client calls tool on stdio server.
2. `mcp-server/src/tool-router.ts` routes to live bridge when available.
3. Bridge endpoint `/tools/{name}` dispatches in Unity (`packages/bridge/Editor/Bridge/BridgeHttpServer.cs`).
4. Response returns through MCP server to client with `_route: { route: "live" }`.

### Fallback route (Editor unavailable)

1. Router checks live availability and detects no live bridge.
2. Tools with headless support route to batch (`mcp-server/src/batch-spawn.ts`), with `_route: { route: "batch", fallbackReason: "live_unavailable" }`.
3. Offline-capable read tools use local parsers (`mcp-server/src/offline.ts`, `mcp-server/src/compressible-router.ts`).
4. Unsupported batch-only operations return typed errors (`batch_not_supported`, `unknown_batch_tool`, and related error codes).

## Ownership map

| Area | Primary ownership |
|---|---|
| Bridge HTTP endpoints and in-Editor dispatch | `packages/bridge/Editor/Bridge/` |
| Typed Unity tool registration | `packages/bridge/Editor/Bridge/Registry/` |
| MCP stdio server and tool/resource registry | `mcp-server/src/index.ts`, `mcp-server/src/tools/`, `mcp-server/src/resources/` |
| Offline asset reads and compact drill-down | `mcp-server/src/offline*.ts`, `mcp-server/src/compressible-router.ts` |

## Source-of-truth files

- Server bootstrap and env requirements: `mcp-server/src/index.ts`
- Route policy and fallback logic: `mcp-server/src/tool-router.ts`
- Batch spawn behavior and supported batch tools: `mcp-server/src/batch-spawn.ts`
- Bridge listener endpoints and gate envelopes: `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`

## Update triggers

Update this document when:
- package boundaries or ownership change,
- route selection logic changes (live/batch/offline),
- bridge transport endpoints or envelope flow changes.
