import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 6 / T20.6.3 — Tilemap create_tile_asset. Compile-gated. Mutating
// (creates a .asset).
export const tilemapCreateTileAsset: Tool = {
  name: "unity_open_mcp_tilemap_create_tile_asset",
  description:
    "Create a Tile ScriptableObject asset (.asset) at the given asset_path. " +
    "Optionally seed its sprite via sprite_asset_path. The parent folder must " +
    "already exist. Mutating: runs the full gate path; paths_hint includes the " +
    "new asset path. Requires com.unity.2d.tilemap.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "Destination asset path. Must end with '.asset'; parent folder must exist.",
      },
      sprite_asset_path: {
        type: "string",
        description: "Optional Sprite asset path to seed the Tile's sprite.",
      },
      tile_name: {
        type: "string",
        description: "Optional Tile name (defaults to the asset filename).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — must include the new .asset path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
