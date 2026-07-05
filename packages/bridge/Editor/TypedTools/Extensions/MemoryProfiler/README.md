# Memory Profiler — embedded domain tool

Memory Profiler typed tool (`unity_senses_memory_snapshot_capture`), embedded
inside the bridge. One tool: capture a Memory Profiler snapshot to a `.snap`
file.

## Compile gate

Two-layer gate (see `docs/contributing/extensions.md` §Embedded domain model):

1. The bridge root asmdef
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   sets `UNITY_OPEN_MCP_EXT_MEMORYPROFILER` via `versionDefines` when
   `com.unity.memoryprofiler` resolves.
2. This folder's sub-asmdef carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_MEMORYPROFILER"]` and references
   `Unity.MemoryProfiler.Editor`. Unity only compiles it when the define is set,
   so the optional package reference never breaks a project that lacks it.

Each source file additionally wraps its body in
`#if UNITY_OPEN_MCP_EXT_MEMORYPROFILER` as a belt-and-suspenders guard.

## Auto-activation (M20 Plan 7 / T20.7.0)

This domain ships with **auto-activation**: the `memoryprofiler` group activates
automatically for the session when `com.unity.memoryprofiler` is installed — no
manual `manage_tools` call required. Auto-activation is ephemeral (per session,
resets on server restart) and is additive to the manual-activation model.
Deactivate via
`unity_open_mcp_manage_tools(action="deactivate", group="memoryprofiler")` to
hide the tool.

## Reflection over the capture API

The Memory Profiler capture surface moved namespaces across Unity versions:

- new (Unity 2023.1+/6000.0): `Unity.Profiling.Memory.MemoryProfiler` (engine
  core — ships without the package, but the `.snap` loader UI needs the package,
  which is why the sub-asmdef is compile-gated on it).
- legacy: `UnityEditor.MemoryProfiler.MemoryProfiler` /
  `Profiling.Memory.Experimental.MemoryProfiler` (the package).

Both expose `TakeSnapshot(string path, Action<string,bool> callback)` (plus
optional screenshot-callback overloads). The capture is **callback-based
(async)** — `TakeSnapshot` returns before the snapshot file is written. The
`MemoryProfilerApi` reflection helper resolves whichever surface is present at
call time, invokes `TakeSnapshot` with a delegate, and blocks (bounded by a
timeout, pumping editor updates so the callback can fire) until the callback
fires — so the tool returns a definitive path/result rather than deferring. When
the API cannot be reached (version mismatch / internal rename), the tool returns
a structured `memoryprofiler_api_unavailable` error.

## Tool group

The capture tool belongs to the `memoryprofiler` group (M20 Plan 7).
Auto-activated when `com.unity.memoryprofiler` is present; otherwise hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`. The capture is read-only re: game/project state
but produces a file — `Gate = Off`, `ReadOnlyHint = true`,
`Lifecycle = EditorSettle` (capture can take seconds).

## Relationship to the profiler family

This tool **pairs** with the existing profiler family rather than duplicating
it:

- `unity_open_mcp_profiler_get_script_stats` — per-script CPU overhead.
- `unity_senses_profiler_capture_frame` — single-frame deep CPU capture.
- `unity_senses_profiler_memory` — live allocator bytes (a snapshot of the
  *now*, not a `.snap` file for offline inspection).

`unity_senses_memory_snapshot_capture` produces a `.snap` file the agent (or a
human) can open in the Memory Profiler window for offline root-cause analysis —
complementary, not overlapping.
