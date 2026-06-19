import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.5 — ProBuilder extension tool. Requires the probuilder
// extension pack. Mutating: runs the full gate path.
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

export const probuilderSetFaceMaterial: Tool = {
  name: "unity_open_mcp_probuilder_set_face_material",
  description:
    "Assign a Material to faces of a ProBuilderMesh, enabling multi-material " +
    "meshes (e.g. grass on top, dirt on sides). material_path is an 'Assets/'-" +
    "rooted path to a .mat asset (or a bare name — searched via " +
    "AssetDatabase.FindAssets). Supply either face_indices (explicit) or " +
    "face_direction ('Up' / 'Down' / 'Left' / 'Right' / 'Forward' / 'Back'); " +
    "exactly one is required. Idempotent. Mutating: runs the full gate path; " +
    "paths_hint is the host scene path. Requires the probuilder extension pack " +
    "installed in the project.",
  inputSchema: {
    type: "object",
    required: ["material_path", "paths_hint"],
    properties: {
      ...targetSchema,
      material_path: {
        type: "string",
        description:
          "'Assets/'-rooted path to a .mat asset (e.g. " +
          "'Assets/Materials/Grass.mat') or a bare material name.",
      },
      face_indices: {
        type: "array",
        items: { type: "integer" },
        description:
          "Explicit face indices to apply the material to. Use this OR " +
          "face_direction, not both.",
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
