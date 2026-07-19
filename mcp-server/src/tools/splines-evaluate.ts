import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M18 Plan 7 / T18.7.3 — Splines extension tool. Requires the
// com.unity.splines package. Read-only, gate-free.
export const splinesEvaluate = makeTool(
  "unity_open_mcp_splines_evaluate",
  "Evaluate a SplineContainer's spline at normalized ratio t (0..1). Returns " +
    "world-space position + tangent (normalized direction) + up vector, plus " +
    "the spline's world-space length. Read-only, gate-free. Address the host " +
    "by instance_id > path > name. Use this to sample positions for object " +
    "placement along a path. Requires the com.unity.splines package installed " +
    "in the project.",
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
          t: {
            type: "number",
            default: 0.5,
            description: "Normalized interpolation ratio (0..1, clamped).",
          },
        },
  },
);
