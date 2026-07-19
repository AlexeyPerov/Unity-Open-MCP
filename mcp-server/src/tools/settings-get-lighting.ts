import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 9 — typed Render/Lighting settings read. Read-only: the
// RenderSettings knobs for the currently-active scene (ambient mode/intensity/
// color, fog + fog mode/density/color/distances, skybox, sun source).
// RenderSettings is scene-scoped in the live Editor — the read reflects the
// active scene's lighting setup. Gate-free direct-response tool.
export const settingsGetLighting = makeTool(
  "unity_open_mcp_settings_get_lighting",
  "Read-only: Render/Lighting settings (RenderSettings) snapshot for the currently-active " +
    "scene. Surfaces ambientMode, ambientIntensity, ambientColor, fog (bool), fogMode, " +
    "fogDensity, fogColor, fogStartDistance, fogEndDistance, and the skybox + sun source " +
    "names when set. RenderSettings is scene-scoped — the read reflects the active scene's " +
    "lighting setup; switch scenes first if you need a different scene's values. Use " +
    "settings_set_lighting to change values. Gate-free.",
  {
    properties: {},
  },
);
