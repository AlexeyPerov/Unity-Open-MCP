import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, IGNORE_SCENE_DIRTY_BASE, makeTool } from "./schema-fragments.js";

// M16 Plan 3 — typed scene open. Mutating: closes/keeps currently opened
// scenes and loads the target scene asset. Runs the full gate path.
export const sceneOpen = makeTool(
  "unity_open_mcp_scene_open",
  "Open a Unity scene asset from disk in Single or Additive mode. Mutating: runs the full gate " +
    "path; `paths_hint` should be the target `.unity` asset path (and any currently-open scene " +
    "paths when `mode: 'single'` closes them). Single mode closes currently opened scenes (Unity " +
    "may prompt to save dirty scenes — see `ignore_scene_dirty`); Additive keeps them open. " +
    "Returns the post-open list of all opened scenes (name/path/isDirty/isLoaded/rootCount/" +
    "buildIndex/isActive) so the agent can confirm state and chain scene_set_active / scene_get_data. " +
    "Prefer this over raw execute_csharp EditorSceneManager.OpenScene.",
  {
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
          ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "When true, proceed even if currently opened scenes are dirty (Unity may otherwise " + "prompt to save). Default false: the bridge refuses when opened scenes are dirty to " + "avoid losing unsaved changes." },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the target `.unity` asset path. Include currently-open scene paths " + "too when `mode: 'single'` would close them." },
          gate: { ...GATE_PROP },
        },
  },
);
