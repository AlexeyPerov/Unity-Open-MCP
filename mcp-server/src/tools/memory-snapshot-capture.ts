import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 7 / T20.7.3 — Memory Profiler snapshot capture. Compile-gated in
// the bridge (UNITY_OPEN_MCP_EXT_MEMORYPROFILER on com.unity.memoryprofiler) +
// auto-activating (the `memoryprofiler` group auto-activates when the package
// is present). Sense-prefixed (unity_senses_*) because it pairs with the
// existing senses profiler family rather than the typed-editor surface.
//
// Read-only re: game/project state but produces a .snap file — Gate = Off,
// ReadOnlyHint = true, Lifecycle = EditorSettle (capture can take seconds). The
// capture is callback-based (async); the bridge reflects over whichever capture
// surface the installed version exposes (Unity.Profiling.Memory.MemoryProfiler or
// UnityEditor.MemoryProfiler) and blocks until the callback fires, so the tool
// returns a definitive path/result. When the API cannot be reached, the tool
// returns a structured memoryprofiler_api_unavailable error.
//
// Pairs with profiler_get_script_stats / profiler_capture_frame for a fuller
// performance picture than a standalone memory tool.
export const memorySnapshotCapture = makeTool(
  "unity_senses_memory_snapshot_capture",
  "Capture a Memory Profiler snapshot to a .snap file using the " +
    "com.unity.memoryprofiler package API. Pairs with the existing profiler " +
    "family (profiler_get_script_stats / profiler_capture_frame) for a fuller " +
    "performance picture — capture the snapshot, then read CPU/frame context " +
    "from the profiler tools. output_path is optional — when omitted the " +
    "snapshot is written to a temp path (snapshots can be hundreds of MB+, so " +
    "the default avoids writing into Assets/). Pass an 'Assets/.../*.snap' " +
    "path to persist it in the project. Read-only re: game/project state but " +
    "produces a file — Gate = Off, ReadOnlyHint = true, Lifecycle = " +
    "EditorSettle (capture can take seconds). Requires the " +
    "com.unity.memoryprofiler package installed. The capture is callback-based; " +
    "the tool blocks until the snapshot file is written (bounded by timeout_ms). " +
    "When the package version exposes a different capture surface, the tool " +
    "returns a structured memoryprofiler_api_unavailable error — capture " +
    "manually from the Memory Profiler window (Window > Analysis > Memory " +
    "Profiler).",
  {
    properties: {
          output_path: {
            type: "string",
            description:
              "Destination .snap path. Either an 'Assets/.../*.snap' path (persists " +
              "the snapshot in the project) or an absolute path. When omitted, the " +
              "snapshot is written to a timestamped temp path (snapshots can be " +
              "hundreds of MB+, so the default avoids writing into Assets/). The " +
              ".snap extension is enforced when omitted on a supplied path — the " +
              "Memory Profiler window only opens .snap files.",
          },
          timeout_ms: {
            type: "integer",
            description:
              "Maximum milliseconds to wait for the callback-based capture to " +
              "finish (default 60000, clamped to 300000). The capture blocks until " +
              "the snapshot file is written; if it times out the tool returns " +
              "memoryprofiler_capture_timeout.",
            default: 60000,
            minimum: 1000,
            maximum: 300000,
          },
        },
  },
);
