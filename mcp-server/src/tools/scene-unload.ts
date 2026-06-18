import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 3 — typed scene unload. Mutating: removes an opened scene from
// the editor (without saving). Runs the full gate path.
export const sceneUnload: Tool = {
  name: "unity_open_mcp_scene_unload",
  description:
    "Unload an opened scene from the Unity Editor (without saving — call scene_save first if " +
    "the scene has unsaved changes you want to keep). Mutating: runs the full gate path; " +
    "`paths_hint` should be the scene's asset path. Returns the post-unload list of opened " +
    "scenes (name/path/isDirty/isLoaded/rootCount/buildIndex/isActive). Prefer this over raw " +
    "execute_csharp SceneManager.UnloadSceneAsync.",
  inputSchema: {
    type: "object",
    required: ["name", "paths_hint"],
    properties: {
      name: {
        type: "string",
        description:
          "Name of the opened scene to unload. Must match an opened scene. Use " +
          "scene_list_opened to enumerate opened scene names.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the unloaded scene's asset path (or its name when it is not yet " +
          "asset-backed).",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
