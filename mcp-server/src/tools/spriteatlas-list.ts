import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.1 — SpriteAtlas domain tool. Read-only: list SpriteAtlas
// assets under a folder (default: whole project). Gate-free; offline-routeable
// in principle.
export const spriteatlasList: Tool = {
  name: "unity_open_mcp_spriteatlas_list",
  description:
    "Read-only: list SpriteAtlas (.spriteatlas) asset paths under a folder " +
    "(omit folder to search the whole project). Each entry reports path + " +
    "name. Cap 200; truncated count reported. Gate-free.",
  inputSchema: {
    type: "object",
    properties: {
      folder: {
        type: "string",
        description:
          "Assets/-rooted folder to search under (default: 'Assets' = whole " +
          "project).",
      },
    },
    additionalProperties: false,
  },
};
