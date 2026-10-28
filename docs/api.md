# API and Protocol Surfaces

This file is the index for external interfaces and protocol contracts exposed by this repository.

## Domain reference

| Document | Covers |
|---|---|
| [`dialog-policy.md`](dialog-policy.md) | Startup/steady-state Unity modal auto-dismiss, `UNITY_OPEN_MCP_DIALOG_POLICY`, and related env vars. |
| [`api/bridge-http.md`](api/bridge-http.md) | Unity bridge HTTP endpoints (`/ping`, `/tools/*`, `/resources*`), envelopes, and errors. |
| [`api/mcp-tools.md`](api/mcp-tools.md) | MCP tool-family overview, example prompts, and focused-reference index. |
| [`api/tool-groups.md`](api/tool-groups.md) | Session defaults, group activation, compiled availability, and auto-activation. |
| [`api/routing-lifecycle.md`](api/routing-lifecycle.md) | Live/batch/offline/local routing, lifecycle recovery, offline coverage, and errors. |
| [`api/cli-automation.md`](api/cli-automation.md) | CLI command and automation reference; links to canonical CI behavior. |
| [`api/input-simulation.md`](api/input-simulation.md) | Input backends (legacy Input Manager / Input System), pointer delivery and `raycast_real`, touch, gestures over real frames, target by label (`target_text`), keyboard, frame stepping, and supported input boundaries. |
| [`api/playtest.md`](api/playtest.md) | UI screen snapshot (`ui_snapshot`): nodes, visibility, profiles/paging, diagnostics, and the snapshot → tap-by-label loop; Game view / Device Simulator control (`game_view`): resolution, device, orientation, safe area, settling; wait-until conditions (`playtest_wait`), frame-scheduled capture series (`capture_series`, gesture `capture_at_ms`) and the player-state sandbox (`player_state`: snapshot, diff, gated restore; project `playtest.begin` / `playtest.end` hook). |
| [`api/project-commands.md`](api/project-commands.md) | Project-owned command declaration, discovery, invocation, schemas, and safety boundaries. |
| [`api/jobs.md`](api/jobs.md) | Asynchronous job lifecycle, observation, idempotency, cancellation, retention, and adapters. |
| [`api/resources.md`](api/resources.md) | MCP resource URIs, payload shapes, and resource router behavior. |

## Contract boundaries

- Bridge HTTP contract source: `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- MCP server routing/registry source: `mcp-server/src/index.ts`, `mcp-server/src/tool-router.ts`
- MCP tool definitions source: `mcp-server/src/tools/`
- Project-command catalog and invocation source: `packages/bridge/Editor/Bridge/Registry/ProjectCommandCatalog.cs`, `packages/bridge/Editor/Bridge/Registry/ProjectCommandInvocation.cs`
- Asynchronous job orchestration source: `mcp-server/src/jobs/`
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
