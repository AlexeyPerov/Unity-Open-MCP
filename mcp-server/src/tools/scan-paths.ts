import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const scanPaths: Tool = {
  name: "unity_open_mcp_scan_paths",
  description:
    "Run one or more ported verify rules scoped to paths. For a single rule, pass categories: [\"missing_references\"]. Unknown rule IDs error with availableRules.",
  inputSchema: {
    type: "object",
    required: ["paths"],
    properties: {
      paths: { type: "array", items: { type: "string" }, description: "Asset paths to scan" },
      categories: {
        type: "array",
        items: { type: "string" },
        description: "Verify rule IDs; auto-selected from paths if omitted. Unknown IDs error with availableRules.",
      },
      platform_profile: { enum: ["mobile", "console", "desktop"], default: "desktop" },
      fail_on_severity: {
        enum: ["error", "warn", "info", "verbose", "never"],
        default: "never",
        description: "Severity threshold for 'passed' flag. 'never' = passed unless unknown rules.",
      },
    },
    additionalProperties: false,
  },
};
