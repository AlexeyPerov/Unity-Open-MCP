import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const findReferences: Tool = {
  name: "unity_open_mcp_find_references",
  description:
    "Reverse dependency lookup for assets. Returns all assets that reference the given asset path or GUID. " +
    "Default (`profile: 'compact'`) returns counts grouped by kind/folder; raise to balanced/full for the per-asset path list " +
    "(and verbose field locations, offline). Page large result sets with page_size/cursor. " +
    "Works offline (scanning YAML on disk) when no Editor is running, or via the live bridge when connected.",
  inputSchema: {
    type: "object",
    properties: {
      asset_path: { type: "string", description: "Asset path (e.g. Assets/Prefabs/Player.prefab)" },
      guid: { type: "string", pattern: "^[0-9a-fA-F]{32}$", description: "Asset GUID (32 hex chars)" },
      profile: {
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "Token-budget output profile (M22). 'compact' (default) = counts + byKind/byFolder groupings only (no per-asset list). " +
          "'balanced' = referencing asset paths grouped by kind/folder. 'full' = also includes which fields reference the target (offline). " +
          "An explicit profile wins over the legacy `detail` param.",
      },
      page_size: {
        type: "integer",
        minimum: 1,
        description:
          "Page the referencing-assets list (M22 uniform paging; balanced/full). When set, the response carries a `pagination` " +
          "block with a `next_cursor` to resume. Omit to receive up to `max_results` entries in one response.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation token from a previous response's `pagination.next_cursor`. Page the referencing-assets list.",
      },
      detail: {
        enum: ["summary", "normal", "verbose"],
        default: "normal",
        description:
          "Legacy compression level (alias for `profile`: summary=compact, normal=balanced, verbose=full). " +
          "Prefer `profile`; ignored when `profile` is set.",
      },
      max_results: {
        type: "integer",
        default: 100,
        description:
          "Max referencing assets returned when page_size is omitted. Legacy alias of the single-page cap. " +
          "Callers should pass >= 1; the value 0 is a server-internal sentinel meaning 'unlimited (for paging)' " +
          "and is emitted by the server itself when page_size is set — it is never a value a caller needs to pass.",
      },
      max_per_file: { type: "integer", default: 5, description: "Verbose/full mode (offline): max field locations per file" },
      pattern_threshold: {
        type: "integer",
        default: 0,
        description: "Collapse folders with >= this many referencing files into a single summary entry (0 = disabled). Offline only.",
      },
    },
    oneOf: [
      { required: ["asset_path"] },
      { required: ["guid"] },
    ],
    additionalProperties: false,
  },
};
