import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 4 — typed UPM package get_info. Read-only (gate-free). Inspects
// one package by name (installed via Client.List, registry via Client.Search
// when offline:false). Returns the same package shape as package_list plus
// an `installed` boolean.
export const packageGetInfo: Tool = {
  name: "unity_open_mcp_package_get_info",
  description:
    "Inspect a single UPM package by name or package id. Read-only (gate-free). Looks up the " +
    "installed package first (Client.List); if not installed and `offline: false`, falls back to " +
    "a live registry search. Returns the full package descriptor (name/displayName/version/" +
    "packageId/source/resolvedPath/description/category/versions/registry/dependencies) plus an " +
    "`installed` boolean flag. Use this for one-package detail instead of paging through " +
    "package_list.",
  inputSchema: {
    type: "object",
    required: ["name"],
    properties: {
      name: {
        type: "string",
        description:
          "Package name (e.g. 'com.unity.textmeshpro'), package id " +
          "('com.unity.textmeshpro@3.0.6'), or display name ('TextMesh Pro'). Match is " +
          "case-insensitive on name / packageId / displayName.",
      },
      offline: {
        type: "boolean",
        default: true,
        description:
          "When true (default), only look in installed packages. Set false to allow a registry " +
          "search when the package is not installed locally.",
      },
    },
    additionalProperties: false,
  },
};
