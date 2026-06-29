import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.1 — SpriteAtlas domain tool. Mutating: remove packables
// by asset path. Runs the full gate path (EditorSettle); paths_hint is the
// atlas asset path. Per-path errors are accumulated.
export const spriteatlasRemovePackable: Tool = {
  name: "unity_open_mcp_spriteatlas_remove_packable",
  description:
    "Remove packables from a SpriteAtlas by Assets/-rooted path. Each path " +
    "resolves against the current packables list and is removed if present. " +
    "Per-path errors are accumulated (a path that is not a packable of this " +
    "atlas is reported, not fatal). Mutating: runs the full gate path " +
    "(editor_settle); paths_hint is the .spriteatlas asset path. Built-in 2D " +
    "module; the 2d group is hidden until manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "packable_paths", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "Assets/-rooted .spriteatlas asset path.",
      },
      packable_paths: {
        type: "array",
        items: { type: "string" },
        description:
          "Assets/-rooted paths of the packables to remove. Each must match " +
          "a current packable of this atlas (matched by asset path).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the .spriteatlas asset path.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
