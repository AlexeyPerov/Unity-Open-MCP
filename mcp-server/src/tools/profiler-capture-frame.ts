import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 1 / T20.1.4 — single-frame deep profiler capture.
//
// Unlike the existing per-module profiler_get_* stats (aggregate / rolling),
// this returns one (or a few) frame's full sample hierarchy for the requested
// modules: deeper than the existing surface, more tokens, but the right tool
// when an agent needs to inspect a specific frame's call tree (e.g. a spike).
// The cost grows with frame_count, max_depth, and max_items — defaults (1, 8,
// 200) bound the token budget.
//
// If the Profiler is disabled the bridge enables it for one frame and reports
// profilerWasEnabled so the agent knows Editor state may have changed.
// Read-only (gate off).
export const profilerCaptureFrame: Tool = {
  name: "unity_senses_profiler_capture_frame",
  description:
    "Capture a single frame's deep profiler sample tree for the requested " +
    "modules. Deeper than profiler_get_script_stats / profiler_get_status " +
    "(those report aggregate / rolling stats); this returns the full sample " +
    "hierarchy for one frame so an agent can inspect a specific frame's call " +
    "tree (e.g. a spike). The output size grows with frame_count, max_depth, " +
    "and max_items — defaults (frame_count = 1, max_depth = 8, max_items = " +
    "200) bound the token budget. If the Profiler is disabled, the tool " +
    "enables it for one frame and reports profilerWasEnabled in the response " +
    "so the agent knows Editor state may have changed. Requires a live Unity " +
    "Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      frame_count: {
        type: "integer",
        default: 1,
        minimum: 1,
        maximum: 10,
        description:
          "Number of recent frames to walk back from the latest captured " +
          "frame. Default 1 (just the latest frame). The response reports the " +
          "full sample tree per frame, so larger values multiply the output " +
          "size; cap at 10 to protect the token budget.",
      },
      modules: {
        type: "string",
        description:
          "Optional comma-separated Profiler category filter, e.g. " +
          "\"CPU,Rendering,Memory\". When omitted, the tool returns the full " +
          "sample tree (every category). Names match the Profiler category " +
          "labels shown in the Profiler window (case-insensitive).",
      },
      thread_index: {
        type: "integer",
        default: 0,
        minimum: 0,
        description:
          "Profiler thread index to read. 0 = main thread (default). Use a " +
          "higher index to read a worker thread; enumerate threads via the " +
          "Profiler window or profiler_get_status.",
      },
      max_depth: {
        type: "integer",
        default: 8,
        minimum: 1,
        maximum: 64,
        description:
          "Maximum recursion depth for the sample tree. Deeper trees expose " +
          "more call detail but grow the output quickly; default 8 strikes a " +
          "balance for spike diagnosis.",
      },
      max_items: {
        type: "integer",
        default: 200,
        minimum: 1,
        maximum: 2000,
        description:
          "Maximum number of sample items emitted per frame across all " +
          "branches. Additional items are reported via the truncated count in " +
          "the response.",
      },
    },
    additionalProperties: false,
  },
};
