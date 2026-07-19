import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M18 Plan 7 / T18.7.3 — Splines extension tool. Requires the
// com.unity.splines package. Read-only, gate-free.
export const splinesGetKnots = makeTool(
  "unity_open_mcp_splines_get_knots",
  "List every knot on a SplineContainer's spline with position, rotation " +
    "(Euler degrees), tangent in/out, and tangent mode. Read-only, gate-free. " +
    "Address the host by instance_id > path > name. Use this to inspect a " +
    "spline before mutating, or to discover valid knot indices. Requires the " +
    "com.unity.splines package installed in the project.",
  {
    properties: {
          instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
          path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
          name: { type: "string", description: "Host GameObject name (first match)." },
          spline_index: {
            type: "integer",
            default: 0,
            description: "Spline index within the container (0 = primary).",
          },
        },
  },
);
