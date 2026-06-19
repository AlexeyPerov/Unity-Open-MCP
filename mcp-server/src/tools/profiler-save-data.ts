import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 7 — typed profiler snapshot save. Mutating: writes a JSON snapshot
// to disk (composed from the read surfaces already in this tool family —
// status / memory / rendering / script / frame — so the shape stays in sync).
// Runs the full gate path with paths_hint scoped to the destination path.
// Folds UMCP `profiler-save-data` and UCP `profiler/capture/save` (the .json
// structured-snapshot path; UCP's binary-log path is editor-runtime-gated and
// folds into profiler_set_config's binary_log warning surface instead).
export const profilerSaveData: Tool = {
  name: "unity_open_mcp_profiler_save_data",
  description:
    "Save a structured JSON profiler snapshot to a path. Composes the runtime status, memory " +
    "allocator stats, rendering environment, script timing, and a single-frame capture into one " +
    "document and writes it (parent directories are created). Mutating: runs the full gate path; " +
    "`paths_hint` must be scoped to the destination file path. The destination must live inside " +
    "the project root and end in `.json`. Read back via profiler_load_data.",
  inputSchema: {
    type: "object",
    required: ["file_path", "paths_hint"],
    properties: {
      file_path: {
        type: "string",
        description:
          "Destination path for the snapshot. Must be project-relative, end in `.json`, and live " +
          "under the project root (`..` and absolute-outside paths are refused). Parent " +
          "directories are created.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the destination `.json` path.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
