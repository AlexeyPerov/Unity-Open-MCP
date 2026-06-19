# Tools and Dependencies

This document tracks primary technologies, scripts, and runtime requirements used across the repository.

## By area

| Area | Primary tools | Notes |
|---|---|---|
| Hub app (`hub/`) | Tauri 2, Svelte 5, SvelteKit, Vite, TypeScript | Desktop UI, local command bridge, and frontend runtime. The Rust backend (`hub/src-tauri/`) shells out to the system `git` for read-only status and uses `libc` for process-group kill in the Open-MCP command runner. |
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
- `npm run build:test` - compile tests + source to `dist-test/` (`tsconfig.test.json`).
- `npm run typecheck` - TypeScript no-emit validation.
- `npm run test` - build tests to `dist-test/` then run the Node test runner against the compiled `*.test.js`. Tests import sibling modules with `.js` specifiers, so they run against compiled output rather than via type-stripping.

## Runtime environment variables

### MCP server

Required:
- `UNITY_PROJECT_PATH` - absolute path to Unity project root. Optional when a bridge instance lock exists for the project (the lock records the path); required otherwise.

Optional:
- `UNITY_OPEN_MCP_BRIDGE_PORT` - override the live bridge port. When unset, the port is the per-project deterministic hash `20000 + (sha256(UNITY_PROJECT_PATH) % 10000)`, discovered at startup from `~/.unity-open-mcp/instances/<sha256(projectPath)>.json` when a live bridge lock exists. `19120` is a legacy backward-compat pin only.
- `UNITY_PATH` - **optional** Unity executable path for batch fallback tools. When unset, the MCP server auto-discovers Unity from the OS-default Hub install paths (macOS `/Applications/Unity/Hub/Editor`, Windows `C:\Program Files\Unity\Hub\Editor`, Linux `~/Unity/Hub/Editor`). On multi-version machines discovery matches the running bridge's `unityVersion` when known, else picks the newest. Set this only to force a specific editor.
- `UNITY_HUB` - **optional** override for the Hub install root scanned by Unity auto-discovery (use when Unity is installed outside the OS-default path). Ignored when `UNITY_PATH` is set.
- `UNITY_OPEN_MCP_BATCH_TIMEOUT_MS` - batch timeout override.
- `UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS` - set to `1` to disable auto-dismissal of Unity's "compile errors at launch" / Safe Mode dialog (enabled by default).
- `UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS` - overall budget (ms) for a launch-dialog dismiss pass (default `30000`).
- `UNITY_OPEN_MCP_DISMISS_INTERVAL_MS` - poll interval (ms) for launch-dialog probes (default `1500`).

### Bridge (Unity Editor process)

- `UNITY_OPEN_MCP_BRIDGE_PORT` can override default listener port.
- Unity command-line argument `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>` also overrides port.

## Testing

### Running tests locally

- **Hub (`hub/`)** — `npm test` runs `node --test` with `--experimental-strip-types` over `src/lib/**/*.test.ts` (pure-TS modules only; component tests are out of scope). Requires Node >= 22.6. Also `npm run check` for Svelte + TypeScript validation.
- **MCP server (`mcp-server/`)** — `npm test` compiles the suite to `dist-test/` (`tsconfig.test.json`) then runs `node --test` over the compiled `*.test.js`. `npm run typecheck` validates types without emitting.
- **Unity packages (`packages/bridge`, `packages/verify`)** — open `demo/` in Unity and run the Test Runner (Window → General → Test Runner → EditMode). The demo declares both packages in `Packages/manifest.json` `testables`, so their `*.Editor.Tests` assemblies are discovered. The demo's own `Demo.Tests.EditMode`/`PlayMode` assemblies (including the intentionally-failing sanity test) are excluded from CI runs.

### CI

Two workflows run on pull requests and pushes to `main`:

| Workflow | Runner | What it does |
|---|---|---|
| `.github/workflows/typescript.yml` | GitHub-hosted Ubuntu, Node 22 | `npm ci`, `npm run check` + `npm test` for `hub/`; `npm ci`, `npm run typecheck` + `npm test` for `mcp-server/`. No Unity license needed. |
| `.github/workflows/unity-open-mcp-verify-demo.yml` | Self-hosted `unity` runner(s) | Verify batch scan (regression check + scan-all) against `demo/`, **plus** a `run-tests` job that runs the Unity Test Runner in EditMode against the two package test assemblies. Each job runs on a **matrix** of supported editors (`unity-6`, `unity-2022-lts`) resolved from repo variables `UNITY_PATH_UNITY_6` / `UNITY_PATH_UNITY_2022_LTS` — see [Unity version compatibility](unity-version-compat.md). The `-runTests` step also serves as the project compilation check: Unity must import and compile the full `demo/` project (and both packages) before any test runs. |

The Unity test job targets only `com.alexeyperov.unity-open-mcp-bridge.Editor.Tests` and `com.alexeyperov.unity-open-mcp-verify.Editor.Tests` via `-testAssembly`, so the demo's intentionally-failing sanity test stays available for manual / `unity_senses_run_tests` demonstration.

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
