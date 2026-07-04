import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — typed prefab create / variant. Mutating: runs the full gate
// path. `paths_hint` is the new prefab asset path.
export const prefabCreate: Tool = {
  name: "unity_open_mcp_prefab_create",
  description:
    "Create a prefab (or prefab variant) asset at a given Assets/-rooted path ending in .prefab, " +
    "from a scene GameObject. Creates intermediate folders if missing. When the source GameObject " +
    "is already a prefab instance and `connect` is true, a prefab variant is created. Mutating: " +
    "runs the full gate path; `paths_hint` is the new .prefab path (and the scene path if the " +
    "source is connected in-scene). Resolve the source GameObject by `instance_id` (canonical) or " +
    "by `path` / `name` (fallback).",
  inputSchema: {
    type: "object",
    required: ["prefab_asset_path", "paths_hint"],
    properties: {
      prefab_asset_path: {
        type: "string",
        description: "Destination prefab asset path. Must start with 'Assets/' and end with '.prefab'.",
      },
      instance_id: {
        type: ["string", "integer"],
        default: 0,
        description: "Instance ID of the scene GameObject to convert (canonical address).",
      },
      path: {
        type: "string",
        description: "Hierarchy path of the scene GameObject (fallback address).",
      },
      name: {
        type: "string",
        description: "Scene GameObject name (lowest priority address).",
      },
      connect: {
        type: "boolean",
        default: true,
        description:
          "When true, the scene GameObject is connected to the new prefab (becoming a prefab instance); " +
          "if the source is already a prefab instance, a variant is created. When false, the asset is " +
          "saved but the scene GameObject is left unchanged.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "New .prefab path (+ scene path when connect is true). The gate's validation scope.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
