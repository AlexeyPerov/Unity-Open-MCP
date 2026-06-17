import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const scanPaths: Tool = {
  name: "unity_open_mcp_scan_paths",
  description:
    "Run one or more ported verify rules scoped to paths. For a single rule, pass categories: [\"missing_references\"]. " +
    "Unknown rule IDs error with availableRules. Use include_rules / exclude_rules to filter the auto-selected set " +
    "(exclude always wins; include narrows an explicit categories list, otherwise it is additive). " +
    "fail_on_severity defaults to the project setting verify.severityThreshold in .unity-open-mcp/settings.json. " +
    "Each issue carries ruleId + categoryId (alias), severity, code + issueCode (alias), assetPath, description, " +
    "and fixId + fixSafe when a fix is available. rulesApplied lists the post-filter rule set.",
  inputSchema: {
    type: "object",
    required: ["paths"],
    properties: {
      paths: { type: "array", items: { type: "string" }, description: "Asset paths to scan" },
      categories: {
        type: "array",
        items: { type: "string" },
        description:
          "Verify rule IDs; auto-selected from paths if omitted. Unknown IDs error with availableRules.",
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
      platform_profile: { enum: ["mobile", "console", "desktop"], default: "desktop" },
      fail_on_severity: {
        enum: ["error", "warn", "info", "verbose", "never"],
        description:
          "Severity threshold for the `passed` flag. Omit to use the project default " +
          "(verify.severityThreshold in .unity-open-mcp/settings.json; falls back to `error`).",
      },
    },
    additionalProperties: false,
  },
};
