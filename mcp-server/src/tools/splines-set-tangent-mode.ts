import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M18 Plan 7 / T18.7.3 — Splines extension tool. Requires the
// com.unity.splines package. Mutating: runs the full gate path; paths_hint is
// the host's scene path.
export const splinesSetTangentMode = makeTool(
  "unity_open_mcp_splines_set_tangent_mode",
  "Set the TangentMode on a spline. Pass knot_index to target one knot, or " +
    "knot_index = -1 to set the whole spline. Mode names: AutoSmooth, Broken, " +
    "Mirrored, Linear, BezierSmooth (legacy Continuous / BrokenMirrored also " +
    "accepted). Mutating: runs the full gate path; paths_hint is the host's " +
    "scene path. Requires the com.unity.splines package installed in the " +
    "project.",
  {
    required: ["tangent_mode", "paths_hint"],
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
            default: -1,
            description: "Knot index to target, or -1 to set the whole spline.",
          },
          tangent_mode: {
            type: "string",
            description:
              "Tangent mode. Valid: AutoSmooth, Broken, Mirrored, Linear, BezierSmooth.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host's scene path." },
          gate: { ...GATE_PROP },
        },
  },
);
