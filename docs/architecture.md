# Architecture

## Monorepo overview

`Unity-AI-Hub` is organized as a multi-area workspace with a desktop client, bridge/runtime pieces, and server-side integration components.

## Top-level boundaries

- `hub/`: desktop UI application and local orchestration experience.
- `packages/`: reusable packages, including bridge logic and tests.
  - `packages/bridge/`: core Unity Editor HTTP bridge + typed tool registry.
  - `packages/verify/`: scoped health-check verify rules + fixes.
  - `packages/extensions/`: optional domain extension packs (NavMesh, planned Input System / ProBuilder / Splines / Terrain / Tilemap / Particle System / Animation). See [extensions.md](extensions.md).
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

### Offline route (no Editor needed)

1. Text-serialized assets (`.prefab`/`.unity`/`.asset`/`.mat`/…) parse directly from disk via `mcp-server/src/offline.ts`.
2. `.meta` GUID indices and YAML parsing are rebuilt per request — no on-disk cache, so reads are always correct against current files.
3. The compressible router (`mcp-server/src/compressible-router.ts`) applies the shared compression module and returns the compact drill-down response.
4. A session-scoped in-memory `AssetModelCache` (LRU, last 8) lets drill-down flags reuse the last model without re-parsing; it is never persisted to disk.

**No-cache philosophy.** The offline-read path deliberately avoids persistent on-disk caches. GUID indices, YAML parses, and reference scans are rebuilt from current files on each request. This trades a small per-request cost for always-correct results and eliminates stale-cache invalidation bugs entirely. The only in-memory state is the session-scoped `AssetModelCache`, which is ephemeral and scoped to drill-down reuse within a single MCP session.

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
- Per-project bridge port + instance lock discovery: `mcp-server/src/instance-discovery.ts` (mirror of `packages/bridge/Editor/Bridge/InstancePortResolver.cs`)
- Batch spawn behavior and supported batch tools: `mcp-server/src/batch-spawn.ts`
- Bridge listener endpoints and gate envelopes: `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- Bridge instance lock + heartbeat: `packages/bridge/Editor/Bridge/BridgeInstanceLock.cs`, `packages/bridge/Editor/Bridge/BridgeHeartbeat.cs`

## Hub project kinds (multi-type)

The hub tracks four kinds of folder, classified automatically on add by `hub/src-tauri/src/config/project_kind.rs` with precedence Unity → Open-MCP → Package → Custom. Each kind gets a tailored list row and settings popup:

- **Unity** (`Assets/` + `ProjectSettings/`): launchable, AI Setup, render-pipeline/build-target chips, full settings popup (launch args, platform intent, env vars).
- **Open-MCP** (`mcp-server/` + root `package.json`): read-only git status, line counter, and a command runner with live streaming logs. npm commands (build / test / version / publish / custom) resolve cwd to `{project.path}/mcp-server` — the publishable package lives there, not at the repo root. A root `package.json` marker exists only so the Hub can register the checkout; npm scripts stay in `mcp-server/package.json`. The maintainer panel (`hub/src/lib/components/project-settings/OpenMcpProjectSettings.svelte`) surfaces version bump, dry-run, and publish; `hub/src-tauri/src/config/command_runner.rs::resolve_npm_cwd` centralizes the cwd rule.
- **Package** (root `package.json`): UPM manifest editor (`hub/src-tauri/src/config/upm/`), `.meta` GUID regeneration, missing-meta fixup, and an append-and-replace file migrator.
- **Custom** (any other folder): read-only git status + line counter only.

Cross-cutting surfaces for all kinds:

- **Git widget** — the branch chip in the list is clickable for every kind and opens a read-only popup showing branch, ahead/behind, and pending files. Shells out to the `git` CLI from `hub/src-tauri/src/config/git_status.rs`.
- **Line counter** — ports the LineWalker algorithm (`hub/src-tauri/src/config/line_count.rs`); auto-calculates in the git popup for small projects (threshold in app settings, default 30 MiB) and is available as an explicit "Run line count" button in every settings popup.

The hub's Rust commands are registered in `hub/src-tauri/src/lib.rs`; the frontend TS wrappers live in `hub/src/lib/services/config.ts`.

## Update triggers

Update this document when:
- package boundaries or ownership change,
- route selection logic changes (live/batch/offline),
- bridge transport endpoints or envelope flow changes.

## Agent governance (layered AGENTS.md)

`AGENTS.md` files are co-located with the code they govern. Precedence flows root → package → subtree: a deeper file may add or narrow rules for its subtree, but never silently contradicts a root rule unless that root rule explicitly allows an exception. On overlap, the deepest applicable rule is most specific.

| Layer | File | Scope |
|---|---|---|
| Root | `AGENTS.md` | Cross-cutting rules (specs/ gitignored, no migrations, docs ownership). |
| Bridge | `packages/bridge/AGENTS.md` | Bridge transport, tool registration, gate policy. |
| Verify | `packages/verify/AGENTS.md` | Verify rules (must declare issue codes), fixes, capability catalog sync. |
| Extensions | `packages/extensions/AGENTS.md` | Optional domain extension packs (NavMesh, Input System, …). |
| MCP server | `mcp-server/AGENTS.md` | Tool definitions, routing, offline-read no-cache philosophy. |
| Hub | `hub/AGENTS.md` | SvelteKit/Tauri UI, state/data, platform neutrality. |
