# Unity Open MCP — Memory Profiler Extension

Skill for AI agents capturing Unity Memory Profiler snapshots
(`com.unity.memoryprofiler`) in a Unity project through the `unity-open-mcp`
MCP server.

> This domain is **embedded** in the bridge, **compile-gated** on
> `com.unity.memoryprofiler`, and **auto-activating**. Its tool compiles in
> only when the project has `com.unity.memoryprofiler` installed (the bridge
> sets the `UNITY_OPEN_MCP_EXT_MEMORYPROFILER` define automatically). When the
> package is present, the `memoryprofiler` group **activates automatically** for
> the session — the tool appears in `ListTools` with no manual `manage_tools`
> call.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The project has `com.unity.memoryprofiler` installed. If `capabilities`
  reports the `memoryprofiler` group as `available: false`, install the package
  (Package Manager → Unity Registry → Memory Profiler) and let the bridge
  recompile.
- The `memoryprofiler` group is **auto-activated** when the package is present —
  check `unity_open_mcp_manage_tools(action="list_groups")`; the group should
  show `activationSource: "auto"`. If you deactivated it manually, re-activate
  with `manage_tools(action="activate", group="memoryprofiler")`.

## Tool

`unity_senses_memory_snapshot_capture` — capture a Memory Profiler snapshot to
a `.snap` file. (Sense-prefixed because it pairs with the existing senses
profiler family rather than the typed-editor surface.)

## Vocabulary

A **Memory Profiler snapshot** (`.snap` file) is an offline capture of the
*entire* managed + native heap at a moment in time — far deeper than the live
`unity_senses_profiler_memory` allocator-bytes read. It can be opened in the
Memory Profiler window (Window > Analysis > Memory Profiler) for root-cause
analysis of leaks, large allocations, and asset footprint.

## Version-stability note

The Memory Profiler capture surface moved namespaces across Unity versions:

- new (Unity 2023.1+/6000.0): `Unity.Profiling.Memory.MemoryProfiler` (engine
  core — ships without the package, but the `.snap` loader UI needs the
  package, which is why the tool is compile-gated on it).
- legacy: `UnityEditor.MemoryProfiler.MemoryProfiler` /
  `Profiling.Memory.Experimental.MemoryProfiler` (the package).

The tool reflects over whichever surface is present at call time. The capture is
**callback-based (async)** — `TakeSnapshot` returns before the `.snap` is
written; the tool blocks (bounded by `timeout_ms`, pumping editor updates so the
callback can fire) until the callback reports completion. When the API cannot be
reached (version mismatch / internal rename), the tool returns a structured
`memoryprofiler_api_unavailable` error.

## Canonical workflow: capture + correlate

1. **Stabilize the scene** — the snapshot reflects the moment of capture, so
   reach the state you want to profile (a specific scene, a frame after load,
   etc.) before calling. The tool's `EditorSettle` lifecycle waits for the editor
   to settle, but it does not advance frames.

2. **Capture** — `unity_senses_memory_snapshot_capture`. `output_path` is
   optional — when omitted the snapshot is written to a **temp path**
   (snapshots can be hundreds of MB+, so the default avoids writing into
   `Assets/`). Pass an `Assets/.../*.snap` path only when you want to persist
   the file in the project. The response reports the concrete path, file size,
   and whether it landed inside `Assets/`.

3. **Correlate with CPU/frame context** — pair the snapshot with the existing
   profiler family for a fuller picture:
   - `unity_open_mcp_profiler_get_script_stats` — per-script CPU overhead.
   - `unity_senses_profiler_capture_frame` — single-frame deep CPU capture.
   - `unity_senses_profiler_memory` — live allocator bytes (a snapshot of the
     *now*, not a `.snap` for offline inspection).

4. **Hand off to the human** — the `.snap` is best analyzed in the Memory
   Profiler window by a human; report the captured path so the operator can
   open it.

## Common recipes

### Quick capture (temp path)

```
unity_senses_memory_snapshot_capture
```

Returns a temp `.snap` path + file size. Hand the path to the operator to open
in the Memory Profiler window.

### Persist in the project

```
unity_senses_memory_snapshot_capture
  output_path: "Assets/MemorySnapshots/LoadPeak.snap"
```

The snapshot lands inside `Assets/` and is imported so it shows up in the
Project window. Useful when you want the snapshot committed alongside the
project.

### Debug a memory spike

1. `unity_senses_profiler_capture_frame` for the frame that spiked — read the
   CPU call tree to find what ran.
2. `unity_senses_memory_snapshot_capture` immediately after — capture the
   resulting heap state.
3. Correlate: the CPU capture tells you *what ran*, the `.snap` tells you *what
   stayed allocated*.

## Failure modes

| Error code | Meaning | Recovery |
|---|---|---|
| `memoryprofiler_api_unavailable` | The capture surface could not be reached (version mismatch / internal rename). | Capture manually from the Memory Profiler window (Window > Analysis > Memory Profiler). |
| `memoryprofiler_capture_timeout` | The callback did not fire within `timeout_ms`. | Increase `timeout_ms` (up to 300000), or capture manually. Large heaps take longer. |
| `memoryprofiler_capture_failed` | The capture threw or the callback reported failure. | Check the Unity console for the underlying error; retry or capture manually. |

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `unity_senses_memory_snapshot_capture` | no (read-only, produces a file) | editor_settle | Capture a `.snap` via the Memory Profiler package API. Gate = Off. |

The capture blocks until the snapshot file is written (bounded by `timeout_ms`,
default 60000, max 300000). When the package version exposes a different capture
surface, the tool returns `memoryprofiler_api_unavailable` — capture manually
from the Memory Profiler window.
