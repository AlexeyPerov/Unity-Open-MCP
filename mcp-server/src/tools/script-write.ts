import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 6 — typed script write. Mutating: creates or overwrites a .cs file
// under the project, after Roslyn pre-write validation. Runs the full gate path
// with paths_hint scoped to the .cs path. Folds UMCP script-update-or-create
// with the validation exposed as a return field rather than a separate tool.
export const scriptWrite: Tool = {
  name: "unity_open_mcp_script_write",
  description:
    "Create or overwrite a C# source (.cs) file under the project. Mutating: writes the " +
    "file, then Roslyn-validates it before persisting and refreshes the asset database " +
    "(which may trigger a recompile / domain reload). Runs the full gate path; `paths_hint` " +
    "must be scoped to the .cs path. By default the call refuses to overwrite an existing " +
    "file unless `overwrite: true` is set. `validate: true` (default) runs a Roslyn parse " +
    "compile of the source first and returns the diagnostics — a failed parse returns " +
    "`validation_failed` and the file is not written. Set `validate: false` only when you " +
    "intentionally want to write a fragment that is not a full compilation unit. Prefer " +
    "this over execute_csharp File.WriteAllText — schema-validated, Roslyn-checked, and " +
    "the gate surfaces any fallout (the new script can break prefab references).",
  inputSchema: {
    type: "object",
    required: ["file_path", "content", "paths_hint"],
    properties: {
      file_path: {
        type: "string",
        description:
          "Project-relative path to the .cs file (e.g. \"Assets/Scripts/Player.cs\"). Must " +
          "end in .cs and live under the project root.",
      },
      content: {
        type: "string",
        description: "The full C# source to write. Must be non-empty.",
      },
      overwrite: {
        type: "boolean",
        default: false,
        description:
          "When false (default) the call refuses to overwrite an existing file. Set true " +
          "to overwrite.",
      },
      validate: {
        type: "boolean",
        default: true,
        description:
          "When true (default) the source is Roslyn-compiled first; parse errors return " +
          "`validation_failed` and the file is not written. The diagnostics are echoed in " +
          "the response. Set false to skip validation (e.g. for partial fragments).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — [\"<the .cs path you are writing>\"].",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
