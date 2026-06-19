import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.5 — ProBuilder extension tool. Requires the probuilder
// extension pack. Mutating + DESTRUCTIVE: runs the full gate path.
const targetSchema = {
  instance_id: {
    type: "integer",
    default: 0,
    description: "Host GameObject instance ID. Highest priority resolver.",
  },
  path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
  name: { type: "string", description: "Host GameObject name (first match). Lowest priority resolver." },
  paths_hint: {
    type: "array",
    items: { type: "string" },
    description: "Mutation scope — the host scene path.",
  },
  gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
};

export const probuilderDeleteFaces: Tool = {
  name: "unity_open_mcp_probuilder_delete_faces",
  description:
    "Delete faces from a ProBuilderMesh, creating holes or removing geometry. " +
    "DESTRUCTIVE — irreversible without undo (use editor_undo to recover if " +
    "needed). Supply either face_indices (explicit) or face_direction " +
    "('Up' / 'Down' / 'Left' / 'Right' / 'Forward' / 'Back'); exactly one is " +
    "required. Refuses to delete every face (at least one must remain). " +
    "Mutating: runs the full gate path; paths_hint is the host scene path. " +
    "Requires the probuilder extension pack installed in the project.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      ...targetSchema,
      face_indices: {
        type: "array",
        items: { type: "integer" },
        description:
          "Explicit face indices to delete. Use this OR face_direction, not both. " +
          "Use probuilder_get_mesh_info to discover valid indices.",
      },
      face_direction: {
        type: "string",
        enum: ["Up", "Down", "Left", "Right", "Forward", "Back"],
        description:
          "Semantic face selection by direction. Use this OR face_indices, not both.",
      },
    },
    additionalProperties: false,
  },
};
