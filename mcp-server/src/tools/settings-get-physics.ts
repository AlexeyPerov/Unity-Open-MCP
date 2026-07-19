import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 9 — typed Physics / Physics2D settings read. Read-only: the global
// Physics knobs (gravity, solver iterations, bounce/sleep thresholds, contact
// offset, simulationMode) + Physics2D gravity + simulationMode. Folds UCP
// settings/physics. Gate-free direct-response tool.
export const settingsGetPhysics = makeTool(
  "unity_open_mcp_settings_get_physics",
  "Read-only: Physics / Physics2D settings snapshot. Surfaces gravity (3D + 2D), " +
    "defaultSolverIterations, defaultSolverVelocityIterations, bounceThreshold, sleepThreshold, " +
    "defaultContactOffset, and the 3D/2D simulationMode. Use settings_set_physics to change " +
    "values. Gate-free.",
  {
    properties: {},
  },
);
