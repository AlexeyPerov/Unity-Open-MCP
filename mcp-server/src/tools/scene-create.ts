import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 3 — typed scene create. Mutating: writes a new .unity asset and
// opens it. Runs the full gate path; `paths_hint` is the new .unity path.
export const sceneCreate = makeTool(
  "unity_open_mcp_scene_create",
  "Create a new scene asset and save it at the given `.unity` path, opening it in the editor. " +
    "Mutating: runs the full gate path; `paths_hint` should be the new `.unity` asset path. " +
    "Returns the new scene's name, path, isDirty, isLoaded, rootCount, and buildIndex so the " +
    "agent can chain scene_get_data / gameobject_create / scene_save without an extra lookup. " +
    "Prefer this over raw execute_csharp EditorSceneManager.NewScene — schema-validated, " +
    "undo-recorded, and the gate surfaces asset-reference fallout.",
  {
    required: ["path", "paths_hint"],
        properties: {
          path: {
            type: "string",
            description:
              "Destination asset path. Must start with 'Assets/' (or be relative to the project " +
              "root) and end with '.unity'. Intermediate folders must already exist.",
          },
          setup: {
            type: "string",
            enum: ["empty", "default"],
            default: "empty",
            description:
              "New-scene setup. 'empty' = NewSceneSetup.EmptyScene (no objects); 'default' = " +
              "NewSceneSetup.DefaultGameObjects (camera + directional light). Defaults to 'empty'.",
          },
          mode: {
            type: "string",
            enum: ["single", "additive"],
            default: "single",
            description:
              "New-scene mode. 'single' closes currently opened scenes and opens this one; " +
              "'additive' keeps currently opened scenes and opens this one alongside them.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the new `.unity` asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
