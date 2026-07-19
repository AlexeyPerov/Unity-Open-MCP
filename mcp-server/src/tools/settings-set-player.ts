import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, IGNORE_SCENE_DIRTY_BASE, makeTool } from "./schema-fragments.js";

// M16 Plan 9 — typed PlayerSettings mutator. Mutating: set one or more
// PlayerSettings knobs by key/value patches. paths_hint must be scoped to
// ProjectSettings/ProjectSettings.asset.
export const settingsSetPlayer = makeTool(
  "unity_open_mcp_settings_set_player",
  "Mutating: set one or more PlayerSettings values by key. Writes ProjectSettings/" +
    "ProjectSettings.asset and runs the full gate path; `paths_hint` must be " +
    "[\"ProjectSettings/ProjectSettings.asset\"]. Per-key failures are accumulated (bad keys " +
    "are reported as warnings) so a single bad patch does not abort the batch. Some keys " +
    "(scriptingBackend-flavored ones) can force a recompile, so the lifecycle is " +
    "restart_then_settle and the active-scene dirty guard preflights the call " +
    "(ignore_scene_dirty: true to opt out). Supported keys: companyName, productName, " +
    "bundleVersion (string); runInBackground, defaultIsNativeResolution (bool); " +
    "defaultScreenWidth, defaultScreenHeight (int); colorSpace (ColorSpace name); " +
    "activeInputHandler / activeInputHandling / inputHandling (0/1/2 or " +
    "old|inputsystem|both). Returns the list of applied keys + any per-key warnings.",
  {
    required: ["fields", "paths_hint"],
        properties: {
          fields: {
            type: "array",
            description:
              "Patches to apply in order. Each: { key: string, value: any }. value shape: strings " +
              "for companyName/productName/bundleVersion/colorSpace; bool for runInBackground / " +
              "defaultIsNativeResolution; int for defaultScreenWidth/defaultScreenHeight; int (0/1/2) " +
              "or name (old|inputsystem|both) for activeInputHandler.",
            items: {
              type: "object",
              required: ["key", "value"],
              properties: {
                key: {
                  type: "string",
                  description:
                    "PlayerSettings key. See the tool description for the supported key list.",
                },
                value: {
                  description:
                    "New value for the key. Scalars/strings/bools as JSON primitives.",
                },
              },
              additionalProperties: false,
            },
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — pass [\"ProjectSettings/ProjectSettings.asset\"]. There is no " + "whole-project fallback." },
          gate: { ...GATE_PROP },
          ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "Bypass the active-scene dirty guard. Set true to proceed and accept the risk of a " + "native save prompt when any loaded scene has unsaved changes." },
        },
  },
);
