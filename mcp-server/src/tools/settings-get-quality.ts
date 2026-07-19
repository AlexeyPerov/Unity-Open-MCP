import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 9 — typed QualitySettings read. Read-only: the quality levels +
// the global QualitySettings knobs. Gate-free direct-response tool.
export const settingsGetQuality = makeTool(
  "unity_open_mcp_settings_get_quality",
  "Read-only: QualitySettings snapshot. Lists every quality level (index/name/isCurrent) plus " +
    "the current level + name and the global knobs (shadowDistance, shadowCascades, " +
    "antiAliasing, vSyncCount, pixelLightCount). Use settings_set_quality to change values. " +
    "Gate-free.",
  {
    properties: {},
  },
);
