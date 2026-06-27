# Architecture

Unity Open MCP has four runtime parts:

- **Unity project** with bridge and verify packages installed.
- **Bridge** running inside Unity Editor over local HTTP.
- **MCP server** exposing tools/resources over stdio to AI clients.
- **Unity Hub Pro** for setup and project operations.

A fifth part, the **Validation Suite**, is a standalone desktop app for guided milestone manual validation; see [validation-suite/README.md](../validation-suite/README.md).

## Repository map

- `mcp-server/` — MCP stdio server, tool registry, routing.
- `packages/bridge/` — Unity HTTP bridge and typed tool handlers. Shipped domain tools live under `Editor/TypedTools/Extensions/` and compile-gate on their Unity dependency (see [Extensions](extensions.md)).
- `packages/verify/` — validation rules and fixes used by gate flows.
- `hub/` — desktop app (Tauri + SvelteKit).
- `validation-suite/` — standalone Tauri + SvelteKit app that runs milestone validation scenarios. Engine-neutral orchestration lives in `validation-suite/packages/core/`; engine specifics (paths, CLI, companions) are declared in bundled engine profiles (`engine-profiles/unity.json`). The suite has no code boundary with `hub/` — it ships standalone and invokes the engine via the MCP CLI as a subprocess (Phase 2).

### Open-MCP npm cwd

When the Hub detects a checkout as an Open-MCP repository (`mcp-server/` directory plus a root `package.json` marker), every npm invocation in the maintainer panel runs with cwd `{repo}/mcp-server` — the publishable package, its scripts, and `files` whitelist live there, not at the repo root. The repo root `package.json` is a detection marker only. This rule is centralized in Rust (`resolve_npm_cwd` in `hub/src-tauri/src/config/command_runner.rs`) so every maintainer-panel command shares one resolution path; Unity `Package` projects keep using the project root.

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
- `mcp-server/src/compat.ts` — runtime server/bridge version-compatibility check
- `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- `packages/bridge/Editor/Bridge/BridgeInstanceLock.cs`

## Versioning

The repo tracks **two independent versions**, each from a single source file:
`version.json` (the shared trio: npm server + bridge + verify Unity packages)
and `hub/version.json` (the Unity Hub Pro app, on its own cadence). Every other
version string is **generated** by `scripts/sync-version.mjs`, which also drives
the CI drift gate (`.github/workflows/version-sync.yml`) and the release
preflight in `npm-publish.yml` / `hub-release.yml`. At runtime, the bridge
reports its version on `/ping` (`bridgeVersion`) and the server compares it
against its own in `compat.ts`, warning once if the pair is incompatible.
See [versioning.md](versioning.md) for the full policy.

## Related docs

- [MCP tools API](api/mcp-tools.md)
- [Bridge HTTP API](api/bridge-http.md)
- [Extensions](extensions.md)
