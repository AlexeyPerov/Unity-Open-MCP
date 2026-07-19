import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed prefab stage save. Mutating: runs the full gate path.
// Saves the currently open prefab stage to its asset path.
export const prefabSave = makeTool(
  "unity_open_mcp_prefab_save",
  "Save the currently open prefab edit stage back to its asset path. No-op (with a note) when " +
    "no prefab stage is open. Mutating: runs the full gate path; `paths_hint` is the prefab asset " +
    "path being saved (best-effort when omitted).",
  {
    properties: {
          paths_hint: { ...PATHS_HINT_TYPE, description: "Prefab asset path being saved (the gate's validation scope). Best-effort when omitted." },
          gate: { ...GATE_PROP },
        },
  },
);
