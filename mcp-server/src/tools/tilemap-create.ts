import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 6 / T20.6.3 — Tilemap create. Compile-gated in the bridge
// (UNITY_OPEN_MCP_EXT_TILEMAP on com.unity.2d.tilemap). Mutating: adds a Grid
// + child Tilemap GameObject to the active scene.
export const tilemapCreate = makeTool(
  "unity_open_mcp_tilemap_create",
  "Create a Grid GameObject in the active scene with a child Tilemap + " +
    "TilemapRenderer. grid_name optionally names the Grid root; tilemap_name " +
    "optionally names the child Tilemap. Returns the Grid and Tilemap instance " +
    "ids + paths. Mutating: runs the full gate path; paths_hint is the active " +
    "scene path. Requires the com.unity.2d.tilemap package installed.",
  {
    required: ["paths_hint"],
        properties: {
          grid_name: { type: "string", description: "Optional Grid GameObject name." },
          tilemap_name: { type: "string", description: "Optional child Tilemap GameObject name." },
          parent_path: {
            type: "string",
            description: "Optional slash-separated hierarchy path to a parent GameObject.",
          },
          position: { type: "string", description: "Optional position as 'x,y,z'." },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the active scene path." },
          gate: { ...GATE_PROP },
        },
  },
);
