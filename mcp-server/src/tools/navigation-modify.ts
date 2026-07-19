import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. Mutating: runs the full gate path.
export const navigationModify = makeTool(
  "unity_open_mcp_navigation_modify",
  "Set one or more serialized fields on a NavMesh component attached to a " +
    "target GameObject. Select the component by component_type (NavMeshSurface " +
    "| NavMeshAgent | NavMeshLink | NavMeshModifier | NavMeshModifierVolume). " +
    "Use this when a typed mutator does not cover a niche field; otherwise " +
    "prefer the typed tools (surface_add / agent_add / etc.). Each entry is " +
    "{ field, value, type? } where type is 'int' | 'float' | 'bool' | 'string' " +
    "| 'vector' (default inferred from the field's current type). Mutating: " +
    "runs the full gate path; paths_hint is the host scene path. Requires " +
    "the navigation extension pack.",
  {
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
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the host." },
          gate: { ...GATE_PROP },
        },
  },
);
