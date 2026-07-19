import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 2 — typed GameObject destroy. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the scene that contains the target.
export const gameobjectDestroy = makeTool(
  "unity_open_mcp_gameobject_destroy",
  "Destroy a GameObject (and its children) in the active scene. Undo-recorded. Mutating: runs " +
    "the full gate path; `paths_hint` is the scene path that contains the target. Address the " +
    "target by instance_id (canonical) > path > name — same vocabulary as spatial_query.",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Target GameObject instance ID. Highest priority resolver.",
          },
          path: {
            type: "string",
            description: "Target hierarchy path \"Root/Child\". Middle-priority resolver.",
          },
          name: {
            type: "string",
            description: "Target GameObject name (first match). Lowest priority resolver.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the target." },
          gate: { ...GATE_PROP },
        },
  },
);
