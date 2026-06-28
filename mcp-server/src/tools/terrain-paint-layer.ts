import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 4 / T20.4 — Terrain domain tool. Paint one terrain layer's splat
// from a 2D alphamap. Built-in Terrain module — ungated. Mutating: runs the
// full gate path; paths_hint is the scene path (+ the layer asset path when a
// new layer is created)
const targetSchema = {
  instance_id: {
    type: "integer",
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
  paths_hint: {
    type: "array",
    items: { type: "string" },
    description:
      "Mutation scope — the host scene path. When a new TerrainLayer asset is " +
      "created (layer_path under Assets/ and a fresh layer), include the layer " +
      ".terrainlayer asset path too. No whole-project fallback.",
  },
  gate: {
    enum: ["enforce", "warn", "off"],
    default: "enforce",
  },
};

export const terrainPaintLayer: Tool = {
  name: "unity_open_mcp_terrain_paint_layer",
  description:
    "Paint a terrain layer's splat in a region. alphamap is a row-major 2D JSON " +
    "array of 0-1 weights for layer_index; x_offset / y_offset position the region " +
    "inside the alphamap. When layer_index is new (>= the terrain's current layer " +
    "count), a new TerrainLayer is added — either loaded from layer_path (an " +
    "existing .terrainlayer asset) or created fresh at that path. The standard " +
    "terrain shader caps layers at 8. Arrays larger than 513x513 per call are " +
    "refused — write in tiles. Mutating: runs the full gate path; paths_hint is the " +
    "scene path (+ the layer asset path when a new layer is created). Built-in " +
    "Terrain module (no package dependency); the terrain group is hidden until " +
    "manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["layer_index", "alphamap", "paths_hint"],
    properties: {
      ...targetSchema,
      layer_index: {
        type: "integer",
        minimum: 0,
        description:
          "Terrain layer index to paint. When >= the terrain's current layer count, " +
          "a new TerrainLayer is seeded (requires layer_path). Capped at 8 for the " +
          "standard terrain shader.",
      },
      x_offset: {
        type: "integer",
        default: 0,
        description: "Start X index in the alphamap for the region write.",
      },
      y_offset: {
        type: "integer",
        default: 0,
        description: "Start Y index in the alphamap for the region write.",
      },
      alphamap: {
        type: "array",
        items: {
          type: "array",
          items: { type: "number" },
        },
        description:
          "Row-major 2D array of normalized 0-1 weights for this layer. Each row " +
          "must have the same length. Capped at 513x513 per call — tile large writes.",
      },
      layer_path: {
        type: "string",
        description:
          "Optional Assets/.../.terrainlayer path. When layer_index is new, the " +
          "layer is loaded from this path (if the asset exists) or created fresh " +
          "there (if the path is a valid Assets/.../.terrainlayer). When omitted " +
          "and the index is new, an in-scene-only (unsaved) layer is created.",
      },
    },
    additionalProperties: false,
  },
};
