import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — typed prefab instance revert. Mutating: runs the full gate path.
export const prefabRevert: Tool = {
  name: "unity_open_mcp_prefab_revert",
  description:
    "Revert a prefab instance to match its source prefab asset (discarding all instance overrides). " +
    "Mutating: runs the full gate path; `paths_hint` should be the scene path holding the instance. " +
    "Resolve the instance by `instance_id` (canonical) or `path` / `name` (fallback).",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: {
        type: ["string", "integer"],
        default: 0,
        description: "Instance ID of the prefab instance to revert (canonical address).",
      },
      path: {
        type: "string",
        description: "Hierarchy path of the prefab instance (fallback address).",
      },
      name: {
        type: "string",
        description: "Prefab instance GameObject name (lowest priority address).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Scene path holding the instance (the gate's validation scope).",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
