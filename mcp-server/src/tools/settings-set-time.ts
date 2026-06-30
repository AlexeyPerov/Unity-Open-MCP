import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.3 — Project Settings remainder. Mutating: patch the
// TimeManager fields. Writes ProjectSettings/TimeManager.asset and runs the
// full gate path.
export const settingsSetTime: Tool = {
  name: "unity_open_mcp_settings_set_time",
  description:
    "Mutating: patch one or more TimeManager values by key. Writes " +
    "ProjectSettings/TimeManager.asset and runs the full gate path; " +
    "`paths_hint` must be [\"ProjectSettings/TimeManager.asset\"]. Per-key " +
    "failures are accumulated. Supported keys: fixedDeltaTime (float), " +
    "timeScale (float), maximumDeltaTime (float), captureFramerate (int — a " +
    "runtime Time value, not serialized in TimeManager.asset; applied via the " +
    "Time static API). Returns the list of applied keys + any per-key warnings.",
  inputSchema: {
    type: "object",
    required: ["fields", "paths_hint"],
    properties: {
      fields: {
        type: "array",
        description:
          "Patches to apply in order. Each: { key: string, value: number }. " +
          "fixedDeltaTime / timeScale / maximumDeltaTime take a float; " +
          "captureFramerate takes an int.",
        items: {
          type: "object",
          required: ["key", "value"],
          properties: {
            key: {
              type: "string",
              description:
                "TimeManager key. Supported: fixedDeltaTime, timeScale, " +
                "maximumDeltaTime, captureFramerate.",
            },
            value: {
              description:
                "New value (float for fixedDeltaTime / timeScale / " +
                "maximumDeltaTime; int for captureFramerate).",
            },
          },
          additionalProperties: false,
        },
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — pass [\"ProjectSettings/TimeManager.asset\"]. " +
          "There is no whole-project fallback.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
