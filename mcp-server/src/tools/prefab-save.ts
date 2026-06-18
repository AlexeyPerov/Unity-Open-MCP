import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — typed prefab stage save. Mutating: runs the full gate path.
// Saves the currently open prefab stage to its asset path.
export const prefabSave: Tool = {
  name: "unity_open_mcp_prefab_save",
  description:
    "Save the currently open prefab edit stage back to its asset path. No-op (with a note) when " +
    "no prefab stage is open. Mutating: runs the full gate path; `paths_hint` is the prefab asset " +
    "path being saved (best-effort when omitted).",
  inputSchema: {
    type: "object",
    properties: {
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Prefab asset path being saved (the gate's validation scope). Best-effort when omitted.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
