import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 2 — typed GameObject duplicate. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the scene that contains the target.
export const gameobjectDuplicate: Tool = {
  name: "unity_open_mcp_gameobject_duplicate",
  description:
    "Duplicate a GameObject (and its children) in the active scene, preserving the parent " +
    "relationship and transform. Undo-recorded. Mutating: runs the full gate path; `paths_hint` " +
    "is the scene path that contains the target. Returns the clone's instanceId, name, path, " +
    "transform, and component list so the agent can immediately chain further ops. Address the " +
    "source by instance_id > path > name.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: {
        type: "integer",
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
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the scene path that contains the source and clone.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
