import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 3 / T20.3.3 — Constraints & LOD domain tool. Configure a LODGroup
// on a host GameObject. Built-in engine module. Mutating: runs the full gate
// path; paths_hint is the host scene path. Idempotent — re-using an existing
// LODGroup reports added:false (configuration still applied).
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
  paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host scene path. No whole-project fallback." },
  gate: { ...GATE_PROP },
};

export const lodGroupConfigure = makeTool(
  "unity_open_mcp_lod_group_configure",
  "Configure a LODGroup on a GameObject. Optional: fade_mode (None | SpeedTree | " +
    "CrossFade, default None — leaves the existing value when omitted), " +
    "animate_cross_fading (default false), lod_count (allocates the LOD array with " +
    "that many levels; renderers start empty — wire them via lod_add_level). " +
    "Idempotent — re-using an existing LODGroup reports added:false (configuration " +
    "still applied). Mutating: runs the full gate path; paths_hint is the host scene " +
    "path. Built-in engine module (no package dependency); the constraints group is " +
    "hidden until manage_tools activates it.",
  {
    required: ["paths_hint"],
        properties: {
          ...targetSchema,
          fade_mode: {
            type: "string",
            enum: ["None", "SpeedTree", "CrossFade"],
            description: "LOD fade mode. Omit to leave the existing value untouched.",
          },
          animate_cross_fading: {
            type: "boolean",
            default: false,
            description: "Whether cross-fade transitions animate.",
          },
          lod_count: {
            type: "integer",
            default: -1,
            description:
              "Allocate the LOD array with this many levels (1-8). -1 leaves the array " +
              "untouched. Renderers start empty — wire them via lod_add_level.",
          },
        },
  },
);
