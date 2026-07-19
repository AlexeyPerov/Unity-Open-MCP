import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 3 — read-only dirty summary. Gate-free. Per-scene summary of
// unsaved changes.
export const sceneGetDirtySummary = makeTool(
  "unity_open_mcp_scene_get_dirty_summary",
  "Summarize unsaved changes per opened scene (which scenes are dirty, plus a per-scene change " +
    "tally). Read-only (gate-free). Use before scene_save / scene_unload / scene_open to decide " +
    "whether to save first. The response lists every opened scene with name/path/isDirty/" +
    "rootCount, marking dirty ones, so the agent can prompt the user or auto-save before a " +
    "destructive op. Prefer this over raw execute_csharp scene.isDirty — structured, all-scenes " +
    "in one call.",
  {
    properties: {},
  },
);
