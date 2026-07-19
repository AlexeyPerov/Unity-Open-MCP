import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const baselineCreate = makeTool(
  "unity_open_mcp_baseline_create",
  "Run full scan and save a baseline JSON file (schema v1) for regression tracking. Baseline includes schemaVersion, platformProfile, generatedAt, severity summary, and per-rule issue keys.",
  {
    properties: {
          baseline_path: {
            type: "string",
            default: "CI/unity-open-mcp-baseline.json",
            description: "Path for the baseline file (relative to project root or absolute).",
          },
          platform_profile: {
            enum: ["mobile", "console", "desktop"],
            default: "desktop",
          },
        },
  },
);
