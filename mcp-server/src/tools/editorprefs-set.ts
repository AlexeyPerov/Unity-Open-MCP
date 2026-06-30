import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.2 — KV preferences. Mutating: set an EditorPrefs key.
// Direct-response (writes to the editor registry, not a project asset —
// gate-free). EditorPrefs writes through immediately (no Save call needed).
export const editorprefsSet: Tool = {
  name: "unity_open_mcp_editorprefs_set",
  description:
    "Mutating: set an EditorPrefs key to a typed value. EditorPrefs writes " +
    "through immediately (no Save call). Writes to the editor registry (NOT a " +
    "project asset) — like editor_undo it is a mutating editor-state write that " +
    "has nothing for the asset gate to validate, so no paths_hint is required. " +
    "EditorPrefs are editor-scoped (shared across projects on the machine) — " +
    "use playerprefs_set for project-scoped player preferences.",
  inputSchema: {
    type: "object",
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
          "Value type. Determines which EditorPrefs setter (SetInt / SetFloat / " +
          "SetString) is called.",
      },
    },
    additionalProperties: false,
  },
};
