import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 3 — typed scene save. Mutating: writes an opened scene back to
// its .unity asset (or a new path). Runs the full gate path.
export const sceneSave: Tool = {
  name: "unity_open_mcp_scene_save",
  description:
    "Save an opened scene back to its asset file, or to a new `.unity` path when `path` is " +
    "provided. Mutating: runs the full gate path; `paths_hint` should be the destination `.unity` " +
    "asset path. When `name` is omitted, saves the active scene. Returns { saved, name, path } " +
    "with `saved: false` and a note when the scene was not dirty (idempotent). Prefer this over " +
    "raw execute_csharp EditorSceneManager.SaveScene.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      name: {
        type: "string",
        description:
          "Opened scene name to save. Omit (or empty) to save the active scene. Use " +
          "scene_list_opened to enumerate opened scene names.",
      },
      path: {
        type: "string",
        description:
          "Optional destination `.unity` asset path (save-as). Omit to save back to the scene's " +
          "existing path. When provided, must end with '.unity'.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the destination `.unity` asset path (the scene's existing path when " +
          "`path` is omitted).",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
