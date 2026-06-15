import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M9 Plan 3 — compressed directory listing. Scans Assets/ on disk (no Editor
// required). Returns folder → kind → count with sample file names. .meta
// files are dropped. Works fully offline.
export const listAssets: Tool = {
  name: "unity_open_mcp_list_assets",
  description:
    "List assets in the project as a compressed directory listing (offline, no Editor required). " +
    "Returns folder → kind → count with sample file names. .meta files are dropped. " +
    "Filter by folder and/or asset type. Useful for understanding project structure before drilling into specific assets.",
  inputSchema: {
    type: "object",
    properties: {
      folder: {
        type: "string",
        default: "Assets",
        description: "Folder to list under (default: Assets).",
      },
      type: {
        type: "string",
        description:
          "Comma-separated asset kinds to filter (e.g. \"prefab,scene\"). Empty = list all asset kinds.",
      },
      max_per_folder: {
        type: "integer",
        default: 30,
        description: "Max file samples accumulated per folder before truncating. Controls listing verbosity.",
      },
    },
    additionalProperties: false,
  },
};
