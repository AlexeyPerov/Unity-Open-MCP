import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.3 — Project Settings remainder. Read-only: TimeManager
// snapshot (fixedDeltaTime / timeScale / maximumDeltaTime / captureFramerate).
// Gate-free direct-response read.
export const settingsGetTime: Tool = {
  name: "unity_open_mcp_settings_get_time",
  description:
    "Read-only: TimeManager snapshot. Reports the runtime Time values " +
    "(timeScale, fixedDeltaTime, maximumDeltaTime, smoothDeltaTime, " +
    "captureFramerate) plus the on-disk TimeManager.asset-backed settings " +
    "(fixedDeltaTimeSetting, timeScaleSetting, maximumDeltaTimeSetting) when " +
    "ProjectSettings/TimeManager.asset is present. Gate-free. Use " +
    "settings_set_time to patch the values.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
