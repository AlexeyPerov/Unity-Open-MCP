import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M18 Plan 7 / T18.7.3 — Splines extension tool. Requires the
// com.unity.splines package installed in the target project (the bridge
// compiles the handler in only when the package is present). Mutating: runs
// the full gate path; paths_hint is the active scene path.
export const splinesContainerCreate = makeTool(
  "unity_open_mcp_splines_container_create",
  "Create a new GameObject carrying a SplineContainer component in the active " +
    "scene. The container is initialized with one empty primary spline " +
    "(spline_index 0). Optionally set name, position, rotation, parent_path, " +
    "and closed state. Mutating: runs the full gate path; paths_hint is the " +
    "active scene path (the new GameObject lives there). Requires the " +
    "com.unity.splines package installed in the project.",
  {
    required: ["paths_hint"],
        properties: {
          name: { type: "string", description: "Optional GameObject name." },
          parent_path: {
            type: "string",
            description: "Optional slash-separated hierarchy path to a parent GameObject.",
          },
          position: { type: "string", description: "Optional position as 'x,y,z'." },
          rotation: { type: "string", description: "Optional Euler rotation (degrees) as 'x,y,z'." },
          closed: {
            type: "boolean",
            default: false,
            description: "Whether the primary spline forms a closed loop.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the active scene path (the new GameObject lives there)." },
          gate: { ...GATE_PROP },
        },
  },
);
