import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 3 — typed scene set active. Mutating: marks an opened scene as
// the active scene. Runs the full gate path.
export const sceneSetActive: Tool = {
  name: "unity_open_mcp_scene_set_active",
  description:
    "Mark an opened scene as the editor's active scene (the scene new GameObjects are added to " +
    "and the default for many operations). Mutating: runs the full gate path; `paths_hint` should " +
    "be the scene's asset path. The target scene must already be opened (use scene_open first). " +
    "Returns the post-set list of opened scenes (name/path/isDirty/isLoaded/rootCount/buildIndex/" +
    "isActive) so the agent can confirm which scene is now active. Prefer this over raw " +
    "execute_csharp EditorSceneManager.SetActiveScene.",
  inputSchema: {
    type: "object",
    required: ["name", "paths_hint"],
    properties: {
      name: {
        type: "string",
        description:
          "Name of the opened scene to mark as active. Must match an opened scene. Use " +
          "scene_list_opened to enumerate opened scene names.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the scene's asset path.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
