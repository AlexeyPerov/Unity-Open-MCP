import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const baselineCreate: Tool = {
  name: "unity_agent_baseline_create",
  description:
    "Run full scan and save a baseline JSON file (schema v1) for regression tracking. Baseline includes schemaVersion, platformProfile, generatedAt, severity summary, and per-rule issue keys.",
  inputSchema: {
    type: "object",
    properties: {
      baseline_path: {
        type: "string",
        default: "CI/unity-agent-baseline.json",
        description: "Path for the baseline file (relative to project root or absolute).",
      },
      platform_profile: {
        enum: ["mobile", "console", "desktop"],
        default: "desktop",
      },
    },
    additionalProperties: false,
  },
};
