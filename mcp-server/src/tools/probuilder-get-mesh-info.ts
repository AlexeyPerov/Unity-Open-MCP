import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.5 — ProBuilder extension tool. Requires the probuilder
// extension pack. Read-only, gate-free.
export const probuilderGetMeshInfo = makeTool(
  "unity_open_mcp_probuilder_get_mesh_info",
  "Inspect a ProBuilderMesh — face / vertex / edge counts, bounds, and a " +
    "face-direction summary (which face indices face Up / Down / Left / Right / " +
    "Forward / Back). Read-only, gate-free. Use this to discover valid face " +
    "indices or to pick a semantic direction for extrude / delete_faces / " +
    "set_face_material. Requires the probuilder extension pack installed in the " +
    "project.",
  {
    properties: {
          instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
          path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
          name: { type: "string", description: "Host GameObject name (first match)." },
        },
  },
);
