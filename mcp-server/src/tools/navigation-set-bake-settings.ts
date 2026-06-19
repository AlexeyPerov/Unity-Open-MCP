import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. Mutating: runs the full gate path; paths_hint is
// the host's scene path.
export const navigationSetBakeSettings: Tool = {
  name: "unity_open_mcp_navigation_set_bake_settings",
  description:
    "Configure NavMesh bake settings on an existing NavMeshSurface: agent type " +
    "(Humanoid / OEM:Tank / etc.), collect-objects mode ('All' | 'Volume'), " +
    "and optional bake extent (x,y,z). Refuses if the host has no " +
    "NavMeshSurface (add one with navigation_surface_add first). Mutating: " +
    "runs the full gate path; paths_hint is the host's scene path. Requires " +
    "the navigation extension pack.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      agent_type: {
        type: "string",
        description: "Agent type name (e.g. 'Humanoid'). Omit to leave unchanged.",
      },
      collect_objects: {
        type: "string",
        enum: ["All", "Volume"],
        description: "Geometry collection mode.",
      },
      collection_extent: {
        type: "string",
        description: "Bake volume half-extent as 'x,y,z'.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the scene path that contains the host.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
