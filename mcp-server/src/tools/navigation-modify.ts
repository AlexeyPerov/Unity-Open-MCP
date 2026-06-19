import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. Mutating: runs the full gate path.
export const navigationModify: Tool = {
  name: "unity_open_mcp_navigation_modify",
  description:
    "Set one or more serialized fields on a NavMesh component attached to a " +
    "target GameObject. Select the component by component_type (NavMeshSurface " +
    "| NavMeshAgent | NavMeshLink | NavMeshModifier | NavMeshModifierVolume). " +
    "Use this when a typed mutator does not cover a niche field; otherwise " +
    "prefer the typed tools (surface_add / agent_add / etc.). Each entry is " +
    "{ field, value, type? } where type is 'int' | 'float' | 'bool' | 'string' " +
    "| 'vector' (default inferred from the field's current type). Mutating: " +
    "runs the full gate path; paths_hint is the host scene path. Requires " +
    "the navigation extension pack.",
  inputSchema: {
    type: "object",
    required: ["component_type", "fields_json", "paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      component_type: {
        type: "string",
        enum: [
          "NavMeshSurface",
          "NavMeshAgent",
          "NavMeshLink",
          "NavMeshModifier",
          "NavMeshModifierVolume",
        ],
        description: "Which NavMesh component to modify.",
      },
      fields_json: {
        type: "string",
        description:
          "JSON array of { field, value, type? } patches. Example: " +
          "[{\"field\":\"speed\",\"value\":5.5,\"type\":\"float\"}].",
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
