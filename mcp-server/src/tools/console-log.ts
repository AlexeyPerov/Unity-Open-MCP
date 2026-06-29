import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 5 — typed console log. Writes a log/warning/error to the Editor
// console from the agent. Gate-free direct-response tool: it emits a message
// to the console but writes no assets, so no gate validation is meaningful.
// The emitted entry surfaces in the next unity_senses_read_console /
// unity_senses_pull_events call.
export const consoleLog: Tool = {
  name: "unity_open_mcp_console_log",
  description:
    "Write a log / warning / error message to the Unity Editor console from " +
    "the agent. Useful for leaving breadcrumbs a human watching the Console " +
    "window can follow, or for asserting a checkpoint in a mutate→gate→fix " +
    "loop. Mutates console state only (no asset writes), so it is gate-free " +
    "and returns directly without the gate envelope. The entry appears in the " +
    "next unity_senses_read_console or unity_senses_pull_events call. Prefer " +
    "this over raw execute_csharp Debug.Log — schema-validated and typed.",
  inputSchema: {
    type: "object",
    required: ["message"],
    properties: {
      message: {
        type: "string",
        description: "The message text to write to the console.",
      },
      level: {
        type: "string",
        enum: ["log", "warning", "error"],
        default: "log",
        description:
          "Severity. 'log' → Debug.Log; 'warning' → Debug.LogWarning; " +
          "'error' → Debug.LogError (shows a red entry + stack). Defaults to 'log'.",
      },
      context_instance_id: {
        type: "integer",
        default: 0,
        description:
          "Optional GameObject/Component instance id to attach as the log " +
          "context. When set, the Console window pings the object when the " +
          "entry is clicked. Omit (or 0) for a context-less log.",
      },
      context_asset_path: {
        type: "string",
        description:
          "Optional asset path (e.g. 'Assets/Prefabs/Player.prefab') to attach " +
          "as the log context. Takes precedence over context_instance_id when " +
          "both are set. Omit for a context-less log.",
      },
    },
    additionalProperties: false,
  },
};
