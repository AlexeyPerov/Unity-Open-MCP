import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 4 / T20.4 — Terrain domain tool. Place tree instances on a terrain.
// Built-in Terrain module — ungated. Mutating: runs the full gate path;
// paths_hint is the host scene path.
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
  paths_hint: {
    type: "array",
    items: { type: "string" },
    description: "Mutation scope — the host scene path. No whole-project fallback.",
  },
  gate: {
    enum: ["enforce", "warn", "off"],
    default: "enforce",
  },
};

export const terrainPlaceTrees: Tool = {
  name: "unity_open_mcp_terrain_place_trees",
  description:
    "Place tree instances on a terrain. instances is an array of {position " +
    "('x,z' normalized 0-1 on the terrain), height_scale (default 1), " +
    "width_scale (default 1), rotation (degrees, default 0)}. " +
    "tree_prototype_index selects the prototype. When the index is new (>= the " +
    "terrain's current prototype count), a prototype is seeded from " +
    "prototype_prefab_path (a .prefab asset under Assets/). Mutating: runs the " +
    "full gate path; paths_hint is the host scene path. Built-in Terrain module " +
    "(no package dependency); the terrain group is hidden until manage_tools " +
    "activates it.",
  inputSchema: {
    type: "object",
    required: ["tree_prototype_index", "instances", "paths_hint"],
    properties: {
      ...targetSchema,
      tree_prototype_index: {
        type: "integer",
        minimum: 0,
        description:
          "Tree prototype index. When >= the terrain's current prototype count, " +
          "a prototype is seeded from prototype_prefab_path.",
      },
      instances: {
        type: "array",
        minItems: 1,
        items: {
          type: "object",
          properties: {
            position: {
              type: "string",
              description:
                "Tree position as 'x,z' normalized 0-1 on the terrain surface " +
                "(0,0 is one corner, 1,1 the opposite).",
            },
            height_scale: {
              type: "number",
              default: 1,
              description: "Height multiplier (1 = prototype's authored height).",
            },
            width_scale: {
              type: "number",
              default: 1,
              description: "Width multiplier (1 = prototype's authored width).",
            },
            rotation: {
              type: "number",
              default: 0,
              description: "Rotation around Y in degrees.",
            },
          },
          required: ["position"],
          additionalProperties: false,
        },
        description: "Tree instances to place. At least one is required.",
      },
      prototype_prefab_path: {
        type: "string",
        description:
          "Assets/.../.prefab path. Required when tree_prototype_index is new " +
          "(>= the terrain's current prototype count); the prefab's GameObject " +
          "becomes the prototype.",
      },
    },
    additionalProperties: false,
  },
};
