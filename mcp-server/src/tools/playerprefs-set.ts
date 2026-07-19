import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.2 — KV preferences. Mutating: set a PlayerPrefs key to a
// typed value and persist. Direct-response (writes to
// Library/PlayerPreferences, not a project asset — gate-free like
// editor_undo). Calls PlayerPrefs.Save() so the change persists.
export const playerprefsSet = makeTool(
  "unity_open_mcp_playerprefs_set",
  "Mutating: set a PlayerPrefs key to a typed value and persist. Calls " +
    "PlayerPrefs.Save() so the value survives a restart. Writes to " +
    "Library/PlayerPreferences (NOT a project asset) — like editor_undo it is a " +
    "mutating editor-state write that has nothing for the asset gate to " +
    "validate, so no paths_hint is required. Use editorprefs_set for " +
    "editor-scoped preferences. (playerprefs_delete_all is deliberately NOT " +
    "shipped — an irreversible project-wide wipe with no key filter is too " +
    "dangerous for a single-call tool; route it through execute_csharp with an " +
    "explicit confirm.)",
  {
    required: ["key", "value", "type"],
        properties: {
          key: {
            type: "string",
            description: "The preference key to write.",
          },
          value: {
            description:
              "The value. Pass an int for type:int, a float for type:float, or a " +
              "string for type:string.",
          },
          type: {
            enum: ["int", "float", "string"],
            description:
              "Value type. Determines which PlayerPrefs setter (SetInt / SetFloat / " +
              "SetString) is called.",
          },
        },
  },
);
