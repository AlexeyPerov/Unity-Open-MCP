import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const regressionCheck: Tool = {
  name: "unity_open_mcp_regression_check",
  description:
    "Compare current full scan against a baseline file. Returns exitCode 1 when the error count increase exceeds regression_threshold, or when the baseline is missing/invalid. Emits a compact regression summary suitable for CI logs.",
  inputSchema: {
    type: "object",
    required: ["baseline_path"],
    properties: {
      baseline_path: {
        type: "string",
        description: "Path to a baseline JSON file created by unity_open_mcp_baseline_create.",
      },
      regression_threshold: {
        type: "integer",
        default: 0,
        minimum: 0,
        description: "Max allowed increase in Error count before the check fails.",
      },
      platform_profile: {
        enum: ["mobile", "console", "desktop"],
        default: "desktop",
      },
    },
    additionalProperties: false,
  },
};
