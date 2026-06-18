import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — typed prefab instance unpack. Mutating: runs the full gate path.
export const prefabUnpack: Tool = {
  name: "unity_open_mcp_prefab_unpack",
  description:
    "Unpack a prefab instance into a plain GameObject (severing the prefab link). Mutating: runs " +
    "the full gate path; `paths_hint` should be the scene path holding the instance. Resolve the " +
    "instance by `instance_id` (canonical) or `path` / `name` (fallback).",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: {
        type: "integer",
        default: 0,
        description: "Instance ID of the prefab instance to unpack (canonical address).",
      },
      path: {
        type: "string",
        description: "Hierarchy path of the prefab instance (fallback address).",
      },
      name: {
        type: "string",
        description: "Prefab instance GameObject name (lowest priority address).",
      },
      completely: {
        type: "boolean",
        default: false,
        description:
          "When true, recursively unpack nested prefab instances. When false (default), only the " +
          "outermost root is unpacked.",
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
