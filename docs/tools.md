# Tools and Dependencies

This document tracks primary technologies, scripts, and runtime requirements used across the repository.

## By area

| Area | Primary tools | Notes |
|---|---|---|
| Hub app (`hub/`) | Tauri 2, Svelte 5, SvelteKit, Vite, TypeScript | Desktop UI, local command bridge, and frontend runtime. |
| Bridge (`packages/bridge/`) | Unity C# editor/runtime APIs, reflection-based registry | Typed dispatch and Unity-side bridge behavior. |
| Verify package (`packages/verify/`) | Unity C# rule engine and batch entrypoints | Health/rule checks used by gate and batch workflows. |
| MCP server (`mcp-server/`) | Node.js, TypeScript, `@modelcontextprotocol/sdk` | MCP stdio transport and server-side tool/resource exposure. |
| Automation/templates (`templates/`) | GitHub Actions templates | Reusable CI and workflow automation building blocks. |

## Package scripts

### `hub/package.json`

- `npm run dev` - start Vite dev server.
- `npm run build` - production build.
- `npm run check` - Svelte + TypeScript checks.
- `npm run test` - Node test runner for `src/lib/**/*.test.ts`.
- `npm run tauri` - Tauri CLI entrypoint.

### `mcp-server/package.json`

- `npm run build` - compile TypeScript (`dist/`).
- `npm run typecheck` - TypeScript no-emit validation.
- `npm run test` - Node test runner for `src/**/*.test.ts`.

## Runtime environment variables

### MCP server

Required:
- `UNITY_PROJECT_PATH` - absolute path to Unity project root.

Optional:
- `UNITY_OPEN_MCP_BRIDGE_PORT` - live bridge port, default `19120`.
- `UNITY_PATH` - Unity executable path for batch fallback tools.
- `UNITY_OPEN_MCP_BATCH_TIMEOUT_MS` - batch timeout override.

### Bridge (Unity Editor process)

- `UNITY_OPEN_MCP_BRIDGE_PORT` can override default listener port.
- Unity command-line argument `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>` also overrides port.

## Key external dependencies

- `@modelcontextprotocol/sdk` in `mcp-server` for MCP server/tool/resource protocol types and stdio transport.
- Tauri dependencies in `hub` (`@tauri-apps/api`, `@tauri-apps/cli`, plugins) for desktop runtime.
- Svelte/SvelteKit/Vite stack in `hub` for frontend runtime and build pipeline.

## Source-of-truth files

- `hub/package.json`
- `mcp-server/package.json`
- `packages/bridge/package.json`
- `packages/verify/package.json`
- `mcp-server/src/index.ts`
- `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`

## Update triggers

Update this file when a major dependency or toolchain decision changes (additions, removals, or version-family shifts).
