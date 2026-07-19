import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 4 / T20.4 — Terrain domain tool. Set neighbor Terrains for LOD
// stitching. Built-in Terrain module — ungated. Mutating: runs the full gate
// path; paths_hint is the host scene path. Param shape: left / top / right /
// bottom neighbors.
const targetSchema = {
  instance_id: {
    type: ["string", "integer"],
    default: 0,
    description: "Host Terrain GameObject instance ID. Highest priority resolver.",
  },
  path: {
    type: "string",
    description: "Host Terrain GameObject hierarchy path \"Root/Child\".",
  },
  name: {
    type: "string",
    description: "Host Terrain GameObject name (first match). Lowest priority resolver.",
  },
  paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host scene path. No whole-project fallback." },
  gate: { ...GATE_PROP },
};

const neighborSchema = (side: string) => ({
  type: "string",
  description:
    `${side} neighbor Terrain GameObject hierarchy path. Pass an empty value or ` +
    "omit to clear the side. The GameObject must carry a Terrain component.",
});

const neighborInstanceSchema = (side: string) => ({
  type: "integer",
  default: 0,
  description: `${side} neighbor Terrain GameObject instance ID (highest priority).`,
});

export const terrainSetNeighbors = makeTool(
  "unity_open_mcp_terrain_set_neighbors",
  "Set neighbor Terrains for LOD stitching. Each side (top / bottom / left / " +
    "right) accepts a terrain GameObject resolved by instance_id > path. Pass an " +
    "empty value or omit a side to clear it. A terrain cannot be its own neighbor. " +
    "Idempotent — re-setting the same neighbors reports set:true. Mutating: runs " +
    "the full gate path; paths_hint is the host scene path. Built-in Terrain module " +
    "(no package dependency); the terrain group is hidden until manage_tools " +
    "activates it.",
  {
    required: ["paths_hint"],
        properties: {
          ...targetSchema,
          top_path: neighborSchema("Top"),
          top_instance_id: neighborInstanceSchema("Top"),
          bottom_path: neighborSchema("Bottom"),
          bottom_instance_id: neighborInstanceSchema("Bottom"),
          left_path: neighborSchema("Left"),
          left_instance_id: neighborInstanceSchema("Left"),
          right_path: neighborSchema("Right"),
          right_instance_id: neighborInstanceSchema("Right"),
        },
  },
);
