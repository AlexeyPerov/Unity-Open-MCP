import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 9 — typed scripting-define setter. Mutating:
// PlayerSettings.SetScriptingDefineSymbols for the active build target group.
// Triggers a recompile (lifecycle: restart_then_settle).
//
// paths_hint: the defines block lives in ProjectSettings/ProjectSettings.asset.
// Scope paths_hint to that asset.
export const buildSetDefines: Tool = {
  name: "unity_open_mcp_build_set_defines",
  description:
    "Mutating: set scripting define symbols for the active build target group " +
    "(PlayerSettings.SetScriptingDefineSymbols). Accepts either a string array (joined with " +
    "';') or a pre-joined ';' string. Pass an empty array (or empty string) to clear the " +
    "defines. Changing defines triggers a recompile; the bridge blocks on the post-write " +
    "compile via its restart_then_settle lifecycle, and the active-scene dirty guard " +
    "preflights the call (pass ignore_scene_dirty: true to opt out). Runs the full gate path; " +
    "`paths_hint` must be [\"ProjectSettings/ProjectSettings.asset\"] (the defines block lives " +
    "in ProjectSettings.asset).",
  inputSchema: {
    type: "object",
    required: ["defines", "paths_hint"],
    properties: {
      defines: {
        description:
          "Define symbols. Either an array (e.g. [\"DEBUG\",\"ENABLE_FEATURE\"]) joined with " +
          "';', or a pre-joined ';' string. Pass an empty array or \"\" to clear.",
        oneOf: [
          { type: "array", items: { type: "string" } },
          { type: "string" },
        ],
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — pass [\"ProjectSettings/ProjectSettings.asset\"]. The defines block " +
          "lives in that asset. There is no whole-project fallback.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
      ignore_scene_dirty: {
        type: "boolean",
        default: false,
        description:
          "Bypass the active-scene dirty guard. Set true to proceed and accept the risk of a " +
          "native save prompt when any loaded scene has unsaved changes.",
      },
    },
    additionalProperties: false,
  },
};
