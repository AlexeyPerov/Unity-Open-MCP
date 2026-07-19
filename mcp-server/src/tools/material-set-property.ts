import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed material property set. Typed by value kind. Mutating:
// runs the full gate path; `paths_hint` is the .mat asset path (or, for a
// scene-instance material, the scene path).
export const materialSetProperty = makeTool(
  "unity_open_mcp_material_set_property",
  "Set a single shader property on a Material. The value kind is inferred from `type` " +
    "(color / float / int / vector / texture). Records an Undo before mutating. Mutating: " +
    "runs the full gate path; `paths_hint` is the .mat asset path (or scene path for an " +
    "instance material). Resolve the material by `asset_path` (.mat) or `instance_id` " +
    "(scene Renderer's sharedMaterial or the Material directly).",
  {
    required: ["property", "type", "value", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description: "Material asset path (.mat). Highest priority resolver.",
          },
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description:
              "Instance ID of a scene GameObject whose Renderer.sharedMaterial is mutated, OR the Material instance directly.",
          },
          property: {
            type: "string",
            description: "Shader property name (must exist on the material's shader).",
          },
          type: {
            type: "string",
            enum: ["color", "float", "int", "vector", "texture"],
            description:
              "Value kind. color = [r,g,b,a] 0-1; float/int = number; vector = [x,y,z,w]; texture = {\"path\": \"Assets/...\"} or null.",
          },
          value: {
            description:
              "The new value. Shape depends on `type` — see `type` for each shape. " +
              "For color/vector: [r,g,b] or [r,g,b,a] arrays; the {" +
              "\"r\":..,\"g\":..,\"b\":..,\"a\":..} / {\"x\":..,\"y\":..,\"z\":..,\"w\":..} " +
              "object form is also accepted (useful for MCP hosts that serialize arrays as strings). " +
              "float/int = number; texture = {\"path\": \"Assets/...\"} or null. Pass null with type=texture to clear.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Affected .mat asset path or scene path (the gate's validation scope)." },
          gate: { ...GATE_PROP },
        },
  },
);
