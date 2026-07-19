import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 4 — typed UPM package check. Read-only (gate-free). Reads
// Packages/manifest.json directly (no UPM request) and reports whether a
// given package id is present as a direct dependency, plus the pinned
// reference (version/Git URL/file path) when installed.
export const packageCheck = makeTool(
  "unity_open_mcp_package_check",
  "Check whether a given package id is installed as a direct dependency in " +
    "Packages/manifest.json, and report the pinned reference (version, Git URL, or file path) " +
    "when it is. Read-only (gate-free). Reads the manifest file directly (no UPM request) so it " +
    "is fast and works even when the package manager is busy. Accepts a versioned input " +
    "('com.unity.textmeshpro@3.0.6') — the check runs against the name half only. Returns " +
    "{ installed, reference }. Use package_list or package_get_info for full package detail.",
  {
    required: ["package_id"],
        properties: {
          package_id: {
            type: "string",
            description:
              "Package id to look up. A trailing '@<version>' is stripped — the check runs against " +
              "the bare package name. Example: 'com.unity.cinemachine' or " +
              "'com.unity.textmeshpro@3.0.6'.",
          },
        },
  },
);
