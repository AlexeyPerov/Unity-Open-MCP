import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { BRIDGE_DEFAULT_TIMEOUT_MS, BRIDGE_MIN_TIMEOUT_MS } from "../constants.js";
import { makeTool } from "./schema-fragments.js";

export const editorStatus = makeTool(
  "unity_open_mcp_editor_status",
  "Returns the current Unity Editor state: play mode, compile state, current scene path, Unity version, and editor type.",
  {
    properties: {
          timeout_ms: {
            type: "integer",
            default: BRIDGE_DEFAULT_TIMEOUT_MS,
            minimum: BRIDGE_MIN_TIMEOUT_MS,
            maximum: 300000,
          },
        },
  },
);
