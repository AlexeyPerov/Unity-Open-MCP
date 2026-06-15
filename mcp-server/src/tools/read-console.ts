import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const readConsole: Tool = {
  name: "unity_agent_read_console",
  description:
    "Read the Unity Editor console log entries. Filter by type (error/warning/log/all), " +
    "optionally clear the console, and get structured entries with stack traces. " +
    "Unity-internal stack frames are stripped by default for readability. " +
    "Useful after execute_csharp or mutations to check for compile errors, " +
    "runtime exceptions, or debug logs. Requires a live Unity Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      type: {
        type: "string",
        enum: ["error", "warning", "log", "all"],
        default: "all",
        description: "Filter entries by log type.",
      },
      clear: {
        type: "boolean",
        default: false,
        description: "Clear the console after reading entries.",
      },
      max_entries: {
        type: "integer",
        default: 100,
        minimum: 1,
        maximum: 1000,
        description: "Maximum number of entries to return (most recent).",
      },
      max_stack_frames: {
        type: "integer",
        default: 20,
        minimum: 0,
        maximum: 100,
        description: "Maximum stack-trace frames per entry.",
      },
      include_unity_frames: {
        type: "boolean",
        default: false,
        description:
          "Include Unity-internal stack frames (UnityEngine, UnityEditor, System). " +
          "Off by default for cleaner output.",
      },
    },
    additionalProperties: false,
  },
};
