import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.1 — Texture domain tool. Read-only: the runtime Texture
// info (width / height / format / filterMode) for a loaded asset. Complementary
// to texture_get_importer (the import-pipeline config). Gate-free.
export const textureGet: Tool = {
  name: "unity_open_mcp_texture_get",
  description:
    "Read-only: the runtime Texture info (width / height / format / " +
    "mipmapCount / filterMode / anisoLevel / wrapMode / isReadable) for a " +
    "loaded texture asset. Complementary to texture_get_importer (the import-" +
    "pipeline config). Gate-free. Built-in 2D module; the 2d group is hidden " +
    "until manage_tools activates it.",
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
