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
- `unity_agent_run_tests` (test runner; requires Unity Test Framework)
- `unity_agent_screenshot` (scene/game/isolated screenshots)
- `unity_agent_read_console` (console log reader)

Typed tools discovered via `BridgeToolRegistry` are also callable through `/tools/{toolName}`.

### Test runner (`unity_agent_run_tests`)

Direct-response tool that starts an async Unity test run and returns `{ "status": "started", "runId": "...", "mode": "EditMode|PlayMode" }`.

- Results are written to `~/.unity-agent/test-results-<runId>.json` when the run completes.
- PlayMode runs survive domain reload: a pending marker file (`test-pending-<runId>.json`) is written before the run, and `TestRunnerState` re-attaches callbacks after reload.
- The MCP server polls the results file and returns structured pass/fail counts to the caller.
- Lives in a separate assembly (`com.alexeyperov.unity-open-mcp-bridge.TestRunner.Editor`) that is conditionally compiled only when `com.unity.test-framework` is installed.

### Screenshots (`unity_agent_screenshot`)

Direct-response tool that captures a PNG screenshot and returns the saved file path.

- `view: "scene"` — renders the last active Scene view camera.
- `view: "game"` — renders the main game camera (or first camera found).
- `view: "isolated"` — renders a single GameObject in a 2×2 composite (Front/Right/Back/Top) with layer culling, configurable background (transparent/solid/skybox), and guaranteed state restore.
- Output is written to `~/.unity-agent/screenshots/screenshot-<view>-<timestamp>.png`.
- Parameters: `view`, `width` (default 1280), `height` (default 720), `object_path` (required for isolated), `background` (default skybox).

### Console reader (`unity_agent_read_console`)

Direct-response tool that reads Unity console entries via reflection on internal `LogEntries`.

- Returns structured entries: `{ type, message, stack }` with summary counts.
- Filter by `type`: `error` | `warning` | `log` | `all` (default).
- `include_unity_frames` (default false) controls whether UnityEngine/UnityEditor/System stack frames are included.
- `max_entries` (default 100) caps the returned entry count; `max_stack_frames` (default 20) truncates long stack traces.
- `clear: true` empties the console after reading.

## `/resources` and `/resources/{route}`

- `GET /resources` returns list of registered resource metadata: `name`, `route`, `mimeType`, `description`.
- `GET /resources/{route}` executes the mapped resource provider and returns content with provider mime type.
- Resource errors use JSON envelope with codes such as `resource_not_found` and `execution_error`.

## Source-of-truth files

- `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- `packages/bridge/Editor/Bridge/Registry/BridgeToolRegistry.cs`
- `packages/bridge/Editor/Bridge/Registry/BridgeResourceRegistry.cs`
