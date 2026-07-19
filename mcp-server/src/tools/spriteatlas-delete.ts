import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.1 — SpriteAtlas domain tool. Mutating (destructive):
// delete the .spriteatlas asset. Runs the full gate path (EditorSettle);
// paths_hint is the asset path.
export const spriteatlasDelete = makeTool(
  "unity_open_mcp_spriteatlas_delete",
  "Delete a SpriteAtlas asset (.spriteatlas). Refuses when the path does " +
    "not point at a SpriteAtlas. Mutating (destructive): runs the full gate " +
    "path (editor_settle); paths_hint is the asset path. There is no undo " +
    "across a Unity restart. Built-in 2D module; the sprite2d group is hidden until " +
    "manage_tools activates it.",
  {
    required: ["asset_path", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description: "Assets/-rooted .spriteatlas asset path.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the asset path to delete." },
          gate: { ...GATE_PROP },
        },
  },
);
