import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.5 — ProBuilder extension tool. Requires the probuilder
// extension pack. Mutating: runs the full gate path.
const targetSchema = {
  instance_id: {
    type: ["string", "integer"],
    default: 0,
    description: "Host GameObject instance ID. Highest priority resolver.",
  },
  path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
  name: { type: "string", description: "Host GameObject name (first match). Lowest priority resolver." },
  paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host scene path." },
  gate: { ...GATE_PROP },
};

export const probuilderExtrude = makeTool(
  "unity_open_mcp_probuilder_extrude",
  "Extrude faces of a ProBuilderMesh along their normals, creating new " +
    "geometry. Supply either face_indices (explicit) or face_direction " +
    "('Up' / 'Down' / 'Left' / 'Right' / 'Forward' / 'Back'); exactly one is " +
    "required. Positive distance extrudes outward, negative inward. " +
    "extrude_method is 'IndividualFaces' / 'FaceNormal' (default) / " +
    "'VertexNormal'. Mutating: runs the full gate path; paths_hint is the host " +
    "scene path. Requires the probuilder extension pack installed in the project.",
  {
    required: ["paths_hint"],
        properties: {
          ...targetSchema,
          face_indices: {
            type: "array",
            items: { type: "integer" },
            description:
              "Explicit face indices to extrude. Use this OR face_direction, not both. " +
              "Use probuilder_get_mesh_info to discover valid indices.",
          },
          face_direction: {
            type: "string",
            enum: ["Up", "Down", "Left", "Right", "Forward", "Back"],
            description:
              "Semantic face selection by direction. Use this OR face_indices, not both.",
          },
          distance: {
            type: "number",
            default: 0.5,
            description: "Extrusion distance. Positive = outward, negative = inward.",
          },
          extrude_method: {
            type: "string",
            enum: ["IndividualFaces", "FaceNormal", "VertexNormal"],
            default: "FaceNormal",
            description: "How the faces move during extrusion.",
          },
        },
  },
);
