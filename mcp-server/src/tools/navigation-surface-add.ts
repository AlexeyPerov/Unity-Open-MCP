import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// `com.alexeyperov.unity-open-mcp-ext-navigation` extension pack installed in
// the target project (adds the bridge-side handler). Mutating: runs the full
// gate path; paths_hint is the scene path containing the host. Address the
// host by instance_id > path > name (same model as gameobject_* / component_*).
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
  paths_hint: {
    type: "array",
    items: { type: "string" },
    description: "Mutation scope — the scene path that contains the host.",
  },
  gate: {
    enum: ["enforce", "warn", "off"],
    default: "enforce",
  },
};

export const navigationSurfaceAdd: Tool = {
  name: "unity_open_mcp_navigation_surface_add",
  description:
    "Add a NavMeshSurface component to a GameObject. Optionally set the agent " +
    "type (default 'Humanoid'), collect-geometry mode ('All' | 'Volume'), and " +
    "an optional bake extent (x,y,z). Idempotent — re-using an existing " +
    "surface is reported with added:false. Mutating: runs the full gate path; " +
    "paths_hint is the scene path that contains the host. Requires the " +
    "navigation extension pack installed in the project.",
  inputSchema: {
    type: "object",
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
    additionalProperties: false,
  },
};
