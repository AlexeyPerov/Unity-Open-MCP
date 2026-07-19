import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 2 — typed GameObject reparent. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the scene that contains the target.
export const gameobjectSetParent = makeTool(
  "unity_open_mcp_gameobject_set_parent",
  "Reparent a GameObject under another GameObject in the active scene. Undo-recorded. Cycle-safe " +
    "(refuses to parent a GameObject under one of its own descendants). Mutating: runs the full " +
    "gate path; `paths_hint` is the scene path that contains the target. Address the child by " +
    "instance_id > path > name. Identify the new parent by parent_instance_id or parent_path. " +
    "world_position_stays (default true) preserves world transform across the reparent.",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Child GameObject instance ID. Highest priority resolver.",
          },
          path: {
            type: "string",
            description: "Child hierarchy path \"Root/Child\".",
          },
          name: {
            type: "string",
            description: "Child GameObject name (first match). Lowest priority resolver.",
          },
          parent_instance_id: {
            type: ["string", "integer"],
            default: 0,
            description:
              "New parent GameObject instance ID. Takes precedence over parent_path when both are set.",
          },
          parent_path: {
            type: "string",
            description: "New parent hierarchy path \"Root/Parent\".",
          },
          world_position_stays: {
            type: "boolean",
            default: true,
            description:
              "When true (default), preserves the GameObject's world position/rotation/scale across " +
              "the reparent. Set false to keep local-space values instead.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the child." },
          gate: { ...GATE_PROP },
        },
  },
);
