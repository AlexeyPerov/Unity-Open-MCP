import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 7 — typed profiler config read. Read-only, gate-free. Folds UCP
// `profiler/config/get`. Returns the Editor-side ProfilerDriver / Profiler
// runtime knobs (driverEnabled, profileEditor = edit-vs-play target,
// deepProfiling, enableAllocationCallstacks, enableBinaryLog, logFile,
// maxUsedMemory) and the available + enabled ProfilerCategory name lists.
export const profilerGetConfig: Tool = {
  name: "unity_open_mcp_profiler_get_config",
  description:
    "Read the profiler runtime configuration: ProfilerDriver / Profiler knobs " +
    "(driverEnabled, profileEditor [edit vs play target], deepProfiling, " +
    "enableAllocationCallstacks, enableBinaryLog, logFile, maxUsedMemory) plus the " +
    "available + enabled ProfilerCategory name lists. Read-only and gate-free. Use " +
    "profiler_set_config to change a knob; use profiler_get_status for the lightweight " +
    "enabled/supported/memory surface. Some knobs are editor-version gated and return as " +
    "false / empty on platforms where the underlying API is unavailable.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
