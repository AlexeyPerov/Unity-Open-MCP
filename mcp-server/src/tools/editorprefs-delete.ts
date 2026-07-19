import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.2 — KV preferences. Mutating: delete a single EditorPrefs
// key. Direct-response (no project-asset write — gate-free). EditorPrefs writes
// through immediately.
export const editorprefsDelete = makeTool(
  "unity_open_mcp_editorprefs_delete",
  "Mutating: delete a single EditorPrefs key. Returns { existed, deleted } so " +
    "the caller knows whether the key was present. Writes to the editor registry " +
    "(NOT a project asset) — a mutating editor-state write that has nothing for " +
    "the asset gate to validate, so no paths_hint is required. EditorPrefs are " +
    "editor-scoped — use playerprefs_delete for project-scoped preferences.",
  {
    required: ["key"],
        properties: {
          key: {
            type: "string",
            description: "The preference key to delete.",
          },
        },
  },
);
