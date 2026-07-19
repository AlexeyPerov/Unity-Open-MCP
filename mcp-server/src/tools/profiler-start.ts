import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 7 — typed profiler session start. Mutating editor state but writes
// NO assets, so the gate (which validates asset-reference fallout) has nothing
// to validate — gate-free direct-response tool. Idempotent: calling when
// already enabled returns the current enabled state. Deep-profile /
// allocation-callstacks / binary-log options are intentionally NOT folded in —
// they require a richer session state than this surface exposes; use
// profiler_set_config for those.
export const profilerStart = makeTool(
  "unity_open_mcp_profiler_start",
  "Enable the Unity runtime profiler (UnityEngine.Profiling.Profiler.enabled = true) " +
    "and open the Profiler window (Window > Analysis > Profiler). Idempotent — calling " +
    "when already enabled is a no-op that returns the current state. Mutates editor state " +
    "only (no asset writes); gate-free. Recording starts at the next frame; poll " +
    "profiler_get_status or unity_senses_profiler_capture to confirm data is flowing. " +
    "Enabling the profiler adds runtime overhead — disable it via profiler_stop when " +
    "finished to restore full editor throughput.",
  {
    properties: {
          open_window: {
            type: "boolean",
            default: true,
            description:
              "Also open the Profiler window via Window > Analysis > Profiler. Default true. " +
              "When false, only the runtime Profiler.enabled flag flips (the window stays " +
              "in whatever state it was).",
          },
        },
  },
);
