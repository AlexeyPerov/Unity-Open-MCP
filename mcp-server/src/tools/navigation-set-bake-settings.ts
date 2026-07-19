import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. Mutating: runs the full gate path; paths_hint is
// the host's scene path.
export const navigationSetBakeSettings = makeTool(
  "unity_open_mcp_navigation_set_bake_settings",
  "Configure NavMesh bake settings on an existing NavMeshSurface: agent type " +
    "(Humanoid / OEM:Tank / etc.), collect-objects mode ('All' | 'Volume'), " +
    "and optional bake extent (x,y,z). Refuses if the host has no " +
    "NavMeshSurface (add one with navigation_surface_add first). Mutating: " +
    "runs the full gate path; paths_hint is the host's scene path. Requires " +
    "the navigation extension pack.",
  {
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
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the host." },
          gate: { ...GATE_PROP },
        },
  },
);
