import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.4 — undo/redo stack read. Read-only counterpart to
// editor_undo / editor_redo / editor_clear_history.
export const editorUndoHistory = makeTool(
  "unity_open_mcp_editor_undo_history",
  "Read the Editor's undo and redo stacks as undo steps, by name, with a " +
    "bounded result size: `undo` most recent first (editor_undo " +
    "{\"steps\":n} reverts its first n entries; each entry's `steps` is that " +
    "n) and `redo` next to redo first. Each mutating call is one step, " +
    "named `MCP <tool>` (or after its first undo record); a call that " +
    "records nothing adds none. Read-only and gate-free. Returns " +
    "undoTotal/redoTotal/truncated; `granularity` is `steps`, or `records` " +
    "with a `note` on an Editor that lists only undo records.",
  {
    properties: {
          max_entries: {
            type: "integer",
            minimum: 1,
            default: 50,
            description:
              "Entries to return from each stack (undo and redo). The bridge enforces a hard cap of 50.",
          },
        },
  },
);
