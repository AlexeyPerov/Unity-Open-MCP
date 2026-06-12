import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const editorStatus: Tool = {
  name: "unity_agent_editor_status",
  description:
    "Returns the current Unity Editor state: play mode, compile state, current scene path, Unity version, and editor type.",
  inputSchema: {
    type: "object",
    properties: {
      timeout_ms: {
        type: "integer",
        default: 30000,
        minimum: 1000,
        maximum: 300000,
      },
    },
    additionalProperties: false,
  },
};
