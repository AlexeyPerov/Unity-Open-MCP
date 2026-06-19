import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. Mutating: runs the full gate path.
export const navigationModifierAdd: Tool = {
  name: "unity_open_mcp_navigation_modifier_add",
  description:
    "Add a NavMeshModifier component to a GameObject. Override the area type " +
    "(default 'Walkable') or mark the object as ignored for baking. Idempotent " +
    "— re-using an existing modifier is reported with added:false. Mutating: " +
    "runs the full gate path; paths_hint is the host scene path. Requires the " +
    "navigation extension pack.",
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
        description: "NavMesh area name (e.g. 'Walkable', 'Door', 'Jump').",
      },
      ignore: {
        type: "boolean",
        default: false,
        description: "Skip this object during the bake (NavMeshModifier.ignoreFromBuild).",
      },
      override_area: {
        type: "boolean",
        default: true,
        description: "Apply the area override (NavMeshModifier.overrideArea).",
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
