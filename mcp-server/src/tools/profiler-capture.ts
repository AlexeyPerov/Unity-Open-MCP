import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const profilerCapture: Tool = {
  name: "unity_agent_profiler_capture",
  description:
    "Capture the Unity Profiler frame hierarchy with drill-down and " +
    "multi-frame averaging. Requires the Profiler to be enabled and to have " +
    "captured at least one frame (Window > Analysis > Profiler > Record). " +
    "Drill into hotspots using 'parent' (an itemId from a previous response) " +
    "or 'root' (a name substring), and bound the output with depth / min_ms / " +
    "max_items to protect the token budget. itemId values are only valid " +
    "within the same frame; re-fetch the frame to drill further. Requires a " +
    "live Unity Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      frame: {
        type: "integer",
        default: -1,
        description:
          "Single frame index to read. -1 (default) = last captured frame.",
      },
      from_frame: {
        type: "integer",
        default: -1,
        description:
          "Start frame index for range averaging. If set (or to_frame set), " +
          "switches to averaged mode across [from_frame..to_frame].",
      },
      to_frame: {
        type: "integer",
        default: -1,
        description:
          "End frame index for range averaging. Defaults to lastFrameIndex " +
          "when from_frame is set.",
      },
      frames: {
        type: "integer",
        default: 0,
        description:
          "Shortcut for averaging the last N frames. Used only when " +
          "from_frame/to_frame are not set. Values > 1 trigger averaging.",
      },
      thread: {
        type: "integer",
        default: 0,
        description: "Thread index. 0 = main thread.",
      },
      parent: {
        type: "integer",
        default: -1,
        description:
          "Profiler item ID to drill into (from the 'itemId' field of a " +
          "previous response). -1 = root level.",
      },
      root: {
        type: "string",
        default: "",
        description:
          "Find an item by case-insensitive name substring (recursive) and " +
          "use it as the root. Takes precedence over 'parent'.",
      },
      min_ms: {
        type: "number",
        default: 0,
        minimum: 0,
        description:
          "Minimum total time (ms) filter. Items below this are omitted.",
      },
      sort: {
        type: "string",
        enum: ["total", "self", "calls"],
        default: "total",
        description: "Sort order for returned items.",
      },
      max_items: {
        type: "integer",
        default: 30,
        minimum: 1,
        maximum: 200,
        description: "Maximum number of items returned per level (token cap).",
      },
      depth: {
        type: "integer",
        default: 1,
        minimum: 0,
        maximum: 10,
        description:
          "Recursive depth. 1 = one level (default), 0 = unlimited.",
      },
    },
    additionalProperties: false,
  },
};
