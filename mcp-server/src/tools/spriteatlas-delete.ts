import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.1 — SpriteAtlas domain tool. Mutating (destructive):
// delete the .spriteatlas asset. Runs the full gate path (EditorSettle);
// paths_hint is the asset path.
export const spriteatlasDelete: Tool = {
  name: "unity_open_mcp_spriteatlas_delete",
  description:
    "Delete a SpriteAtlas asset (.spriteatlas). Refuses when the path does " +
    "not point at a SpriteAtlas. Mutating (destructive): runs the full gate " +
    "path (editor_settle); paths_hint is the asset path. There is no undo " +
    "across a Unity restart. Built-in 2D module; the 2d group is hidden until " +
    "manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "Assets/-rooted .spriteatlas asset path.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the asset path to delete.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
