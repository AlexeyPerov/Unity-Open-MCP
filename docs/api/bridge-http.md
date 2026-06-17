# Bridge HTTP API

Unity bridge HTTP endpoints are served by `packages/bridge/Editor/Bridge/BridgeHttpServer.cs` on `127.0.0.1`.

## Endpoint summary

| Endpoint | Method | Purpose |
|---|---|---|
| `/ping` | `GET` | Bridge/editor health snapshot. |
| `/instance` | `GET` | Live instance lock + heartbeat snapshot (M13). |
| `/tools/{toolName}` | `POST` | Execute bridge tool by name. |
| `/resources` | `GET` | List bridge-registered resources. |
| `/resources/{route}` | `GET` | Read one bridge resource payload. |

## Listener and port

- Default bind address: `127.0.0.1`
- Default port: **deterministic per project** — `20000 + (sha256(projectPath) % 10000)`. Two Unity projects running bridges simultaneously get two distinct ports with no configuration.
- Port overrides (both win over the deterministic default):
  - env var `UNITY_OPEN_MCP_BRIDGE_PORT`
  - Unity arg `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`
- The hash formula takes the first 8 bytes of SHA256 of the normalized project path (forward slashes, trailing slash trimmed), interprets them as a big-endian 64-bit unsigned integer, and applies `% 10000`. Both the bridge (`InstancePortResolver.ComputePort`) and the MCP server (`mcp-server/src/instance-discovery.ts`) implement it identically.

## Multi-instance discovery (M13)

Each running bridge writes a lock file at `~/.unity-agent/instances/<sha256(projectPath)>.json`. The file doubles as the heartbeat — it is rewritten every 0.5s and on every forced editor state transition (compile start, play-mode change, domain reload).

Lock / heartbeat fields:
- `pid`, `port`, `projectPath`, `projectHash`
- `startedAt`, `updatedAt`, `heartbeatAt` (ISO-8601 UTC)
- `state` — `idle` | `compiling` | `reloading` | `entering_playmode` | `playing` | `exiting_playmode`
- `isPlaying`, `isCompiling`
- `bridgeVersion`, `unityVersion`

The MCP server reads this file (no HTTP round-trip needed) to pick the right bridge port per project; stale locks whose `pid` is no longer alive are ignored and cleaned up by the next bridge that starts. `GET /instance` returns the same JSON the bridge just wrote, for clients that want to verify the live bridge against the on-disk lock.

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

## `/instance` response (M13)

Returns the live lock JSON described under [Multi-instance discovery](#multi-instance-discovery-m13). Returns `503` with `{"error":{"code":"no_instance", ...}}` when the bridge has not acquired a lock yet (listener started but lock write failed — e.g. the `~/.unity-agent/instances/` directory could not be created).

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
- `scene_dirty` (disruptive op refused because a loaded scene has unsaved changes — see [Scene dirty guard](#active-scene-dirty-guard))
- `timeout`
- `execution_error`
- `bridge_internal_error`

### Lifecycle policy

Every dispatched tool declares a **lifecycle policy** that tells the dispatcher how long to wait before returning and whether the op may survive a domain reload. The policy is surfaced in the gate response envelope as `lifecycle` (snake_case token) and `settleMs` (milliseconds the bridge blocked waiting for the editor to finish compiling).

| Policy | Token | Behaviour |
|---|---|---|
| `None` | `none` | Read-only, returns immediately. No settle wait. |
| `EditorSettle` | `editor_settle` | Mutating; the bridge waits for asset refresh/serialization to finish (cap ~5s) before returning. |
| `RestartThenSettle` | `restart_then_settle` | Mutating; may trigger a domain reload. The bridge blocks until the editor finishes compiling (cap ~60s) so the caller never observes a half-compiled state. The HTTP listener survives the reload, so a follow-up `/ping` reflects the post-reload state automatically. |
| `CustomConfirmation` | `custom_confirmation` | Async; returns immediately and the result arrives via an external completion signal (e.g. `run_tests` file-handoff poll on the MCP server). |

Classification lives in two places that never drift: the `[BridgeTool(Lifecycle = ...)]` attribute for registry-discovered tools, and `ToolLifecycle.Map` for the legacy hardcoded meta-tools. Unknown tools default to `None` (read-only safe default).

Tool → policy assignment:

| Policy | Tools |
|---|---|
| `none` | `ping`, `find_members`, `validate_edit`, `checkpoint_create`, `delta`, `find_references`, `scan_paths`, `read_asset`, `search_assets`, `list_assets`, `editor_status`, `read_console`, `screenshot`, `profiler_capture`, `profiler_memory`, `profiler_rendering`, `spatial_query` |
| `editor_settle` | `apply_fix`, `reserialize` |
| `restart_then_settle` | `execute_csharp`, `invoke_method`, `execute_menu`, `compile_check` |
| `custom_confirmation` | `run_tests` |

### Active-scene dirty guard

Before any `restart_then_settle` op, the bridge preflights the loaded scenes via `EditorSceneManager.GetSceneManagerSetup()`. If any scene has unsaved changes (`isDirty`), the call is **refused** so Unity's native save modal never interrupts the flow:

```json
{
  "mutation": {
    "success": false,
    "output": null,
    "error": {
      "code": "scene_dirty",
      "message": "Active scene has unsaved changes (dirty): Assets/Scenes/Main.unity. ..."
    }
  },
  "gate": { "mode": "enforce", "skipped": true, "validation": null, "delta": null },
  "lifecycle": "restart_then_settle",
  "settleMs": 0,
  "dirtyScenes": ["Assets/Scenes/Main.unity"],
  "agentNextSteps": [
    "Save or discard changes to the dirty scene(s) before retrying: Assets/Scenes/Main.unity.",
    "To save via the bridge: unity_open_mcp_execute_csharp with EditorSceneManager.SaveScene(...).",
    "To discard: EditorSceneManager.RestoreSavedSceneState(), or retry with ignore_scene_dirty: true."
  ]
}
```

Recover by saving the scene first, discarding, or passing `ignore_scene_dirty: true` on `execute_csharp` / `invoke_method` / `execute_menu` to proceed and accept the risk of a native save prompt. The guard is **not** applied to `apply_fix` / `reserialize` (they never trigger the native save modal).

### Gate envelope behavior

Mutating tools return a gate-aware envelope with:
- `mutation` (success/output/error)
- `gate` (mode/checkpoint/validation/delta/skipped flags)
- `lifecycle` — the resolved lifecycle policy token (see [Lifecycle policy](#lifecycle-policy))
- `settleMs` — milliseconds the bridge blocked waiting for the editor to finish compiling (0 when no settle wait ran)
- `dirtyScenes` — present (array of scene paths) only when the active-scene dirty guard refused the op; `null` otherwise
- `agentNextSteps` (actionable guidance)

Non-mutating direct-response tools return tool payloads directly (or direct error JSON); they do not carry the gate/lifecycle envelope.

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
- `unity_agent_profiler_capture` (profiler frame hierarchy)
- `unity_agent_profiler_memory` (memory allocator stats)
- `unity_agent_profiler_rendering` (rendering environment stats)
- `unity_agent_spatial_query` (physics raycast / overlap / bounds / ground / nearest)

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

### Profiler capture (`unity_agent_profiler_capture`)

Direct-response tool that reads the Unity Profiler frame hierarchy via `ProfilerDriver.GetHierarchyFrameDataView` (requires the Profiler to be enabled and to have captured frames).

- Single-frame mode (default): returns `{ itemId, name, totalMs, selfMs, calls, children? }` for the root level.
- Drill-down: `parent` (an `itemId` from a previous response, same frame only) or `root` (recursive case-insensitive name substring).
- Averaging: set `from_frame`/`to_frame` or `frames` (last N) to switch to averaged flat-by-name mode with `avgTotalMs`/`avgSelfMs`/`avgCalls`/`appearedIn`.
- Token-bounded via `depth` (1 = one level, 0 = unlimited), `min_ms`, `max_items` (default 30), and `sort` (`total`/`self`/`calls`).
- Returns `profiler_empty` / `frame_out_of_range` / `no_frame_data` / `root_not_found` / `no_frames_in_range` error codes as appropriate.

### Profiler memory (`unity_agent_profiler_memory`)

Direct-response tool that snapshots live memory allocator stats.

- Returns raw byte counts (`allocatedBytes`, `reservedBytes`, `unusedReservedBytes`, `tempAllocatorBytes`, `managedHeapBytes`) plus a `humanReadable` block.
- `gc_collect: true` runs a full GC (with finalizers) before sampling.

### Profiler rendering (`unity_agent_profiler_rendering`)

Direct-response tool that snapshots the rendering environment (no parameters).

- `system` — GPU name/vendor/version, device type, VRAM (MB), processor, OS.
- `renderPipeline` — active `RenderPipelineAsset` type name (URP/HDRP) or `Built-in Render Pipeline`.
- `screen` — width/height/dpi/fullScreen, current resolution + refresh rate.
- `quality` — quality level/name, vSync, pixel lights, anti-aliasing, shadow cascades, soft shadows.
- `application` — target frame rate, run in background, is playing, Unity version.
- `time` — frame count, rendered frame count, time scale, realtime since startup.

### Spatial query (`unity_agent_spatial_query`)

Direct-response tool that runs physics-based spatial queries against the current scene (live-only; requires a loaded scene).

- `action: raycast` — `origin` + `direction` (`"x,y,z"` strings); returns `hit`, and on hit `point`/`normal`/`distance` plus the hit object's `instanceId`/`gameObject`/`path`/`collider`.
- `action: overlap` — volume query by `shape` (`sphere`/`box`/`capsule`) centered on `center`; returns `hits[]` with `instanceId`/`gameObject`/`path`/`collider`/`distance`. `half_extents` (box) and `end` (capsule) configure the shape.
- `action: bounds` — combined world AABB of a target object; returns `center`/`extents`/`size`/`min`/`max` (and `empty` if no renderers/colliders).
- `action: ground_check` — cast downward (override with `direction`) from a target object or a `point` to find the surface below; returns `hit` and on hit `point`/`normal`/`distance` plus `surface`/`surfaceId`/`surfacePath`.
- `action: nearest` — closest objects to a target object or a `point`; returns `objects[]` sorted by distance with `instanceId`/`name`/`path`/`distance`/`position`. Filter via `component` (type name) and `tag`; cap via `max` (default 5).
- Targets are addressed in priority order: `instance_id`, `path` (`"Root/Child"`), `name` (first match).
- Physics queries (`raycast`/`overlap`/`ground_check`) hit Colliders only; `bounds`/`nearest` also see render-only objects. `layer` (name) restricts physics to one layer; `query_triggers` includes trigger colliders.
- Error codes: `unknown_action`, `missing_parameter`, `bad_parameter`, `target_not_found`.

## `/resources` and `/resources/{route}`

- `GET /resources` returns list of registered resource metadata: `name`, `route`, `mimeType`, `description`.
- `GET /resources/{route}` executes the mapped resource provider and returns content with provider mime type.
- Resource errors use JSON envelope with codes such as `resource_not_found` and `execution_error`.

## Object handles

Live `UnityEngine.Object` values returned by `invoke_method` / `execute_csharp` are emitted as serializable handles (not reflected into JSON) so they survive the LLM round-trip. Each handle carries a canonical instance ID plus redundant fallback locators:

```json
{
  "objectId": 12345,
  "type": "UnityEngine.GameObject",
  "name": "Player",
  "path": "Root/Player",
  "assetPath": "Assets/Prefabs/Player.prefab",
  "assetGuid": "a1b2c3..."
}
```

- **GameObjects** include `path` (hierarchy path).
- **Components** include `gameObjectPath` and `gameObjectId` (parent locators).
- **Assets** include `assetPath` and `assetGuid`.
- Objects that are neither assets nor scene objects emit only `objectId`, `type`, `name`.

### Passing handles back to tools

- `invoke_method` accepts `object_id` (instance ID) to target a live object for instance methods instead of creating a new instance. Args that are handle JSON are auto-resolved when the target parameter type is `UnityEngine.Object`.
- `execute_csharp` accepts `object_ids` (array of instance IDs or handle JSON). Resolved objects are injected as `Snippet.Refs[index]` / `Snippet.Ref<T>(index)` in the snippet body.

### Domain-reload safety

Instance IDs are invalidated by domain reload (recompilation, enter/exit Play Mode). Resolution uses a priority fallback chain: `objectId` → `assetPath` → `assetGuid` → `path` → component-on-parent → `name`. When all locators fail, the error includes guidance to re-acquire the object via `unity_agent_scene_snapshot`, `unity_agent_spatial_query`, or `unity_open_mcp_search_assets`.

## Source-of-truth files

- `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- `packages/bridge/Editor/Bridge/Registry/BridgeToolRegistry.cs`
- `packages/bridge/Editor/Bridge/Registry/BridgeResourceRegistry.cs`
