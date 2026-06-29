import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 9 — typed Physics / Physics2D mutator. Mutating: set Physics knobs
// by key/value patches. paths_hint must be scoped to
// ProjectSettings/DynamicsManager.asset.
export const settingsSetPhysics: Tool = {
  name: "unity_open_mcp_settings_set_physics",
  description:
    "Mutating: set one or more Physics / Physics2D values by key. Writes ProjectSettings/" +
    "DynamicsManager.asset and runs the full gate path; `paths_hint` must be " +
    "[\"ProjectSettings/DynamicsManager.asset\"]. Per-key failures are accumulated. Supported " +
    "keys: gravity ([x,y,z]), defaultSolverIterations (int), defaultSolverVelocityIterations " +
    "(int), bounceThreshold (float), sleepThreshold (float), defaultContactOffset (float), " +
    "physics2DGravity ([x,y]). Returns the list of applied keys + any per-key warnings.",
  inputSchema: {
    type: "object",
    required: ["fields", "paths_hint"],
    properties: {
      fields: {
        type: "array",
        description:
          "Patches to apply in order. Each: { key, value }. value shape: [x,y,z] for gravity / " +
          "[x,y] for physics2DGravity / int or float for the scalar keys.",
        items: {
          type: "object",
          required: ["key", "value"],
          properties: {
            key: {
              type: "string",
              description:
                "Physics key. Supported: gravity, defaultSolverIterations, " +
                "defaultSolverVelocityIterations, bounceThreshold, sleepThreshold, " +
                "defaultContactOffset, physics2DGravity.",
            },
            value: {
              description:
                "New value. Vector3 [x,y,z] for gravity; Vector2 [x,y] for physics2DGravity; " +
                "int/float scalars for the rest.",
            },
          },
          additionalProperties: false,
        },
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — pass [\"ProjectSettings/DynamicsManager.asset\"]. There is no " +
          "whole-project fallback.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
