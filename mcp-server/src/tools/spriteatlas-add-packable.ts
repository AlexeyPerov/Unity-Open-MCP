import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.1 — SpriteAtlas domain tool. Mutating: add packables by
// asset path. Runs the full gate path (EditorSettle); paths_hint is the atlas
// asset path. Per-path errors are accumulated.
export const spriteatlasAddPackable: Tool = {
  name: "unity_open_mcp_spriteatlas_add_packable",
  description:
    "Add packables (sprites / textures / DefaultAsset folders) to a " +
    "SpriteAtlas by Assets/-rooted path. Each path resolves to its main asset " +
    "Object. Per-path errors are accumulated — a single bad path does not " +
    "abort the batch. Idempotent re-adds are tolerated by Unity. Mutating: " +
    "runs the full gate path (editor_settle); paths_hint is the .spriteatlas " +
    "asset path. Built-in 2D module; the sprite2d group is hidden until " +
    "manage_tools activates it.",
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
          "Assets/-rooted paths of the sprites / textures / folders to add " +
          "as packables. Each resolves to its main asset Object.",
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
