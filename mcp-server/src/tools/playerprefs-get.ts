import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.2 — KV preferences. Read-only: get a PlayerPrefs value by
// key. Type (int/float/string) is inferred from the stored value when omitted
// (probes int → float → string). Gate-free direct-response read.
export const playerprefsGet: Tool = {
  name: "unity_open_mcp_playerprefs_get",
  description:
    "Read-only: get a PlayerPrefs value by key. Returns { store, key, type, " +
    "value }. When `type` is omitted, the type is inferred from the stored value " +
    "(probes int → float → string in that order). Gate-free. Use " +
    "editorprefs_get for editor-scoped preferences. Mutating counterpart: " +
    "playerprefs_set.",
  inputSchema: {
    type: "object",
    required: ["key"],
    properties: {
      key: {
        type: "string",
        description: "The preference key to read.",
      },
      type: {
        enum: ["int", "float", "string"],
        description:
          "Optional type hint. When omitted, the type is inferred (int → float " +
          "→ string). When present, the value is read as that type.",
      },
    },
    additionalProperties: false,
  },
};
