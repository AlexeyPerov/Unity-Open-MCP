import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M18 Plan 7 / T18.7.3 — Splines extension tool. Requires the
// com.unity.splines package. Mutating: runs the full gate path; paths_hint is
// the host's scene path.
export const splinesAddKnot: Tool = {
  name: "unity_open_mcp_splines_add_knot",
  description:
    "Append a BezierKnot to a spline on a SplineContainer. Requires position " +
    "('x,y,z'); rotation ('x,y,z' Euler degrees) and tangent_in/tangent_out " +
    "('x,y,z') are optional. When tangent_mode is set (AutoSmooth / Broken / " +
    "Mirrored / Linear / BezierSmooth) the knot adopts it and the spline " +
    "recomputes tangents. Returns the new knot index. Mutating: runs the full " +
    "gate path; paths_hint is the host's scene path. Requires the " +
    "com.unity.splines package installed in the project.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      spline_index: {
        type: "integer",
        default: 0,
        description: "Spline index within the container (0 = primary).",
      },
      position: { type: "string", description: "Knot position as 'x,y,z' (required)." },
      rotation: { type: "string", description: "Optional knot rotation as Euler degrees 'x,y,z'." },
      tangent_in: { type: "string", description: "Optional in-tangent as 'x,y,z'." },
      tangent_out: { type: "string", description: "Optional out-tangent as 'x,y,z'." },
      tangent_mode: {
        type: "string",
        description:
          "Optional tangent mode. Valid: AutoSmooth, Broken, Mirrored, Linear, " +
          "BezierSmooth (legacy Continuous / BrokenMirrored also accepted).",
      },
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
