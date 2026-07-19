import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed material shader swap. Mutating: runs the full gate path.
export const materialSetShader = makeTool(
  "unity_open_mcp_material_set_shader",
  "Change the shader assigned to a Material. Records an Undo before mutating. Note that " +
    "swapping shaders can invalidate properties whose names/types don't carry over — the gate " +
    "delta surfaces missing-property references. Mutating: runs the full gate path; `paths_hint` " +
    "is the .mat asset path or scene path. Resolve the material by `asset_path` (.mat) or " +
    "`instance_id`. Use unity_open_mcp_shader_list_all first to discover a valid shader name.",
  {
    required: ["shader_name", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description: "Material asset path (.mat). Highest priority resolver.",
          },
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Instance ID of a scene GameObject whose Renderer.sharedMaterial is mutated, OR the Material instance directly.",
          },
          shader_name: {
            type: "string",
            description: "Shader name resolvable via Shader.Find.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Affected .mat asset path or scene path (the gate's validation scope)." },
          gate: { ...GATE_PROP },
        },
  },
);
