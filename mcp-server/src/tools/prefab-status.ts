import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — read-only prefab status. Gate-free. Reports the prefab-ness
// of a GameObject (isPrefab / isInstance / isRoot / hasOverrides + source
// asset path when relevant).
export const prefabStatus: Tool = {
  name: "unity_open_mcp_prefab_status",
  description:
    "Report the prefab-ness of a GameObject: isPrefab, isInstance, isRoot, hasOverrides, and " +
    "(when relevant) the source prefab asset path/name. Read-only (gate-free). Resolve the target " +
    "by `instance_id` (canonical) or `path` / `name`.",
  inputSchema: {
    type: "object",
    properties: {
      instance_id: {
        type: "integer",
        default: 0,
        description: "Instance ID of the target GameObject (canonical address).",
      },
      path: {
        type: "string",
        description: "Hierarchy path of the target GameObject (fallback address).",
      },
      name: {
        type: "string",
        description: "Target GameObject name (lowest priority address).",
      },
    },
    additionalProperties: false,
  },
};
