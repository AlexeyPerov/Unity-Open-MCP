import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. The bridge-side
// handler is embedded in the bridge (compile-gated by
// UNITY_OPEN_MCP_EXT_NAVIGATION, active when com.unity.ai.navigation is
// present). Mutating: runs the full gate path; paths_hint is the scene path
// containing the host. Address the host by instance_id > path > name (same
// model as gameobject_* / component_*).
const targetSchema = {
  instance_id: {
    type: ["string", "integer"],
    default: 0,
    description: "Host GameObject instance ID. Highest priority resolver.",
  },
  path: {
    type: "string",
    description: "Host hierarchy path \"Root/Child\".",
  },
  name: {
    type: "string",
    description: "Host GameObject name (first match). Lowest priority resolver.",
  },
  paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the host." },
  gate: { ...GATE_PROP },
};

export const navigationSurfaceAdd = makeTool(
  "unity_open_mcp_navigation_surface_add",
  "Add a NavMeshSurface component to a GameObject. Optionally set the agent " +
    "type (default 'Humanoid'), collect-geometry mode ('All' | 'Volume'), and " +
    "an optional bake extent (x,y,z). Idempotent — re-using an existing " +
    "surface is reported with added:false. Mutating: runs the full gate path; " +
    "paths_hint is the scene path that contains the host. Requires the " +
    "navigation extension pack installed in the project.",
  {
    required: ["paths_hint"],
        properties: {
          ...targetSchema,
          agent_type: {
            type: "string",
            default: "Humanoid",
            description: "Agent type name (e.g. 'Humanoid'). Resolved via NavMesh.GetSettingsByName.",
          },
          collect_objects: {
            type: "string",
            enum: ["All", "Volume"],
            default: "All",
            description: "Geometry collection mode. 'Volume' bakes only geometry inside collection_extent.",
          },
          collection_extent: {
            type: "string",
            description: "Bake volume half-extent as 'x,y,z'. Non-zero forces collect_objects='Volume'.",
          },
        },
  },
);
