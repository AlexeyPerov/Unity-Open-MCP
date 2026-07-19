import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 9 — typed PlayerSettings read. Read-only: snapshot of the common
// PlayerSettings knobs for the active build target. Gate-free direct-response
// tool.
export const settingsGetPlayer = makeTool(
  "unity_open_mcp_settings_get_player",
  "Read-only: PlayerSettings snapshot for the active build target. Surfaces companyName, " +
    "productName, bundleVersion, runInBackground, colorSpace, the first GraphicsAPI, scripting " +
    "backend, API compatibility level, active input handler (numeric + name), target frame " +
    "rate, and default screen dimensions. Use settings_set_player to change values. Gate-free.",
  {
    properties: {},
  },
);
