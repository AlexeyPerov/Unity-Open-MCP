import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.1 — Texture domain tool. Read-only: the runtime Texture
// info (width / height / format / filterMode) for a loaded asset. Complementary
// to texture_get_importer (the import-pipeline config). Gate-free.
export const textureGet = makeTool(
  "unity_open_mcp_texture_get",
  "Read-only: the runtime Texture info (width / height / format / " +
    "mipmapCount / filterMode / anisoLevel / wrapMode / isReadable) for a " +
    "loaded texture asset. Complementary to texture_get_importer (the import-" +
    "pipeline config). Gate-free. Built-in 2D module; the sprite2d group is hidden " +
    "until manage_tools activates it.",
  {
    required: ["asset_path"],
        properties: {
          asset_path: {
            type: "string",
            description: "Assets/-rooted texture asset path.",
          },
        },
  },
);
