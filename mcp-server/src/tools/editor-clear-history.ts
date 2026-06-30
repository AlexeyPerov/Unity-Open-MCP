import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.4 — undo/redo stack reset. Irreversible editor-state
// mutation (history loss), recorded via gate.
export const editorClearHistory: Tool = {
  name: "unity_open_mcp_editor_clear_history",
  description:
    "Clear the editor undo/redo history stack. Irreversible (history cannot be " +
    "recovered). Mutating editor state: runs the gate path and requires " +
    "`paths_hint` scoped to the active scene path.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — active scene path (records undo-history reset in gate history).",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
