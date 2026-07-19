import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 7 — typed profiler snapshot save. Mutating: writes a JSON snapshot
// to disk (composed from the read surfaces already in this tool family —
// status / memory / rendering / script / frame — so the shape stays in sync).
// Runs the full gate path with paths_hint scoped to the destination path.
// Saves the .json structured-snapshot path (the binary-log path is editor-
// runtime-gated and folds into profiler_set_config's binary_log warning
// surface instead).
export const profilerSaveData = makeTool(
  "unity_open_mcp_profiler_save_data",
  "Save a structured JSON profiler snapshot to a path. Composes the runtime status, memory " +
    "allocator stats, rendering environment, script timing, and a single-frame capture into one " +
    "document and writes it (parent directories are created). Mutating: runs the full gate path; " +
    "`paths_hint` must be scoped to the destination file path. The destination must live inside " +
    "the project root and end in `.json`. Read back via profiler_load_data.",
  {
    required: ["file_path", "paths_hint"],
        properties: {
          file_path: {
            type: "string",
            description:
              "Destination path for the snapshot. Must be project-relative, end in `.json`, and live " +
              "under the project root (`..` and absolute-outside paths are refused). Parent " +
              "directories are created.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the destination `.json` path." },
          gate: { ...GATE_PROP },
        },
  },
);
