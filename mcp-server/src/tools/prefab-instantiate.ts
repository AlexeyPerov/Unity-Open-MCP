import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed prefab instantiate. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the destination scene path.
export const prefabInstantiate = makeTool(
  "unity_open_mcp_prefab_instantiate",
  "Instantiate a prefab at an optional position/rotation/scale, optionally parented under an existing " +
    "GameObject path. Without parent_path the instance root goes to the container scene_path/scene_name " +
    "names; else, while a prefab stage is open, under the prefab root (unless paths_hint names a loaded scene " +
    "and not the prefab); else the active scene. A parent_path found in several loaded scenes resolves in the " +
    "one scene_path / paths_hint names, then the active scene, else refuses with ambiguous_path. Mutating: " +
    "runs the full gate path. `paths_hint` should be the destination scene path plus the prefab asset path. " +
    "Returns the new instance's instanceId, name, path and container (scene or prefab stage) so the agent can " +
    "re-acquire it. Use unity_open_mcp_search_assets with t:Prefab to locate the prefab asset first.",
  {
    required: ["prefab_asset_path", "paths_hint"],
        properties: {
          prefab_asset_path: {
            type: "string",
            description: "Prefab asset path to instantiate.",
          },
          name: {
            type: "string",
            description: "Optional name for the new GameObject (defaults to the prefab's name).",
          },
          parent_path: {
            type: "string",
            description:
              "Optional parent hierarchy path \"Root/Parent\" (in the open prefab stage: from the prefab root) " +
              "— the parent must already exist. Omit to instantiate a root.",
          },
          scene_path: {
            type: "string",
            description:
              "Optional container: a loaded scene's asset path (e.g. 'Assets/Scenes/Bootstrap.unity'; load it " +
              "with unity_open_mcp_scene_open Additive first) or the open prefab stage's prefab asset path. The " +
              "instance root goes there, and parent_path is looked up only there.",
          },
          scene_name: {
            type: "string",
            description:
              "Optional target scene name for the instance root (alternative to scene_path). The scene must " +
              "already be loaded. Omit to instantiate in the active scene.",
          },
          position: {
            type: "string",
            description: "Optional world-space position as \"x,y,z\". Defaults to 0,0,0.",
          },
          rotation: {
            type: "string",
            description: "Optional world-space Euler rotation in degrees as \"x,y,z\". Defaults to 0,0,0.",
          },
          scale: {
            type: "string",
            description: "Optional world-space scale as \"x,y,z\". Defaults to 1,1,1.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — include the active scene path (the instance is a scene side-effect) and the prefab asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
