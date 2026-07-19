import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 6 — typed script read. Read-only (gate-free). Reads a .cs file from
// disk with optional line slicing so an agent can inspect a script without
// invoking execute_csharp or the file system escape hatch. Token-bounded by a
// line range + a soft character cap.
export const scriptRead = makeTool(
  "unity_open_mcp_script_read",
  "Read a C# source (.cs) file from disk. Read-only and gate-free. Returns the file " +
    "path, line count, and a `lines[]` array of numbered source lines, optionally " +
    "sliced by start_line / end_line (1-based, inclusive). Use this to inspect a script " +
    "before script_write, or to learn a type's implementation before invoke_method. " +
    "Prefer this over execute_csharp with File.ReadAllText — schema-validated and " +
    "token-bounded by the line range. The file must live under the project (Assets/, " +
    "Packages/, or the project root); absolute paths outside the project are refused.",
  {
    required: ["file_path"],
        properties: {
          file_path: {
            type: "string",
            description:
              "Project-relative path to the .cs file (e.g. \"Assets/Scripts/Player.cs\"). " +
              "Must end in .cs and live under the project root.",
          },
          start_line: {
            type: "integer",
            default: 1,
            minimum: 1,
            description: "First line to return (1-based, inclusive).",
          },
          end_line: {
            type: "integer",
            default: 0,
            minimum: 0,
            description: "Last line to return (1-based, inclusive). 0 = through end of file.",
          },
          max_lines: {
            type: "integer",
            default: 2000,
            minimum: 1,
            description:
              "Hard cap on the number of lines returned. Larger ranges are truncated and " +
              "reported via 'truncated' + 'totalLines'.",
          },
        },
  },
);
