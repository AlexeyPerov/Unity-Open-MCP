import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M9 Plan 2 — compact drill-down asset read. Returns a MAP by default (counts +
// CMP component-set declarations + folded hierarchy tree + omission counts);
// drill-down flags expand detail progressively without the caller re-specifying
// the whole asset. Offline-first (Plan 3): text-serialized assets are parsed
// directly from disk without a running Editor; binary formats fall back to the
// live bridge.
export const readAsset: Tool = {
  name: "unity_open_mcp_read_asset",
  description:
    "Read a Unity asset as a compact, token-budgeted summary (hierarchy + components + counts). " +
    "Default (`profile: 'compact'`) returns a map: ASSET/PATH/GUID/OBJECTS/COMPONENTS counts, CMP component-set declarations, and a folded TREE with 'more: N hidden' omission counts. " +
    "Drill down with component/path/id, or raise the budget with profile=balanced|full. Page large trees with page_size/cursor. " +
    "Achieves >=70% size reduction vs raw YAML on typical prefabs. Offline-first: text-serialized assets parse from disk (no Editor needed); binary formats fall back to the live bridge.",
  inputSchema: {
    type: "object",
    required: ["asset_path"],
    properties: {
      asset_path: {
        type: "string",
        description:
          "Asset path to read (e.g. \"Assets/Prefabs/Player.prefab\"). Text-serialized YAML assets only (.prefab/.unity/.asset/.mat/.controller/.anim).",
      },
      profile: {
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "Token-budget output profile (M22). 'compact' (default) = folded tree + component-set codes + omission counts. " +
          "'balanced' = inline component names per node. 'full' = full tree without render-only folding. " +
          "An explicit profile wins over the legacy `detail` param; the two map onto the same axis.",
      },
      page_size: {
        type: "integer",
        minimum: 1,
        description:
          "Page the TREE rows (M22 uniform paging). When set, the response carries a `pagination` block with a `next_cursor` " +
          "to resume. Omit to receive the whole (profile-shaped) tree in one response.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation token from a previous response's `pagination.next_cursor`. Page the TREE rows.",
      },
      detail: {
        enum: ["summary", "normal", "verbose"],
        default: "summary",
        description:
          "Legacy compression level (alias for `profile`: summary=compact, normal=balanced, verbose=full). " +
          "Prefer `profile`; ignored when `profile` is set.",
      },
      component: {
        type: "string",
        description:
          "Drill-down: dump fields for components whose name/scriptPath matches (case-insensitive substring). Returns a componentMatches list instead of the TREE.",
      },
      path: {
        type: "string",
        description:
          "Drill-down: scope the TREE to the subtree whose hierarchy path matches (case-insensitive substring).",
      },
      id: {
        type: "string",
        description:
          "Drill-down: return one object by local YAML fileID. Offline-only (live bridge does not expose fileIDs); use component/path drill-down when reading live.",
      },
      override: {
        type: "boolean",
        default: false,
        description:
          "Drill-down: show prefab variant overrides (property modifications, added/removed components and GameObjects). Returns an overrides list instead of the TREE. Offline-only (requires the offline YAML parser).",
      },
      depth: {
        type: "integer",
        default: -1,
        description: "Hierarchy depth cap. -1 = unlimited. Nodes past the cap are counted in 'moreHidden'.",
      },
      limit: {
        type: "integer",
        default: 0,
        description: "Max TREE rows after folding. 0 = unlimited. Dropped rows counted in 'moreHidden'.",
      },
      field_limit: {
        type: "integer",
        default: 0,
        description:
          "Max serialized fields per component when the bridge fetches them. 0 = names only (suitable for summary). Bump to 20-50 before using component drill-down so fields are available.",
      },
    },
    additionalProperties: false,
  },
};
