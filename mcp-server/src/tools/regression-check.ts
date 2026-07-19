import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const regressionCheck = makeTool(
  "unity_open_mcp_regression_check",
  "Compare current full scan against a baseline file. Returns exitCode 1 when the error count increase exceeds " +
    "regression_threshold (global) or any per-category threshold, or when the baseline is missing/invalid. " +
    "per_category_thresholds maps a ruleId to its max tolerated error-count increase; rules absent from the map " +
    "fall back to regression_threshold. Emits a compact regression summary (with optional per-rule breakdown) " +
    "suitable for CI logs.",
  {
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
            description: "Max allowed increase in Error count before the check fails (applied globally).",
          },
          per_category_thresholds: {
            type: "object",
            additionalProperties: { type: "integer", minimum: 0 },
            description:
              "Per-ruleId max tolerated error-count increase. Each key is a ruleId; the value overrides regression_threshold " +
              "for that rule. Example: {\"missing_references\": 2}. Rules not named here use regression_threshold.",
          },
          platform_profile: {
            enum: ["mobile", "console", "desktop"],
            default: "desktop",
          },
        },
  },
);

