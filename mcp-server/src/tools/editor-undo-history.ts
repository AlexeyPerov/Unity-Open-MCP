import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.4 — undo/redo stack read. Read-only counterpart to
// editor_undo / editor_redo / editor_clear_history.
export const editorUndoHistory: Tool = {
  name: "unity_open_mcp_editor_undo_history",
  description:
    "Read the recent editor undo/redo stack entries (names) with a bounded " +
    "result size. Read-only and gate-free. Returns count/total/truncated so " +
    "agents can detect when the local cap clipped the timeline.",
  inputSchema: {
    type: "object",
    properties: {
      max_entries: {
        type: "integer",
        minimum: 1,
        default: 50,
        description:
          "Requested number of recent entries. The bridge enforces a hard cap of 50.",
      },
    },
    additionalProperties: false,
  },
};
