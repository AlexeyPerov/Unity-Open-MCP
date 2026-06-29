import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 9 — typed Render/Lighting settings mutator. Mutating: set
// RenderSettings knobs by key/value patches. RenderSettings is scene-scoped —
// writes only persist when the active scene is marked dirty (the tool does
// that automatically). paths_hint must scope to the active scene path (and/or
// the lighting/render settings asset) so the gate covers the scene-side
// fallout.
export const settingsSetLighting: Tool = {
  name: "unity_open_mcp_settings_set_lighting",
  description:
    "Mutating: set one or more Render/Lighting (RenderSettings) values by key for the " +
    "currently-active scene. RenderSettings is scene-scoped — the tool marks the active scene " +
    "dirty on each successful patch so the write persists (call scene_save to commit). Runs the " +
    "full gate path; `paths_hint` should scope to the active scene path (the scene is what gets " +
    "dirtied) and/or the render/lighting settings asset. Per-key failures are accumulated. " +
    "Supported keys: ambientMode (AmbientMode name), ambientIntensity (float), ambientColor " +
    "([r,g,b,(a)]), fog (bool), fogMode (FogMode name), fogDensity (float), fogColor " +
    "([r,g,b,(a)]), fogStartDistance (float), fogEndDistance (float). Returns the list of " +
    "applied keys + any per-key warnings.",
  inputSchema: {
    type: "object",
    required: ["fields", "paths_hint"],
    properties: {
      fields: {
        type: "array",
        description:
          "Patches to apply in order. Each: { key, value }. value shape: Color [r,g,b,(a)] for " +
          "ambientColor/fogColor; bool for fog; enum name for ambientMode/fogMode; float for the " +
          "scalar keys.",
        items: {
          type: "object",
          required: ["key", "value"],
          properties: {
            key: {
              type: "string",
              description:
                "RenderSettings key. Supported: ambientMode, ambientIntensity, ambientColor, " +
                "fog, fogMode, fogDensity, fogColor, fogStartDistance, fogEndDistance.",
            },
            value: {
              description:
                "New value. Colors as [r,g,b,(a)] 0-1; bool for fog; enum name " +
                "(AmbientMode/FogMode) for ambientMode/fogMode; float for the rest.",
            },
          },
          additionalProperties: false,
        },
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — pass the active scene path (the scene gets dirtied on each write) " +
          "and/or the render/lighting settings asset. There is no whole-project fallback.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
