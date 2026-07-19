import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.4 — undo/redo stack reset. Irreversible editor-state
// mutation (history loss), recorded via gate.
export const editorClearHistory = makeTool(
  "unity_open_mcp_editor_clear_history",
  "Clear the editor undo/redo history stack. Irreversible (history cannot be " +
    "recovered). Mutating editor state: runs the gate path and requires " +
    "`paths_hint` scoped to the active scene path.",
  {
    required: ["paths_hint"],
        properties: {
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — active scene path (records undo-history reset in gate history)." },
          gate: { ...GATE_PROP },
        },
  },
);
