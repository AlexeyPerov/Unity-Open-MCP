import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 1 / T20.1.3 — Frame Debugger control + draw-call list.
//
// Routes through Unity's internal Frame Debugger API (reflection-wrapped in
// the bridge) to enable/disable capture and enumerate the draw calls of the
// currently-debugged frame. Enable/disable is a non-mutating Editor state
// change, so the tool is gate-free; the response carries `windowOpened` so the
// agent knows Editor UI may have changed (the Frame Debugger window may have
// been opened during capture).
//
// The draw-call list is capped at max_draw_calls (default 256) with a
// `truncated` count; per-call entries surface shader / pass / material /
// render target / vertex/index/instance counts where the Unity build exposes
// them (some fields are version-specific and simply omitted when absent).
export const frameDebugger: Tool = {
  name: "unity_senses_frame_debugger",
  description:
    "Control Unity's Frame Debugger and list the draw calls of the " +
    "currently-debugged frame. Use action 'enable' to open the Frame Debugger " +
    "window and start capturing a frame, 'disable' to stop capturing, or 'list' " +
    "to enumerate the draw calls (shader, pass, material, render target, " +
    "vertex/index/instance counts per call). Enable/disable is a non-mutating " +
    "Editor state change — no assets or scenes are touched and the gate is " +
    "always off, but the response reports windowOpened so the agent knows " +
    "Editor UI state may have changed. Requires a live Unity Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      action: {
        type: "string",
        enum: ["enable", "disable", "list"],
        default: "list",
        description:
          "'enable' opens the Frame Debugger window and starts capturing; " +
          "'disable' stops capturing; 'list' returns the draw-call list for " +
          "the currently-debugged frame (enable must have been called first).",
      },
      max_draw_calls: {
        type: "integer",
        default: 256,
        minimum: 1,
        maximum: 2000,
        description:
          "Maximum number of draw calls to return for action 'list'. The " +
          "response reports the full totalDrawCalls count plus a truncated " +
          "flag when the list was capped.",
      },
    },
    additionalProperties: false,
  },
};
