import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 2 / T20.2.1 — Lighting domain tool. Built-in lighting module (no
// extra UPM); the `lighting` group is hidden until manage_tools activates it.
// Mutating: runs the full gate path; paths_hint is the scene path containing
// the host. Address the host by instance_id > path > name (same model as
// gameobject_* / component_*).
const targetSchema = {
  instance_id: {
    type: ["string", "integer"],
    default: 0,
    description: "Host GameObject instance ID. Highest priority resolver.",
  },
  path: {
    type: "string",
    description: "Host hierarchy path \"Root/Child\".",
  },
  name: {
    type: "string",
    description: "Host GameObject name (first match). Lowest priority resolver.",
  },
  paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the host." },
  gate: { ...GATE_PROP },
};

export const lightAdd = makeTool(
  "unity_open_mcp_light_add",
  "Add a Light component to a GameObject. Optionally set the type " +
    "(Spot | Point | Directional | Area | Rectangle, default Directional), color " +
    "([r,g,b,(a)] 0-1), intensity (default 1), range (Point/Spot, default 10), and " +
    "spot angle (Spot only, default 30). Idempotent — re-using an existing Light " +
    "reports added:false. Mutating: runs the full gate path; paths_hint is the " +
    "scene path that contains the host. Built-in lighting module (no package " +
    "dependency); the lighting group is hidden until manage_tools activates it.",
  {
    required: ["paths_hint"],
        properties: {
          ...targetSchema,
          light_type: {
            type: "string",
            enum: ["Spot", "Point", "Directional", "Area", "Rectangle"],
            default: "Directional",
            description: "LightType name.",
          },
          color: {
            type: "string",
            description: "Color as 'r,g,b,(a)' 0-1 (e.g. '1,0,0,1').",
          },
          intensity: {
            type: "number",
            default: 1,
            description: "Light intensity.",
          },
          range: {
            type: "number",
            default: 10,
            description: "Light range (Point/Spot).",
          },
          spot_angle: {
            type: "number",
            default: 30,
            description: "Spot angle in degrees (Spot only).",
          },
        },
  },
);
