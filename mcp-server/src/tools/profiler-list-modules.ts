import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 7 — typed profiler module list. Read-only, gate-free. Folds UMCP
// `profiler-list-modules`. Returns the canonical Profiler window module names
// (CPU / GPU / Rendering / Memory / Audio / Video / Physics / Physics2D /
// NetworkMessages / NetworkOperations / UI / UIDetails / GlobalIllumination /
// VirtualTexturing) with the local enabled bookkeeping flag each.
export const profilerListModules: Tool = {
  name: "unity_open_mcp_profiler_list_modules",
  description:
    "List every known Profiler window module name (CPU, GPU, Rendering, Memory, Audio, Video, " +
    "Physics, Physics2D, NetworkMessages, NetworkOperations, UI, UIDetails, " +
    "GlobalIllumination, VirtualTexturing) with the local enabled bookkeeping flag for each. " +
    "Read-only and gate-free. The enabled flag is local bookkeeping only — Unity's runtime API " +
    "does not expose programmatic per-module toggling; actual module visibility is controlled " +
    "from the Profiler window. Pair with profiler_enable_module to flip the flag.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
