import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.1 — SpriteAtlas domain tool. Built-in 2D module (no
// extra UPM); the `2d` tool group is hidden until manage_tools activates it.
// Mutating: runs the full gate path (EditorSettle — the .spriteatlas asset is
// written + reimported); paths_hint is the new asset path.
export const spriteatlasCreate: Tool = {
  name: "unity_open_mcp_spriteatlas_create",
  description:
    "Create a new SpriteAtlas asset at an Assets/-rooted .spriteatlas path. " +
    "Intermediate folders are created if missing. include_in_build defaults to " +
    "true (the atlas ships with the player build). Mutating: runs the full gate " +
    "path (editor_settle lifecycle — the .spriteatlas asset is written + " +
    "reimported); paths_hint is the new asset path. Built-in 2D module (no " +
    "package dependency); the 2d group is hidden until manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description:
          "Destination asset path. Must start with 'Assets/' and end with " +
          "'.spriteatlas'. Intermediate folders are created if missing.",
      },
      include_in_build: {
        type: "boolean",
        default: true,
        description:
          "Whether the atlas ships with the player build (SpriteAtlas " +
          "includeInBuild). Defaults to true.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the new .spriteatlas asset path.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
