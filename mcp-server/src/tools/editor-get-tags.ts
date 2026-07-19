import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 5 — typed editor tags read. Read-only: lists every configured tag
// from the TagManager. Gate-free. Pair with editor_add_tag for the mutating
// side.
export const editorGetTags = makeTool(
  "unity_open_mcp_editor_get_tags",
  "List every tag configured in the project's TagManager (the Tags list " +
    "under Edit → Project Settings → Tags and Layers). Includes Unity's " +
    "built-in tags (Untagged, Respawn, Finish, EditorOnly, MainCamera, " +
    "Player, GameController) plus user-defined tags. Read-only and gate-free. " +
    "Use this before gameobject_modify (tag) to discover valid tag names, and " +
    "before editor_add_tag to check whether a tag already exists. Prefer this " +
    "over raw execute_csharp InternalEditorUtility.tags — schema-validated.",
  {
    properties: {},
  },
);
