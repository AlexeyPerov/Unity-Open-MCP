import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const profilerRendering = makeTool(
  "unity_senses_profiler_rendering",
  "Snapshot the rendering environment: GPU / SystemInfo, active render " +
    "pipeline (URP/HDRP/Built-in), QualitySettings, screen resolution and " +
    "refresh rate, target frame rate, and Time stats. For per-frame batch / " +
    "draw-call counts use unity_senses_profiler_capture. Requires a live " +
    "Unity Editor connection.",
  {
    properties: {},
  },
);
