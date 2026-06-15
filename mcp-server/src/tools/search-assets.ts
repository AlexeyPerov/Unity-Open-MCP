import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M9 Plan 2 — compact asset search. Returns grouped, reason-tagged matches with
// omission counts; each match lists why it matched (file-name/gameobject/
// component/guid). Paths are compacted (Assets/ prefix dropped, EXT table
// declared once). Offline-first (Plan 3): scans Assets/ on disk without a
// running Editor.
export const searchAssets: Tool = {
  name: "unity_open_mcp_search_assets",
  description:
    "Search the project for assets by file/GameObject/component/script/GUID. Returns compact, grouped, reason-tagged matches with omission counts. " +
    "Each result tags why it matched (file-name / gameobject / component / guid) so the agent knows which drill-down to run next. " +
    "Offline-first: scans Assets/ on disk (no Editor needed).",
  inputSchema: {
    type: "object",
    properties: {
      name: {
        type: "string",
        description: "Substring filter on file name or GameObject name.",
      },
      component: {
        type: "string",
        description: "Substring filter on component or MonoBehaviour script name.",
      },
      guid: {
        type: "string",
        description: "Raw Unity GUID to find references to.",
      },
      type: {
        type: "string",
        description: "Comma-separated asset kinds (e.g. \"prefab,scene\"). Empty = search all YAML asset kinds.",
      },
      folder: {
        type: "string",
        default: "Assets",
        description: "Folder to search under (default: Assets).",
      },
      object_limit: {
        type: "integer",
        default: 12,
        description: "Max objects listed per result file; extras counted in 'moreObjectsHidden'.",
      },
      max_results: {
        type: "integer",
        default: 50,
        description: "Max result files returned; extras counted in 'truncated'.",
      },
    },
    additionalProperties: false,
  },
};
