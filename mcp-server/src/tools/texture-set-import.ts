import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.1 — Texture domain tool. Mutating: patch the
// TextureImporter and reimport via a structured settings_json object. Folds
// sprite + normal-map presets in (sprite_mode / normalmap keys). Runs the full
// gate path (EditorSettle — the reimport can take seconds and may trigger a
// platform-switch domain reload); paths_hint is the texture asset path.
export const textureSetImport: Tool = {
  name: "unity_open_mcp_texture_set_import",
  description:
    "Mutating: patch a TextureImporter and reimport the texture. settings_json " +
    "is a JSON object with optional keys: texture_type " +
    "(Default|NormalMap|Sprite|Cursor|Cookie|Lightmap|SingleChannel|...), " +
    "texture_shape (Texture2D|TextureCube), npot_scale " +
    "(None|ToNearest|ToLarger|ToSmaller), max_texture_size (32|64|...|8192), " +
    "compression (None|Uncompressed|Compressed|CompressedHQ|CompressedLQ), " +
    "compression_quality (0-100), crunched (bool), srgb (bool), readable " +
    "(bool), mipmap_enabled (bool), filter_mode (Point|Bilinear|Trilinear), " +
    "aniso_level (0-16), wrap_mode (Repeat|Clamp|Mirror|MirrorOnce), " +
    "alpha_is_transparency (bool), sprite_mode (None|Single|Multiple|Polygon), " +
    "sprite_pixels_per_unit (float), normalmap (bool). Each key is optional — " +
    "omit to leave unchanged. Unknown keys are reported in `unknownFields`, " +
    "not fatal. The reimport runs through the gate (editor_settle) so the next " +
    "mutation sees the settled texture. Mutating: paths_hint is the asset path. " +
    "Built-in 2D module; the 2d group is hidden until manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "settings_json", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "Assets/-rooted texture asset path.",
      },
      settings_json: {
        type: "string",
        description:
          'JSON object string. e.g. ' +
          '"{"compression":"Compressed","max_texture_size":1024,"sprite_mode":"Single"}". ' +
          "Sprite preset: sprite_mode (Single|Multiple|Polygon) switches " +
          "textureType to Sprite. Normal-map preset: normalmap:true switches " +
          "textureType to NormalMap. Unknown keys reported in unknownFields.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the texture asset path.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
