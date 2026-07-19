import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 7 — typed profiler snapshot read-back. Read-only (reads a file
// previously written by profiler_save_data); gate-free direct-response tool.
// Optionally calls Profiler.AddFramesFromFile to load the capture back into
// the Editor Profiler; this tool surfaces BOTH the raw JSON text (caller
// parses) and an `addedToProfiler` boolean so the agent knows whether the
// frames are now browsable in the Profiler window.
export const profilerLoadData = makeTool(
  "unity_open_mcp_profiler_load_data",
  "Read back a previously-saved profiler JSON snapshot and optionally push its frames back " +
    "into the Editor Profiler window. Read-only file access; gate-free. The source path must " +
    "live inside the project root, end in `.json`, and be under a 10 MB cap (rejects oversized " +
    "files up-front to avoid OOM). Returns the raw JSON text plus an `addedToProfiler` flag — " +
    "when `add_to_profiler: true` (default false), Profiler.AddFramesFromFile is invoked so the " +
    "frames are browsable in the Profiler window (no-op for non-binary snapshots; surfaces a " +
    "warning). Use profiler_save_data to write a snapshot first.",
  {
    required: ["file_path"],
        properties: {
          file_path: {
            type: "string",
            description:
              "Path to a snapshot previously written by profiler_save_data. Must be project-relative, " +
              "end in `.json`, and live under the project root. Must be under 10 MB.",
          },
          add_to_profiler: {
            type: "boolean",
            default: false,
            description:
              "When true, also call Profiler.AddFramesFromFile so the frames are browsable in the " +
              "Profiler window. JSON snapshots saved by profiler_save_data are NOT raw binary " +
              "captures, so AddFramesFromFile will refuse them — the request surfaces as a warning " +
              "and the raw JSON is still returned. Set true only if you have a real .raw capture.",
          },
        },
  },
);
