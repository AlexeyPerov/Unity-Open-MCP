import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const scanPaths = makeTool(
  "unity_open_mcp_scan_paths",
  "Run one or more ported verify rules scoped to paths. For a single rule, pass categories: [\"missing_references\"]. " +
    "Unknown rule IDs error with availableRules. Use include_rules / exclude_rules to filter the auto-selected set " +
    "(exclude always wins; include narrows an explicit categories list, otherwise it is additive). " +
    "fail_on_severity defaults to the project setting verify.severityThreshold in .unity-open-mcp/settings.json. " +
    "Default (`profile: 'compact'`) returns passed + issue counts grouped by severity (no per-issue list); raise to " +
    "balanced/full for the full issues list, and page large result sets with page_size/cursor. " +
    "Each issue carries ruleId + categoryId (alias), severity, code + issueCode (alias), assetPath, description, " +
    "and fixId + fixSafe when a fix is available. rulesApplied lists the post-filter rule set.",
  {
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
          profile: {
            enum: ["compact", "balanced", "full"],
            default: "compact",
            description:
              "Token-budget output profile (M22). 'compact' (default) = passed + issue counts grouped by severity " +
              "(issues[] stripped; drill in with balanced/full). 'balanced'/'full' = the full issues list (paged when " +
              "page_size is set).",
          },
          page_size: {
            type: "integer",
            minimum: 1,
            description:
              "Page the issues list (M22 uniform paging; balanced/full). When set, the response carries a `pagination` block " +
              "with a `next_cursor` to resume. Omit to receive the whole issues list in one response.",
          },
          cursor: {
            type: "string",
            description:
              "Opaque continuation token from a previous response's `pagination.next_cursor`. Page the issues list.",
          },
          fail_on_severity: {
            enum: ["error", "warn", "info", "verbose", "never"],
            description:
              "Severity threshold for the `passed` flag. Omit to use the project default " +
              "(verify.severityThreshold in .unity-open-mcp/settings.json; falls back to `error`).",
          },
        },
  },
);
