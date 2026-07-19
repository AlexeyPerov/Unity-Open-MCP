import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 4 / T20.4 — Terrain domain tool. Set a heightmap region from a 2D
// array of normalized 0-1 values. Built-in Terrain module — ungated. Mutating:
// runs the full gate path; paths_hint is the host scene path. Param shape:
// xBase / yBase + 2D heights. The 2D array is row-major
// [[r0c0,r0c1,...],[r1c0,...],...].
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

export const terrainSetHeights = makeTool(
  "unity_open_mcp_terrain_set_heights",
  "Set a heightmap region. heights is a row-major 2D JSON array of normalized " +
    "0-1 values ([[r0c0,r0c1,...],[r1c0,...],...]); x_offset / y_offset position " +
    "the region inside the heightmap. Arrays larger than 513x513 per call are " +
    "refused — write in tiles via repeated calls with different offsets instead. " +
    "Mutating: runs the full gate path; paths_hint is the host scene path. Built-in " +
    "Terrain module (no package dependency); the terrain group is hidden until " +
    "manage_tools activates it.",
  {
    required: ["heights", "paths_hint"],
        properties: {
          ...targetSchema,
          x_offset: {
            type: "integer",
            default: 0,
            description: "Start X index in the heightmap for the region write.",
          },
          y_offset: {
            type: "integer",
            default: 0,
            description: "Start Y index in the heightmap for the region write.",
          },
          heights: {
            type: "array",
            items: {
              type: "array",
              items: { type: "number" },
            },
            description:
              "Row-major 2D array of normalized 0-1 height values. Each row must have " +
              "the same length. The array shape defines the region size. Capped at " +
              "513x513 per call — tile large writes via x_offset / y_offset.",
          },
        },
  },
);
