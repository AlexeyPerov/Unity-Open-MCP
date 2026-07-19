import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, IGNORE_SCENE_DIRTY_BASE, makeTool } from "./schema-fragments.js";

// M16 Plan 9 — typed build-target switch. Mutating: EditorUserBuildSettings
// .SwitchActiveBuildTarget. May trigger a recompile + domain reload (the
// lifecycle is restart_then_settle).
//
// paths_hint: ProjectSettings files are rewritten when the active target
// changes (ProjectSettings/ProjectSettings.asset + the build-target-specific
// override assets). Scope paths_hint to "ProjectSettings" so the gate covers
// the rewritten settings.
export const buildSetTarget = makeTool(
  "unity_open_mcp_build_set_target",
  "Mutating: switch the active build target via EditorUserBuildSettings.SwitchActiveBuildTarget. " +
    "The target must be a valid BuildTarget name (use build_get_targets to enumerate). Switching " +
    "can trigger a recompile / domain reload; the bridge blocks on the post-switch compile via " +
    "its restart_then_settle lifecycle, and the active-scene dirty guard preflights the call " +
    "(pass ignore_scene_dirty: true to opt out). Runs the full gate path; `paths_hint` should " +
    "scope to \"ProjectSettings\" (target switches rewrite ProjectSettings/*.asset).",
  {
    required: ["target", "paths_hint"],
        properties: {
          target: {
            type: "string",
            description:
              "BuildTarget name, e.g. 'StandaloneWindows64', 'iOS', 'Android', 'WebGL'. Parsed " +
              "case-insensitively. Use build_get_targets to enumerate valid names.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — pass [\"ProjectSettings\"] so the gate covers the rewritten " + "settings assets. There is no whole-project fallback." },
          gate: { ...GATE_PROP, description: "Gate mode. Default 'enforce' — fails the call if the switch surfaces new errors." },
          ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "Bypass the active-scene dirty guard. By default a disruptive op (recompile / scene " + "switch) is refused with scene_dirty when any loaded scene has unsaved changes, so " + "Unity's native save modal never interrupts the flow. Set true to proceed and accept " + "the risk of a native save prompt." },
        },
  },
);
