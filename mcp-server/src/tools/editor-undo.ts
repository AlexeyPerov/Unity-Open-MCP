import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 5 — typed editor undo. Mutates editor undo state but writes no
// assets — routes as a gate-free direct-response tool. Folds UUMCP editor_undo
// (Undo.PerformUndo). Pair with editor_redo and the undo-recorded typed
// mutators (gameobject_*, component_*, selection_set, ...).
export const editorUndo: Tool = {
  name: "unity_open_mcp_editor_undo",
  description:
    "Perform an editor Undo — reverts the most recent recorded action (one " +
    "step). Mutates editor undo state only (writes no assets), so it is " +
    "gate-free and returns directly without the gate envelope. Works against " +
    "every undo-recorded action the bridge takes (gameobject_*, component_*, " +
    "selection_set, material_set_*, prefab_*, scene_*) plus human actions. " +
    "Use steps > 1 to undo multiple actions in one call. Prefer this over raw " +
    "execute_csharp Undo.PerformUndo() — schema-validated and surfaces the " +
    "post-undo selection so the agent knows what reverted.",
  inputSchema: {
    type: "object",
    properties: {
      steps: {
        type: "integer",
        default: 1,
        minimum: 1,
        description:
          "Number of undo steps to perform. Each step reverts one recorded " +
          "action. Defaults to 1.",
      },
    },
    additionalProperties: false,
  },
};
