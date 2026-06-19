import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. Mutating: runs the full gate path.
export const navigationModifierVolumeAdd: Tool = {
  name: "unity_open_mcp_navigation_modifier_volume_add",
  description:
    "Add a NavMeshModifierVolume component to a GameObject and size it. " +
    "Re-tags the NavMesh inside the volume to the given area (default " +
    "'Walkable'). Idempotent. Mutating: runs the full gate path; paths_hint " +
    "is the host scene path. Requires the navigation extension pack.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      area: {
        type: "string",
        default: "Walkable",
        description: "NavMesh area name to apply inside the volume.",
      },
      size: {
        type: "string",
        default: "4,4,4",
        description: "Volume size as 'x,y,z' (local space).",
      },
      center: {
        type: "string",
        default: "0,0,0",
        description: "Volume center as 'x,y,z' (local space).",
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
