import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.1 — SpriteAtlas domain tool. Mutating: patch
// include_in_build + packing + texture settings via a structured JSON object.
// Runs the full gate path (EditorSettle); paths_hint is the atlas asset path.
// Unknown fields are reported, not fatal.
export const spriteatlasModify: Tool = {
  name: "unity_open_mcp_spriteatlas_modify",
  description:
    "Mutating: patch SpriteAtlas settings — include_in_build (bool), packing " +
    "(blockOffset / padding / enableRotation / enableTightPacking / " +
    "enableAlphaDilation), and texture (anisoLevel / filterMode / " +
    "generateMipMaps / readable / sRGB). settings_json is a JSON object with " +
    "three optional sub-objects: {include_in_build, packing:{...}, texture:{...}}. " +
    "Unknown fields are reported in `unknownFields`, not fatal. NOTE: packing and " +
    "texture settings are applied to the in-memory asset (take effect for the " +
    "next pack) but are NOT written to the .spriteatlas file in this Unity " +
    "version — Unity manages them via the internal packing pipeline. Mutating: " +
    "runs the full gate path (editor_settle); paths_hint is the .spriteatlas " +
    "asset path. Built-in 2D module; the sprite2d group is hidden until " +
    "manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "settings_json", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "Assets/-rooted .spriteatlas asset path.",
      },
      settings_json: {
        type: "string",
        description:
          'JSON object string. e.g. ' +
          '"{"include_in_build":false,"packing":{"padding":4,"enableRotation":true},"texture":{"anisoLevel":1,"filterMode":"Bilinear"}}". ' +
          "Top-level keys: include_in_build (bool). Sub-objects: packing " +
          "{blockOffset(int), padding(int), enableRotation(bool), " +
          "enableTightPacking(bool), enableAlphaDilation(bool)}, texture " +
          "{anisoLevel(int), filterMode(Point|Bilinear|Trilinear), " +
          "generateMipMaps(bool), readable(bool), sRGB(bool)}. NOTE: packing " +
          "and texture settings are applied to the in-memory asset (take effect " +
          "for the next pack) but are NOT written to the .spriteatlas file in " +
          "this Unity version — Unity manages them via the internal packing " +
          "pipeline. texture.maxTextureSize is not settable here (no setter on " +
          "the settings struct; controlled via platform settings).",
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
