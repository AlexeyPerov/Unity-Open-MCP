import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const ping: Tool = {
  name: "unity_open_mcp_ping",
  description: "Bridge health check.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
