import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const executeMenu: Tool = {
  name: "unity_open_mcp_execute_menu",
  description: "Execute a Unity Editor menu item.",
  inputSchema: {
    type: "object",
    required: ["menu_path"],
    properties: {
      menu_path: {
        type: "string",
        description: "e.g. Assets/Refresh, File/Save Project",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
