import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 9 — typed build-scenes setter. Mutating: replaces
// EditorBuildSettings.scenes with the supplied list. Folds UCP build/set-scenes.
//
// paths_hint: EditorBuildSettings.asset lives under ProjectSettings/. Scope
// paths_hint to "ProjectSettings" (or "ProjectSettings/EditorBuildSettings.asset").
export const buildSetScenes: Tool = {
  name: "unity_open_mcp_build_set_scenes",
  description:
    "Mutating: replace the build scene list (EditorBuildSettings.scenes). Each entry is either " +
    "{path, enabled?} (enabled defaults to true) or a bare path string (treated as enabled). " +
    "The full list is replaced — pass the complete set you want in build settings. Runs the " +
    "full gate path; `paths_hint` should scope to \"ProjectSettings\" " +
    "(EditorBuildSettings.asset lives under ProjectSettings/).",
  inputSchema: {
    type: "object",
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
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — pass [\"ProjectSettings\"] (or " +
          "[\"ProjectSettings/EditorBuildSettings.asset\"]). There is no whole-project fallback.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
