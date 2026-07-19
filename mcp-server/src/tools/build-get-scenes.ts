import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 9 — typed build-scenes read. Read-only: the EditorBuildSettings
// scene list (path / enabled / guid). Gate-free direct-response tool.
export const buildGetScenes = makeTool(
  "unity_open_mcp_build_get_scenes",
  "Read-only: the scene list currently in EditorBuildSettings.scenes. Each " +
    "entry is { path, enabled, guid }. Use this to audit which scenes will be " +
    "baked into the player before build_start, and to learn the current shape " +
    "before build_set_scenes. Gate-free.",
  {
    properties: {},
  },
);
