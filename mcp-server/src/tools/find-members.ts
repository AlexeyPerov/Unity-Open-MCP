import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const findMembers: Tool = {
  name: "unity_open_mcp_find_members",
  description:
    "Discover types, methods, and properties for agent planning (reduces blind execute calls). " +
    "Token-bounded: `max_results` caps the returned list, `truncated` always reports how many " +
    "additional matches were dropped.",
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
        description:
          "Maximum number of members to return. Additional matches are counted in `truncated`.",
      },
    },
    additionalProperties: false,
  },
};
