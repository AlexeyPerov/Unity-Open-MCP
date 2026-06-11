import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const findMembers: Tool = {
  name: "unity_agent_find_members",
  description:
    "Discover types, methods, and properties for agent planning (reduces blind execute calls).",
  inputSchema: {
    type: "object",
    properties: {
      query: {
        type: "string",
        description: "Substring filter on type or member name",
      },
      kind: {
        enum: ["type", "method", "property", "all"],
        default: "all",
      },
      assembly_filter: {
        type: "string",
      },
      include_unity_editor: {
        type: "boolean",
        default: true,
      },
      include_project: {
        type: "boolean",
        default: true,
      },
      max_results: {
        type: "integer",
        default: 50,
        maximum: 200,
      },
    },
    additionalProperties: false,
  },
};
