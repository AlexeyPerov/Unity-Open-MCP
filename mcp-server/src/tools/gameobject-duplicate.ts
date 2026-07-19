import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 2 — typed GameObject duplicate. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the scene that contains the target.
export const gameobjectDuplicate = makeTool(
  "unity_open_mcp_gameobject_duplicate",
  "Duplicate a GameObject (and its children) in the active scene, preserving the parent " +
    "relationship and transform. Undo-recorded. Mutating: runs the full gate path; `paths_hint` " +
    "is the scene path that contains the target. Returns the clone's instanceId, name, path, " +
    "transform, and component list so the agent can immediately chain further ops. Address the " +
    "source by instance_id > path > name.",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Source GameObject instance ID. Highest priority resolver.",
          },
          path: {
            type: "string",
            description: "Source hierarchy path \"Root/Child\". Middle-priority resolver.",
          },
          name: {
            type: "string",
            description: "Source GameObject name (first match). Lowest priority resolver.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the source and clone." },
          gate: { ...GATE_PROP },
        },
  },
);
