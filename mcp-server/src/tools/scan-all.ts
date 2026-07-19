import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const scanAll = makeTool(
  "unity_open_mcp_scan_all",
  "Full project scan using all ported verify rules. Runs in batch mode (headless Unity) — no open Editor required. Returns severity counts, per-rule summaries, timing, and issue details. exitCode 0 = pass, 1 = issues above fail_on_severity threshold.",
  {
    properties: {
          platform_profile: {
            enum: ["mobile", "console", "desktop"],
            default: "desktop",
            description: "Stored in output metadata; does not filter rules in M5.",
          },
          fail_on_severity: {
            enum: ["error", "warn", "info", "verbose", "never"],
            default: "warn",
            description: "Severity threshold for exit code. 'warn' = fail on any error or warning.",
          },
          output_path: {
            type: "string",
            description: "Optional JSON report path inside project (relative to project root or absolute).",
          },
        },
  },
);
