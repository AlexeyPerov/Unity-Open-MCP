import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 6 / T20.6.3 — Tilemap set_tile. Compile-gated. Mutating.
export const tilemapSetTile = makeTool(
  "unity_open_mcp_tilemap_set_tile",
  "Paint a single tile at a cell coordinate. Address the Tilemap by " +
    "instance_id > path > name; address the TileBase by tile_asset_path. " +
    "x / y are the cell coordinates (z defaults to 0). Mutating: runs the " +
    "full gate path; paths_hint is the host scene path. Requires " +
    "com.unity.2d.tilemap.",
  {
    required: ["tile_asset_path", "paths_hint"],
        properties: {
          instance_id: { type: "integer", description: "Tilemap host GameObject instance id." },
          path: { type: "string", description: "Host hierarchy path." },
          name: { type: "string", description: "Host name (last-resort resolver)." },
          tile_asset_path: {
            type: "string",
            description: "Asset path to a TileBase (.asset).",
          },
          x: { type: "integer", default: 0, description: "Cell x coordinate." },
          y: { type: "integer", default: 0, description: "Cell y coordinate." },
          z: { type: "integer", default: 0, description: "Cell z coordinate." },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host scene path." },
          gate: { ...GATE_PROP },
        },
  },
);
