import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed prefab instance revert. Mutating: runs the full gate path.
export const prefabRevert = makeTool(
  "unity_open_mcp_prefab_revert",
  "Revert a prefab instance to match its source prefab asset (discarding all instance overrides). " +
    "Mutating: runs the full gate path; `paths_hint` should be the scene path holding the instance. " +
    "Resolve the instance by `instance_id` (canonical) or `path` / `name` (fallback).",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Instance ID of the prefab instance to revert (canonical address).",
          },
          path: {
            type: "string",
            description: "Hierarchy path of the prefab instance (fallback address).",
          },
          name: {
            type: "string",
            description: "Prefab instance GameObject name (lowest priority address).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Scene path holding the instance (the gate's validation scope)." },
          gate: { ...GATE_PROP },
        },
  },
);
