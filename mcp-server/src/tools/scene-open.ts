import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 3 — typed scene open. Mutating: closes/keeps currently opened
// scenes and loads the target scene asset. Runs the full gate path.
export const sceneOpen: Tool = {
  name: "unity_open_mcp_scene_open",
  description:
    "Open a Unity scene asset from disk in Single or Additive mode. Mutating: runs the full gate " +
    "path; `paths_hint` should be the target `.unity` asset path (and any currently-open scene " +
    "paths when `mode: 'single'` closes them). Single mode closes currently opened scenes (Unity " +
    "may prompt to save dirty scenes — see `ignore_scene_dirty`); Additive keeps them open. " +
    "Returns the post-open list of all opened scenes (name/path/isDirty/isLoaded/rootCount/" +
    "buildIndex/isActive) so the agent can confirm state and chain scene_set_active / scene_get_data. " +
    "Prefer this over raw execute_csharp EditorSceneManager.OpenScene.",
  inputSchema: {
    type: "object",
    required: ["path", "paths_hint"],
    properties: {
      path: {
        type: "string",
        description:
          "Asset path of the scene to open (must end with '.unity'). Must exist on disk.",
      },
      mode: {
        type: "string",
        enum: ["single", "additive"],
        default: "single",
        description:
          "'single' (default) closes currently opened scenes and opens this one; 'additive' " +
          "keeps currently opened scenes open and opens this one alongside them.",
      },
      ignore_scene_dirty: {
        type: "boolean",
        default: false,
        description:
          "When true, proceed even if currently opened scenes are dirty (Unity may otherwise " +
          "prompt to save). Default false: the bridge refuses when opened scenes are dirty to " +
          "avoid losing unsaved changes.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the target `.unity` asset path. Include currently-open scene paths " +
          "too when `mode: 'single'` would close them.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
