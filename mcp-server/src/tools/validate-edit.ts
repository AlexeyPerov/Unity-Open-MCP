import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const validateEdit: Tool = {
  name: "unity_open_mcp_validate_edit",
  description:
    "Scoped health check without a preceding mutation. Used by agents for manual verification or pre-commit checks.",
  inputSchema: {
    type: "object",
    required: ["paths"],
    properties: {
      paths: {
        type: "array",
        items: { type: "string" },
        minItems: 1,
      },
      categories: {
        type: "array",
        items: { type: "string" },
        description: "Verify rule IDs; auto-selected from paths if omitted",
      },
      platform_profile: {
        enum: ["mobile", "console", "desktop"],
        default: "desktop",
      },
      detail: {
        enum: ["summary", "normal", "verbose"],
        default: "normal",
      },
    },
    additionalProperties: false,
  },
};
