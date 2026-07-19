import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.3 — Project Settings remainder. Mutating: patch the
// TimeManager fields. Writes ProjectSettings/TimeManager.asset and runs the
// full gate path.
export const settingsSetTime = makeTool(
  "unity_open_mcp_settings_set_time",
  "Mutating: patch one or more TimeManager values by key. Writes " +
    "ProjectSettings/TimeManager.asset and runs the full gate path; " +
    "`paths_hint` must be [\"ProjectSettings/TimeManager.asset\"]. Per-key " +
    "failures are accumulated. Supported keys: fixedDeltaTime (float), " +
    "timeScale (float), maximumDeltaTime (float), captureFramerate (int — a " +
    "runtime Time value, not serialized in TimeManager.asset; applied via the " +
    "Time static API). Returns the list of applied keys + any per-key warnings.",
  {
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
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — pass [\"ProjectSettings/TimeManager.asset\"]. " + "There is no whole-project fallback." },
          gate: { ...GATE_PROP },
        },
  },
);
