import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 2 / T20.2.3 — Lighting domain tool. Skybox is a scene-environment
// setting — RenderSettings is scene-scoped, and the active scene is marked
// dirty so the write persists. Built-in lighting module. Mutating: runs the
// full gate path.
export const skyboxSet = makeTool(
  "unity_open_mcp_skybox_set",
  "Assign RenderSettings.skybox from a material asset path. Pass " +
    "material_path: null to clear the skybox. Skybox is a scene-environment " +
    "setting — the active scene is marked dirty so the write persists (call " +
    "scene_save to commit). The ambient/indirect environment is refreshed via " +
    "DynamicGI.UpdateEnvironment. Mutating: runs the full gate path; paths_hint " +
    "covers the active scene path and the material asset path.",
  {
    required: ["paths_hint"],
        properties: {
          material_path: {
            type: "string",
            description:
              "Assets/-rooted .mat path to assign as the skybox, or null/omitted " +
              "to clear.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the active scene path (skybox is a scene-environment " + "setting) and/or the material asset path. There is no whole-project " + "fallback." },
          gate: { ...GATE_PROP },
        },
  },
);
