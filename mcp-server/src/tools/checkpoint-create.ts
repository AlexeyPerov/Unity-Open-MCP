import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const checkpointCreate: Tool = {
  name: "unity_open_mcp_checkpoint_create",
  description: "Create a manual checkpoint for later delta comparison.",
  inputSchema: {
    type: "object",
    properties: {
      paths: {
        type: "array",
        items: { type: "string" },
        description: "Scope; empty = whole project summary (expensive)",
      },
      label: { type: "string" },
    },
    additionalProperties: false,
  },
};
