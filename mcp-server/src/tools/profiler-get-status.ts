import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 7 — typed profiler status read. Read-only, gate-free. Does NOT
// duplicate M10 — unity_senses_profiler_memory / profiler_rendering /
// profiler_capture are the per-frame / allocator / GPU reads; this tool
// returns the runtime flag, max used memory high-water mark, platform support,
// and the local module bookkeeping set.
export const profilerGetStatus = makeTool(
  "unity_open_mcp_profiler_get_status",
  "Snapshot the runtime profiler's current state: enabled flag, platform support " +
    "(Profiler.supported), max-used-memory high-water mark (Profiler.maxUsedMemory), and " +
    "the local module bookkeeping set (see profiler_list_modules / profiler_enable_module). " +
    "Read-only and gate-free. Pair with profiler_start / profiler_stop to drive the enabled " +
    "flag. For per-frame hierarchy / live allocator bytes / GPU + QualitySettings batch use " +
    "unity_senses_profiler_capture / profiler_memory / profiler_rendering instead — this tool " +
    "is only the runtime-status surface.",
  {
    properties: {},
  },
);
