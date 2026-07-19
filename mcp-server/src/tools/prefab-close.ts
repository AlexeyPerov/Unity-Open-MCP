import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed prefab stage close. Mutating: runs the full gate path.
export const prefabClose = makeTool(
  "unity_open_mcp_prefab_close",
  "Close the currently open prefab edit stage, optionally saving changes. No-op (with a note) " +
    "when no prefab stage is open. Mutating: runs the full gate path; `paths_hint` is the prefab " +
    "asset path being edited (best-effort — when omitted, the gate runs without a scope).",
  {
    properties: {
          save: {
            type: "boolean",
            default: true,
            description:
              "When true (default), save changes to the prefab asset before closing. When false, " +
              "discard the in-stage changes.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Prefab asset path being edited (the gate's validation scope). Best-effort when omitted." },
          gate: { ...GATE_PROP },
        },
  },
);
