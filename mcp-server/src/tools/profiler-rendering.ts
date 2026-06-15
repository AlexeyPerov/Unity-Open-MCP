import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const profilerRendering: Tool = {
  name: "unity_agent_profiler_rendering",
  description:
    "Snapshot the rendering environment: GPU / SystemInfo, active render " +
    "pipeline (URP/HDRP/Built-in), QualitySettings, screen resolution and " +
    "refresh rate, target frame rate, and Time stats. For per-frame batch / " +
    "draw-call counts use unity_agent_profiler_capture. Requires a live " +
    "Unity Editor connection.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
