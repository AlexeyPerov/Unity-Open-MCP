import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 6 / T20.6.3 — Tilemap box_fill. Compile-gated. Mutating.
export const tilemapBoxFill: Tool = {
  name: "unity_open_mcp_tilemap_box_fill",
  description:
    "Fill a rectangular cell region with a tile. x1 / y1 and x2 / y2 are the " +
    "opposite corners (inclusive); z defaults to 0. Address the Tilemap by " +
    "instance_id > path > name and the tile by tile_asset_path. Mutating: runs " +
    "the full gate path; paths_hint is the host scene path. Requires " +
    "com.unity.2d.tilemap.",
  inputSchema: {
    type: "object",
    required: ["tile_asset_path", "paths_hint"],
    properties: {
      instance_id: { type: "integer", description: "Tilemap host GameObject instance id." },
      path: { type: "string", description: "Host hierarchy path." },
      name: { type: "string", description: "Host name (last-resort resolver)." },
      tile_asset_path: { type: "string", description: "Asset path to a TileBase (.asset)." },
      x1: { type: "integer", description: "First corner cell x." },
      y1: { type: "integer", description: "First corner cell y." },
      x2: { type: "integer", description: "Opposite corner cell x." },
      y2: { type: "integer", description: "Opposite corner cell y." },
      z: { type: "integer", default: 0, description: "Cell z plane." },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the host scene path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
