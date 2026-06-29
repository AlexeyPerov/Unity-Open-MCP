import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 4 / T20.4 — Terrain domain tool. Built-in Terrain module
// (Terrain / TerrainData / TreePrototype / TerrainLayer) — ungated in the
// bridge (no UNITY_OPEN_MCP_EXT_TERRAIN define), always compiled. The
// `terrain` group is hidden until manage_tools activates it. Mutating: runs
// the full gate path; paths_hint is the new terrain's scene path + the asset
// path (when provided). Param shape: width / length / height /
// heightmapResolution / position / dataPath.
const HEIGHTMAP_RESOLUTIONS =
  "Power-of-two plus one (33 | 65 | 129 | 257 | 513 | 1025 | 2049 | 4097).";

export const terrainCreate: Tool = {
  name: "unity_open_mcp_terrain_create",
  description:
    "Create a Terrain (allocates TerrainData + a Terrain GameObject). Optional: " +
    "width (X size, default 500), length (Z size, default 500), height (Y max, " +
    "default 200), heightmap_resolution (" + HEIGHTMAP_RESOLUTIONS + " default 129), " +
    "position ('x,y,z' world-space), asset_path (Assets/.../.asset for the " +
    "TerrainData — when omitted, the TerrainData is in-scene only and is NOT saved " +
    "to disk). Mutating: runs the full gate path; paths_hint is the scene path + " +
    "the asset path (when provided). Built-in Terrain module (no package " +
    "dependency); the terrain group is hidden until manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      terrain_name: {
        type: "string",
        default: "Terrain",
        description: "Name for the new Terrain GameObject.",
      },
      width: {
        type: "number",
        default: 500,
        description: "Terrain width in world units (X). Must be positive.",
      },
      length: {
        type: "number",
        default: 500,
        description: "Terrain length in world units (Z). Must be positive.",
      },
      height: {
        type: "number",
        default: 200,
        description: "Maximum terrain height in world units (Y). Must be positive.",
      },
      heightmap_resolution: {
        type: "integer",
        default: 129,
        description: "Heightmap resolution. " + HEIGHTMAP_RESOLUTIONS,
      },
      position: {
        type: "string",
        description: "World-space position 'x,y,z' for the Terrain GameObject.",
      },
      asset_path: {
        type: "string",
        description:
          "Assets/.../.asset path to save the TerrainData. Must start with " +
          "'Assets/' and end in '.asset', and the path must be unique. When " +
          "omitted, the TerrainData is in-scene only and is NOT saved to disk.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the new terrain's scene path. When asset_path is " +
          "provided, include BOTH the scene path and the .asset path so the gate " +
          "validates both. No whole-project fallback.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
