import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 5 — typed editor redo. Mutates editor undo state but writes no
// assets — routes as a gate-free direct-response tool (Undo.PerformRedo).
// Pair with editor_undo and the undo-recorded typed mutators.
export const editorRedo: Tool = {
  name: "unity_open_mcp_editor_redo",
  description:
    "Perform an editor Redo — re-applies the most recent undone action (one " +
    "step). Mutates editor undo state only (writes no assets), so it is " +
    "gate-free and returns directly without the gate envelope. Use steps > 1 " +
    "to redo multiple actions in one call. Prefer this over raw execute_csharp " +
    "Undo.PerformRedo() — schema-validated and surfaces the post-redo " +
    "selection so the agent knows what re-applied.",
  inputSchema: {
    type: "object",
    properties: {
      steps: {
        type: "integer",
        default: 1,
        minimum: 1,
        description:
          "Number of redo steps to perform. Each step re-applies one undone " +
          "action. Defaults to 1.",
      },
    },
    additionalProperties: false,
  },
};
