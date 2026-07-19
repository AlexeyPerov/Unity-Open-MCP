import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 9 — typed build-scenes setter. Mutating: replaces
// EditorBuildSettings.scenes with the supplied list.
//
// paths_hint: EditorBuildSettings.asset lives under ProjectSettings/. Scope
// paths_hint to "ProjectSettings" (or "ProjectSettings/EditorBuildSettings.asset").
export const buildSetScenes = makeTool(
  "unity_open_mcp_build_set_scenes",
  "Mutating: replace the build scene list (EditorBuildSettings.scenes). Each entry is either " +
    "{path, enabled?} (enabled defaults to true) or a bare path string (treated as enabled). " +
    "The full list is replaced — pass the complete set you want in build settings. Runs the " +
    "full gate path; `paths_hint` should scope to \"ProjectSettings\" " +
    "(EditorBuildSettings.asset lives under ProjectSettings/).",
  {
    required: ["scenes", "paths_hint"],
        properties: {
          scenes: {
            type: "array",
            description:
              "Scene entries. Each is either {path, enabled?} or a bare path string. The full list " +
              "REPLACES the current build scene list.",
            items: {
              oneOf: [
                {
                  type: "object",
                  required: ["path"],
                  properties: {
                    path: {
                      type: "string",
                      description: "Scene asset path, e.g. 'Assets/Scenes/Main.unity'.",
                    },
                    enabled: {
                      type: "boolean",
                      default: true,
                      description: "Whether the scene is included in the build (enabled in the list).",
                    },
                  },
                  additionalProperties: false,
                },
                {
                  type: "string",
                  description: "Bare scene asset path — treated as enabled.",
                },
              ],
            },
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — pass [\"ProjectSettings\"] (or " + "[\"ProjectSettings/EditorBuildSettings.asset\"]). There is no whole-project fallback." },
          gate: { ...GATE_PROP },
        },
  },
);
