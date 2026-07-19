import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. Read-only, gate-free.
export const navigationGet = makeTool(
  "unity_open_mcp_navigation_get",
  "Read every NavMesh component attached to one GameObject " +
    "(NavMeshSurface / NavMeshAgent / NavMeshLink / NavMeshModifier / " +
    "NavMeshModifierVolume) with their serialized fields. Read-only, " +
    "gate-free. Address the host by instance_id > path > name. Requires the " +
    "navigation extension pack.",
  {
    properties: {
          instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
          path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
          name: { type: "string", description: "Host GameObject name (first match)." },
        },
  },
);
