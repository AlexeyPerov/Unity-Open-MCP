import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed prefab instance apply. Mutating: runs the full gate path.
// Applies a prefab instance's overrides back to the prefab asset.
export const prefabApply = makeTool(
  "unity_open_mcp_prefab_apply",
  "Apply a prefab instance's overrides back to its source prefab asset (so all instances inherit " +
    "the change). Mutating: runs the full gate path; `paths_hint` should include the prefab asset " +
    "path (so the gate validates the asset) and the scene path (so the gate validates the instance). " +
    "Resolve the instance by `instance_id` (canonical) or `path` / `name` (fallback).",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Instance ID of the prefab instance to apply (canonical address).",
          },
          path: {
            type: "string",
            description: "Hierarchy path of the prefab instance (fallback address).",
          },
          name: {
            type: "string",
            description: "Prefab instance GameObject name (lowest priority address).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Prefab asset path + scene path (the gate's validation scope)." },
          gate: { ...GATE_PROP },
        },
  },
);
