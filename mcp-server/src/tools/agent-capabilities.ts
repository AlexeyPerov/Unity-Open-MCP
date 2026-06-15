import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const agentCapabilities: Tool = {
  name: "unity_open_mcp_capabilities",
  description:
    "Discover the full capability surface in one call: every tool with its input schema and route policy, " +
    "every verify rule with applicable asset kinds and issue severities, and every available fix. " +
    "Each capability carries an `implemented` boolean; planned-but-unbuilt items return with " +
    "`status: \"planned\"` and actionable guidance instead of failing. Call this first to learn what is " +
    "available before using execute_csharp or scan_paths blindly.",
  inputSchema: {
    type: "object",
    properties: {
      kind: {
        enum: ["tools", "rules", "fixes"],
        description:
          "Filter to a single surface. Omit to return tools + rules + fixes together.",
      },
      include_planned: {
        type: "boolean",
        default: true,
        description:
          "Include planned-but-unbuilt capabilities (status \"planned\"). Set false to see only implemented items.",
      },
    },
    additionalProperties: false,
  },
};
