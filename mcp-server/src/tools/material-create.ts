import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed material create. Wraps AssetDatabase.CreateAsset on a
// new Material(shader). Mutating: runs the full gate path; `paths_hint`
// must point at the new .mat path.
export const materialCreate = makeTool(
  "unity_open_mcp_material_create",
  "Create a new Material asset at a given Assets/-rooted path ending in .mat, using the " +
    "named shader. Intermediate folders are created if missing. Defaults `shader_name` to " +
    "'Standard' (or 'Universal Render Pipeline/Lit' if found). Mutating: runs the full gate " +
    "path; `paths_hint` must be [\"<new .mat path>\"]. Use unity_open_mcp_shader_list_all first " +
    "to discover a valid shader name.",
  {
    required: ["asset_path", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description: "Destination asset path. Must start with 'Assets/' and end with '.mat'.",
          },
          shader_name: {
            type: "string",
            description:
              "Shader name resolvable via Shader.Find (e.g. 'Standard', 'Universal Render Pipeline/Lit', 'Sprites/Default'). " +
              "Defaults to 'Standard' (or 'Universal Render Pipeline/Lit' if present).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "New .mat path (the gate's validation scope). No whole-project fallback." },
          gate: { ...GATE_PROP },
        },
  },
);
