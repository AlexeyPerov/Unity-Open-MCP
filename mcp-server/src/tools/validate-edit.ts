import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const validateEdit: Tool = {
  name: "unity_open_mcp_validate_edit",
  description:
    "Scoped health check without a preceding mutation. Used by agents for manual verification or pre-commit checks. " +
    "Use include_rules / exclude_rules to narrow the auto-selected rule set. Each issue carries ruleId + categoryId (alias), " +
    "severity, code + issueCode (alias), assetPath, description, and fixId + fixSafe when a fix exists.",
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
      include_rules: {
        type: "array",
        items: { type: "string" },
        description:
          "Allow-list applied to the resolved rule set. When `categories` is set, include_rules narrows to their intersection; " +
          "without `categories` it is additive on top of the auto-selected set.",
      },
      exclude_rules: {
        type: "array",
        items: { type: "string" },
        description: "Deny-list. Always wins over categories and include_rules.",
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

