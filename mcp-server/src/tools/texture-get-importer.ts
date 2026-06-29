import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.1 — Texture domain tool. Read-only: the TextureImporter
// settings (the import-pipeline config) for a texture asset. Gate-free.
export const textureGetImporter: Tool = {
  name: "unity_open_mcp_texture_get_importer",
  description:
    "Read-only: the TextureImporter settings (the import-pipeline config) for " +
    "a texture asset. Reports textureType / textureShape / npotScale / " +
    "maxTextureSize / textureCompression / compressionQuality / sRGBTexture / " +
    "isReadable / mipmapEnabled / filterMode / anisoLevel / wrapMode / " +
    "alphaIsTransparency / spriteImportMode / spritePixelsPerUnit / normalmap " +
    "/ crunchedCompression. Gate-free. Use texture_get for the runtime Texture " +
    "info (width / height / format). Built-in 2D module; the 2d group is " +
    "hidden until manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["asset_path"],
    properties: {
      asset_path: {
        type: "string",
        description: "Assets/-rooted texture asset path.",
      },
    },
    additionalProperties: false,
  },
};
