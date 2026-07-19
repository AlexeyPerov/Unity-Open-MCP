import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed prefab instantiate. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the destination scene path.
export const prefabInstantiate = makeTool(
  "unity_open_mcp_prefab_instantiate",
  "Instantiate a prefab into the currently active scene at an optional position/rotation/scale, " +
    "optionally parented under an existing scene GameObject path. Mutating: runs the full gate path. " +
    "`paths_hint` should be the destination scene path (the instance is a scene side-effect) plus the " +
    "prefab asset path. Returns the new instance's instanceId, name, and path so the agent can " +
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
              "Optional destination scene path \"Root/Parent\" — the parent must already exist. " +
              "Omit to instantiate at scene root.",
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
