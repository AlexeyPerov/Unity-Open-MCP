import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const applyFix: Tool = {
  name: "unity_open_mcp_apply_fix",
  description:
    "Apply a verify rule fix action. Supports dry_run (default true) to preview the fix before applying. Returns gate envelope when dry_run is false.",
  inputSchema: {
    type: "object",
    required: ["fix_id", "issue_id"],
    properties: {
      fix_id: {
        type: "string",
        description: "Fix action id from issue payload (e.g. remove_missing_script)",
      },
      issue_id: {
        type: "string",
        description: "Issue key from validate_edit or scan_paths (format: ruleId|severity|assetPath|issueCode)",
      },
      dry_run: {
        type: "boolean",
        default: true,
        description: "Preview the fix without applying. Default true.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description: "Gate mode when dry_run is false. Ignored for dry_run.",
      },
    },
    additionalProperties: false,
  },
};
