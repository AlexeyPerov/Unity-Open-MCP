import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 2 — typed GameObject destroy. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the scene that contains the target.
export const gameobjectDestroy: Tool = {
  name: "unity_open_mcp_gameobject_destroy",
  description:
    "Destroy a GameObject (and its children) in the active scene. Undo-recorded. Mutating: runs " +
    "the full gate path; `paths_hint` is the scene path that contains the target. Address the " +
    "target by instance_id (canonical) > path > name — same vocabulary as spatial_query.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: {
        type: "integer",
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
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the scene path that contains the target.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
