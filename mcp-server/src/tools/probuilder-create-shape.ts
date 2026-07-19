import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.5 — ProBuilder extension tool. The bridge-side handler
// is embedded in the bridge (compile-gated by UNITY_OPEN_MCP_EXT_PROBUILDER,
// active when com.unity.probuilder is present). Mutating: runs the full gate
// path; paths_hint is the active scene path.
export const probuilderCreateShape = makeTool(
  "unity_open_mcp_probuilder_create_shape",
  "Create a new editable ProBuilderMesh GameObject from a ShapeType primitive " +
    "(Cube / Cylinder / Sphere / Plane / Prism / Cone / Stair / Door / Pipe / " +
    "Arch / Sprite / Torus). Optionally set name, position, rotation, scale, and " +
    "parent_path. Mutating: runs the full gate path; paths_hint is the active " +
    "scene path. Requires the probuilder extension pack installed in the project.",
  {
    required: ["paths_hint"],
        properties: {
          shape_type: {
            type: "string",
            default: "Cube",
            description:
              "ShapeType primitive. Valid: Cube, Cylinder, Sphere, Plane, Prism, Cone, " +
              "Stair, Door, Pipe, Arch, Sprite, Torus.",
          },
          name: { type: "string", description: "Optional GameObject name." },
          parent_path: {
            type: "string",
            description: "Optional slash-separated hierarchy path to a parent GameObject.",
          },
          position: { type: "string", description: "Optional position as 'x,y,z'." },
          rotation: { type: "string", description: "Optional Euler rotation (degrees) as 'x,y,z'." },
          scale: { type: "string", description: "Optional scale as 'x,y,z'." },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the active scene path (the new GameObject lives there)." },
          gate: { ...GATE_PROP },
        },
  },
);
