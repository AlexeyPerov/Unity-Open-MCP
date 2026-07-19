import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 1 — read-only prefab overrides listing. Gate-free.
export const prefabGetOverrides = makeTool(
  "unity_open_mcp_prefab_get_overrides",
  "List property/component overrides on a prefab instance: propertyModifications (target type + " +
    "propertyPath + value), addedComponents (type + instanceId), and removedComponents (type). " +
    "Read-only (gate-free). Resolve the instance by `instance_id` (canonical) or `path` / `name`.",
  {
    properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Instance ID of the prefab instance (canonical address).",
          },
          path: {
            type: "string",
            description: "Hierarchy path of the prefab instance (fallback address).",
          },
          name: {
            type: "string",
            description: "Prefab instance GameObject name (lowest priority address).",
          },
        },
  },
);
