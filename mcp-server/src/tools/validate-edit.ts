import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const validateEdit = makeTool(
  "unity_open_mcp_validate_edit",
  "Scoped health check without a preceding mutation. Used by agents for manual verification or pre-commit checks. " +
    "Use include_rules / exclude_rules to narrow the auto-selected rule set. Default (`profile: 'compact'`) returns " +
    "passed + issue counts grouped by severity (no per-issue list); raise to balanced/full for the full issues list, " +
    "and page large result sets with page_size/cursor. Each issue carries ruleId + categoryId (alias), " +
    "severity, code + issueCode (alias), assetPath, description, and fixId + fixSafe when a fix exists.",
  {
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
          profile: {
            enum: ["compact", "balanced", "full"],
            default: "compact",
            description:
              "Token-budget output profile (M22). 'compact' (default) = passed + issue counts grouped by severity " +
              "(issues[] stripped; drill in with balanced/full). 'balanced'/'full' = the full issues list (paged when " +
              "page_size is set). An explicit profile wins over the legacy `detail` param.",
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
          detail: {
            enum: ["summary", "normal", "verbose"],
            default: "normal",
            description:
              "Legacy compression level (alias for `profile`: summary=compact, normal/full=verbose). Prefer `profile`; " +
              "ignored when `profile` is set.",
          },
        },
  },
);

