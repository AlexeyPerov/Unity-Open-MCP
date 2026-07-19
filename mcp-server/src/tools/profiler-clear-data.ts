import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 7 — typed profiler buffered-frames clear. Mutates editor state
// (ProfilerDriver.ClearAllFrames) but writes NO assets; gate-free direct-
// response tool. Idempotent. Cannot be undone — call profiler_save_data first
// if a snapshot is wanted.
export const profilerClearData = makeTool(
  "unity_open_mcp_profiler_clear_data",
  "Discard every frame currently buffered by the Editor Profiler " +
    "(UnityEditorInternal.ProfilerDriver.ClearAllFrames). Cannot be undone — call " +
    "profiler_save_data first if you need a snapshot. Mutates editor state only (no asset " +
    "writes); gate-free. The Profiler window's frame history is empty after this call; " +
    "subsequent recording starts from frame 0. Idempotent (clearing an already-empty buffer " +
    "is a no-op).",
  {
    properties: {},
  },
);
