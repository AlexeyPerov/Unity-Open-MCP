import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 7 — typed profiler session stop. Mutating editor state but writes
// NO assets; gate-free direct-response tool. Idempotent. Folds UMCP
// `profiler-stop` and UCP `profiler/session/stop` (UCP's session-state restore
// is intentionally NOT folded — Unity's profiler keeps buffered frames across a
// stop, so call profiler_clear_data to discard them).
export const profilerStop: Tool = {
  name: "unity_open_mcp_profiler_stop",
  description:
    "Disable the Unity runtime profiler (UnityEngine.Profiling.Profiler.enabled = false). " +
    "Idempotent — calling when already disabled returns the current state. Mutates editor " +
    "state only (no asset writes); gate-free. Buffered frames stay in the Profiler's memory " +
    "after stop — call profiler_clear_data to discard them, or profiler_save_data to persist " +
    "a snapshot first. Closing the Profiler window is NOT done by this tool — Unity keeps " +
    "the runtime flag separate from the window.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
