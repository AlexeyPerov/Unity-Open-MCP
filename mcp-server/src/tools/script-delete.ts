import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 6 — typed script delete. Mutating: deletes one or more .cs files
// (and their .meta) from disk. Runs the full gate path with paths_hint scoped
// to the deleted file(s). Deleting a script can break prefab/scene references,
// which the gate surfaces.
export const scriptDelete: Tool = {
  name: "unity_open_mcp_script_delete",
  description:
    "Delete one or more C# source (.cs) files (and their .meta) from the project. Mutating: " +
    "removes the file, refreshes the asset database (which may trigger a recompile / domain " +
    "reload), and runs the full gate path; `paths_hint` must list every .cs path you delete " +
    "so the gate can surface fallout (a removed MonoBehaviour breaks prefab/scene references). " +
    "Returns `file_not_found` per missing path; per-file errors are accumulated and do not " +
    "abort the batch. Prefer this over execute_csharp File.Delete — schema-validated and the " +
    "gate catches dangling script references.",
  inputSchema: {
    type: "object",
    required: ["file_paths", "paths_hint"],
    properties: {
      file_paths: {
        type: "array",
        items: { type: "string" },
        description:
          "Project-relative .cs paths to delete (e.g. [\"Assets/Scripts/Old.cs\"]). Each " +
          "must end in .cs and live under the project root.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the same .cs paths you pass in file_paths.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
