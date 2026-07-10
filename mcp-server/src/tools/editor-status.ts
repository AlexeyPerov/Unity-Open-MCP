import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { BRIDGE_DEFAULT_TIMEOUT_MS, BRIDGE_MIN_TIMEOUT_MS } from "../constants.js";

export const editorStatus: Tool = {
  name: "unity_open_mcp_editor_status",
  description:
    "Returns the current Unity Editor state: play mode, compile state, current scene path, Unity version, and editor type.",
  inputSchema: {
    type: "object",
    properties: {
      timeout_ms: {
        type: "integer",
        default: BRIDGE_DEFAULT_TIMEOUT_MS,
        minimum: BRIDGE_MIN_TIMEOUT_MS,
        maximum: 300000,
      },
    },
    additionalProperties: false,
  },
};
