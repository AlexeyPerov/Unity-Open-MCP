import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.1 — SpriteAtlas domain tool. Read-only: the atlas
// authoring state (packables + packing/texture/platform settings). Gate-free.
export const spriteatlasGet = makeTool(
  "unity_open_mcp_spriteatlas_get",
  "Read-only: SpriteAtlas snapshot — packables (asset paths + type), " +
    "include-in-build, is-variant, packing settings (blockOffset / padding / " +
    "enableRotation / enableTightPacking / enableAlphaDilation), texture " +
    "settings (maxTextureSize / anisoLevel / filterMode / generateMipMaps / " +
    "readable / sRGB), and the default platform settings (maxTextureSize / " +
    "format / textureCompression). Gate-free.",
  {
    required: ["asset_path"],
        properties: {
          asset_path: {
            type: "string",
            description: "Assets/-rooted .spriteatlas asset path.",
          },
        },
  },
);
