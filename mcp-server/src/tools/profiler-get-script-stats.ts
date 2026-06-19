import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 7 — typed script-stats read. Read-only, gate-free. Folds UMCP
// `profiler-get-script-stats`. Surfaces script execution timing from Time
// (deltaTime / fixedDeltaTime / timeScale / frameCount / realtimeSinceStartup)
// plus Mono + GC managed-heap usage — complementary to the per-frame
// profiler_capture hierarchy and the live allocator profiler_memory snapshot.
export const profilerGetScriptStats: Tool = {
  name: "unity_open_mcp_profiler_get_script_stats",
  description:
    "Snapshot script execution timing and managed-memory usage from a single call: frame time " +
    "(Time.deltaTime*1000 in ms), fixed delta time (Time.fixedDeltaTime*1000 in ms), time scale, " +
    "total frame count, realtime since startup, Mono used memory (Profiler.GetMonoUsedSizeLong " +
    "in MB), and GC total memory (GC.GetTotalMemory in MB). Read-only and gate-free. Single- " +
    "frame snapshot — for historical frame data use the Profiler window or " +
    "unity_senses_profiler_capture; for live allocator bytes use unity_senses_profiler_memory.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
