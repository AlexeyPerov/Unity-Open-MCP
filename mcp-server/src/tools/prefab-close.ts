import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — typed prefab stage close. Mutating: runs the full gate path.
export const prefabClose: Tool = {
  name: "unity_open_mcp_prefab_close",
  description:
    "Close the currently open prefab edit stage, optionally saving changes. No-op (with a note) " +
    "when no prefab stage is open. Mutating: runs the full gate path; `paths_hint` is the prefab " +
    "asset path being edited (best-effort — when omitted, the gate runs without a scope).",
  inputSchema: {
    type: "object",
    properties: {
      save: {
        type: "boolean",
        default: true,
        description:
          "When true (default), save changes to the prefab asset before closing. When false, " +
          "discard the in-stage changes.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Prefab asset path being edited (the gate's validation scope). Best-effort when omitted.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
