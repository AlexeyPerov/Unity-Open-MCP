import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.2 — KV preferences. Mutating: delete a single PlayerPrefs
// key and persist. Direct-response (no project-asset write — gate-free like
// editor_undo). Calls PlayerPrefs.Save().
export const playerprefsDelete: Tool = {
  name: "unity_open_mcp_playerprefs_delete",
  description:
    "Mutating: delete a single PlayerPrefs key and persist (PlayerPrefs.Save). " +
    "Returns { existed, deleted } so the caller knows whether the key was " +
    "present. Writes to Library/PlayerPreferences (NOT a project asset) — a " +
    "mutating editor-state write that has nothing for the asset gate to " +
    "validate, so no paths_hint is required. Use editorprefs_delete for " +
    "editor-scoped preferences.",
  inputSchema: {
    type: "object",
    required: ["key"],
    properties: {
      key: {
        type: "string",
        description: "The preference key to delete.",
      },
    },
    additionalProperties: false,
  },
};
