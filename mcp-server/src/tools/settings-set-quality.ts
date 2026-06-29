import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 9 — typed QualitySettings mutator. Mutating: set QualitySettings
// knobs by key/value patches. paths_hint must be scoped to
// ProjectSettings/QualitySettings.asset.
export const settingsSetQuality: Tool = {
  name: "unity_open_mcp_settings_set_quality",
  description:
    "Mutating: set one or more QualitySettings values by key. Writes ProjectSettings/" +
    "QualitySettings.asset and runs the full gate path; `paths_hint` must be " +
    "[\"ProjectSettings/QualitySettings.asset\"]. Per-key failures are accumulated. Supported " +
    "keys: level (int — QualitySettings.SetQualityLevel), shadowDistance (float), " +
    "shadowCascades (int), antiAliasing (int), vSyncCount (int), pixelLightCount (int). " +
    "Returns the list of applied keys + any per-key warnings.",
  inputSchema: {
    type: "object",
    required: ["fields", "paths_hint"],
    properties: {
      fields: {
        type: "array",
        description:
          "Patches to apply in order. Each: { key: string, value: number }. All supported " +
          "QualitySettings keys take a number (int or float per the key).",
        items: {
          type: "object",
          required: ["key", "value"],
          properties: {
            key: {
              type: "string",
              description:
                "QualitySettings key. Supported: level, shadowDistance, shadowCascades, " +
                "antiAliasing, vSyncCount, pixelLightCount.",
            },
            value: {
              description:
                "New value (int for level/shadowCascades/antiAliasing/vSyncCount/pixelLightCount; " +
                "float for shadowDistance).",
            },
          },
          additionalProperties: false,
        },
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — pass [\"ProjectSettings/QualitySettings.asset\"]. There is no " +
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
