import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M18 Plan 7 / T18.7.3 — Splines extension tool. Requires the
// com.unity.splines package. Mutating: runs the full gate path; paths_hint is
// the host's scene path.
export const splinesModify = makeTool(
  "unity_open_mcp_splines_modify",
  "Set one or more serialized fields on the SplineContainer component (not " +
    "the Spline itself — use set_knot / set_tangent_mode for knot fields). Each " +
    "entry is { field, value, type? } where type is 'int' | 'float' | 'bool' | " +
    "'string' | 'vector' (default inferred from the current value). Per-field " +
    "errors are accumulated — a single bad entry does not abort the batch. " +
    "Mutating: runs the full gate path; paths_hint is the host's scene path. " +
    "Requires the com.unity.splines package installed in the project.",
  {
    required: ["fields_json", "paths_hint"],
        properties: {
          instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
          path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
          name: { type: "string", description: "Host GameObject name (first match)." },
          fields_json: {
            type: "string",
            description:
              "JSON array of { field, value, type? } objects targeting SplineContainer " +
              "serialized fields.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host's scene path." },
          gate: { ...GATE_PROP },
        },
  },
);
