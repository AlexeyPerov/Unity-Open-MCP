# MCP Tools

MCP tools are registered in `mcp-server/src/tools/index.ts` and exposed by the stdio server in `mcp-server/src/index.ts`.

## Quick lookup

| Question | Section |
|---|---|
| Which tool names are available? | Tool catalog |
| How route selection works? | Route policy |
| Which tools can run in batch? | Batch support |
| Which tools are offline-first? | Offline/compressible reads |

## Tool catalog

### Core tools (M2 + M2.5)

- `unity_open_mcp_ping`
- `unity_open_mcp_execute_csharp`
- `unity_open_mcp_invoke_method`
- `unity_open_mcp_execute_menu`
- `unity_open_mcp_find_members`
- `unity_open_mcp_editor_status`

### Gate and validation tools (M3 + M5)

- `unity_open_mcp_validate_edit`
- `unity_open_mcp_checkpoint_create`
- `unity_open_mcp_delta`
- `unity_open_mcp_find_references`
- `unity_open_mcp_scan_paths`
- `unity_open_mcp_apply_fix`
- `unity_open_mcp_scan_all`
- `unity_open_mcp_baseline_create`
- `unity_open_mcp_regression_check`

### Asset intelligence tools (M9)

- `unity_open_mcp_reserialize`
- `unity_open_mcp_read_asset`
- `unity_open_mcp_search_assets`
- `unity_open_mcp_list_assets`

### Agent senses tools (M10)

- `unity_agent_run_tests` — EditMode + PlayMode test runner with per-test pass/fail, filter by assembly/namespace/class/method, domain-reload-safe PlayMode via file handoff.
- `unity_agent_screenshot` — Capture Scene view, Game view, or isolated 2×2 composite (Front/Right/Back/Top) of a single GameObject with layer culling. Returns saved PNG file path.
- `unity_agent_read_console` — Read Unity console entries via reflection on internal `LogEntries`. Filter by type (error/warning/log/all), user-code stack filter, optional clear, token-bounded output.
- `unity_agent_profiler_capture` — Read the Unity Profiler frame hierarchy via `ProfilerDriver.GetHierarchyFrameDataView`. Drill-down by parent ID / root name-substring / depth, multi-frame averaging, token-bounded top-N by self/total/calls.
- `unity_agent_profiler_memory` — Live memory allocator stats (allocated/reserved/unused/temp/managed heap) with optional GC first.
- `unity_agent_profiler_rendering` — Rendering environment batch: GPU/SystemInfo, active render pipeline, QualitySettings, screen resolution, target frame rate, Time stats.
- `unity_agent_spatial_query` — Physics-based spatial reasoning (raycast / overlap / bounds / ground_check / nearest) against the live scene. Targets addressed by instance_id/path/name; returns hit object instanceId/name/path.

## Object handle system

Live `UnityEngine.Object` values returned by `invoke_method` and `execute_csharp` are serialized as object handles (instance ID + type + fallback locators) instead of reflected JSON. This lets agents pass live objects back in subsequent tool calls:

- `invoke_method` — `object_id` parameter targets a live object for instance methods; args that are handle JSON are auto-resolved for `UnityEngine.Object` parameters.
- `execute_csharp` — `object_ids` parameter injects resolved objects as `Snippet.Refs[i]` / `Snippet.Ref<T>(i)`.

Handles include fallback locators (`path`, `assetPath`, `assetGuid`, `gameObjectPath`) so they degrade gracefully after domain reload. See [bridge-http.md](bridge-http.md#object-handles) for the wire format and resolution priority.

## Route policy

Route selection is implemented in `mcp-server/src/tool-router.ts`.

- `unity_open_mcp_list_assets`: always offline route.
- `unity_open_mcp_find_references`: live when available, otherwise offline reader.
- `unity_open_mcp_read_asset` and `unity_open_mcp_search_assets`: compressible router with offline-first behavior and live fallback.
- Other tools:
  - prefer live bridge when connected,
  - use batch fallback only for tools in batch-eligible set,
  - return batch-style ping result when live is unavailable and tool is `unity_open_mcp_ping`.

Tool responses include route metadata under `_route`:
- live: `{ route: "live" }`
- batch fallback: `{ route: "batch", fallbackReason: "live_unavailable" }`

## Batch support

Batch tool allow-list is defined by `BATCH_TOOL_NAMES` in `mcp-server/src/batch-spawn.ts`.

Supported operations:
- `unity_open_mcp_scan_all`
- `unity_open_mcp_baseline_create`
- `unity_open_mcp_regression_check`
- `unity_open_mcp_find_members`

Recognized but intentionally blocked in batch mode (`batch_not_supported`):
- `unity_open_mcp_execute_csharp`
- `unity_open_mcp_invoke_method`
- `unity_open_mcp_execute_menu`

Batch runtime requirements:
- `UNITY_PATH` set to Unity executable.
- `UNITY_PROJECT_PATH` set to project root.

## Offline/compressible reads

`mcp-server/src/compressible-router.ts` handles:
- `unity_open_mcp_read_asset`
- `unity_open_mcp_search_assets`

Behavior:
- Parse text-serialized assets offline first.
- Fall back to live bridge for binary formats or offline parse failures.
- Return source marker (`_source: "offline"` or `_source: "live"`).
- `read_asset` uses LRU model cache and returns cache marker (`_cache: "hit" | "miss"`).

## Tool naming and contract notes

- Tool names use `unity_open_mcp_*`.
- Input schema and descriptions live in each tool file under `mcp-server/src/tools/`.
- Errors are returned as JSON text payloads with `error.code` and `error.message`.

## Source-of-truth files

- `mcp-server/src/index.ts`
- `mcp-server/src/tools/index.ts`
- `mcp-server/src/tool-router.ts`
- `mcp-server/src/batch-spawn.ts`
- `mcp-server/src/compressible-router.ts`
