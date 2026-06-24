import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M18 Plan 7 / T18.7.3 — Splines extension tool. Requires the
// com.unity.splines package. Mutating: runs the full gate path; paths_hint is
// the host's scene path.
export const splinesSetKnot: Tool = {
  name: "unity_open_mcp_splines_set_knot",
  description:
    "Replace the BezierKnot at knot_index on a spline. Provide any of position " +
    "('x,y,z'), rotation ('x,y,z' Euler), tangent_in/tangent_out ('x,y,z'); " +
    "omitted fields keep the current knot's value. Mutating: runs the full " +
    "gate path; paths_hint is the host's scene path. Requires the " +
    "com.unity.splines package installed in the project.",
  inputSchema: {
    type: "object",
    required: ["knot_index", "paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      spline_index: {
        type: "integer",
        default: 0,
        description: "Spline index within the container (0 = primary).",
      },
      knot_index: {
        type: "integer",
        description: "Index of the knot to replace (0-based).",
      },
      position: { type: "string", description: "Optional new position as 'x,y,z'." },
      rotation: { type: "string", description: "Optional new rotation as Euler degrees 'x,y,z'." },
      tangent_in: { type: "string", description: "Optional new in-tangent as 'x,y,z'." },
      tangent_out: { type: "string", description: "Optional new out-tangent as 'x,y,z'." },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the host's scene path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
