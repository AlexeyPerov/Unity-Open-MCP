import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 5 — typed editor add tag. Mutating: appends a user tag to the
// TagManager (ProjectSettings/TagManager.asset) and saves it. Runs the full
// gate path with paths_hint scoped to that asset. Idempotent: a tag that
// already exists is a no-op (saved:false). Built-in tag names are reserved.
export const editorAddTag = makeTool(
  "unity_open_mcp_editor_add_tag",
  "Add a user-defined tag to the project's TagManager. Mutating: appends the " +
    "tag, writes ProjectSettings/TagManager.asset, and refreshes the asset " +
    "database. Runs the full gate path; paths_hint should be " +
    "[\"ProjectSettings/TagManager.asset\"]. Idempotent — adding a tag that " +
    "already exists returns saved:false (no write). Refuses reserved built-in " +
    "tag names (Untagged, Respawn, Finish, EditorOnly, MainCamera, Player, " +
    "GameController) and invalid names. Prefer this over raw execute_csharp " +
    "TagManager manipulation — schema-validated, undo-recorded, and the gate " +
    "surfaces any fallout from the TagManager rewrite. Pair with " +
    "editor_get_tags to confirm the addition.",
  {
    required: ["tag", "paths_hint"],
        properties: {
          tag: {
            type: "string",
            description:
              "The tag name to add. Must be non-empty and not a reserved built-in " +
              "tag. Leading/trailing whitespace is trimmed.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — [\"ProjectSettings/TagManager.asset\"] (the asset " + "this tool rewrites)." },
          gate: { ...GATE_PROP },
        },
  },
);
