import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.4 — SceneView pose read. Distinct from scene_focus:
// scene_focus frames a target object, while this reports the current editor
// camera pose/pivot ("what is the agent currently looking at?").
export const sceneviewGetCamera = makeTool(
  "unity_open_mcp_sceneview_get_camera",
  "Read the SceneView camera pose: camera position, rotation (Euler), pivot, " +
    "orthographic flag, size, and field of view. Read-only and gate-free. " +
    "Use this to inspect the current editor viewpoint before screenshot, " +
    "scene_focus, or manual takeover.",
  {
    properties: {},
  },
);
