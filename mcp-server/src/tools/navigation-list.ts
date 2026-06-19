import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. Read-only, gate-free.
export const navigationList: Tool = {
  name: "unity_open_mcp_navigation_list",
  description:
    "List every NavMeshSurface, NavMeshAgent, NavMeshLink, NavMeshModifier, " +
    "and NavMeshModifierVolume in the open scene(s). Read-only, gate-free. " +
    "Each entry includes the host name, instance id, hierarchy path, and a " +
    "few component-specific fields. Use this to discover surfaces / agents / " +
    "links before mutating. Requires the navigation extension pack.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
