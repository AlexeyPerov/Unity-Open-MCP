import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M9 Plan 2 — compact asset search. Returns grouped, reason-tagged matches with
// omission counts; each match lists why it matched (file-name/gameobject/
// component/guid). Paths are compacted (Assets/ prefix dropped, EXT table
// declared once). Offline-first (Plan 3): scans Assets/ on disk without a
// running Editor.
export const searchAssets: Tool = {
  name: "unity_open_mcp_search_assets",
  description:
    "Search the project for assets by file/GameObject/component/script/GUID. Returns grouped, reason-tagged matches with omission counts. " +
    "Each result tags why it matched (file-name / gameobject / component / guid) so the agent knows which drill-down to run next. " +
    "Default `profile: 'compact'`; page large result sets with page_size/cursor. " +
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
      profile: {
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "Token-budget output profile (M22). 'compact' (default) = per-file object listings capped at object_limit with a moreObjectsHidden count. " +
          "'balanced'/'full' raise the object_limit default. Maps onto the legacy `object_limit` axis.",
      },
      page_size: {
        type: "integer",
        minimum: 1,
        description:
          "Page the result-file matches (M22 uniform paging). When set, the response carries a `pagination` block with a `next_cursor` " +
          "to resume. Omit to receive up to `max_results` matches in one response.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation token from a previous response's `pagination.next_cursor`. Page the result-file matches.",
      },
      object_limit: {
        type: "integer",
        default: 12,
        description:
          "Max objects listed per result file; extras counted in 'moreObjectsHidden'. Legacy alias (overrides the profile default when set explicitly).",
      },
      max_results: {
        type: "integer",
        default: 50,
        description:
          "Max result files returned when page_size is omitted; extras counted in 'truncated'. Legacy alias of the single-page cap. " +
          "Callers should pass >= 1; the value 0 is a server-internal sentinel meaning 'unlimited (for paging)' " +
          "and is emitted by the server itself when page_size is set — it is never a value a caller needs to pass.",
      },
    },
    additionalProperties: false,
  },
};
