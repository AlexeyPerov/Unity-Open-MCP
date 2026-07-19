import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { IGNORE_SCENE_DIRTY_BASE, makeTool } from "./schema-fragments.js";

// M16 Plan 5 — typed editor state set (play / pause / stop). Complements
// unity_open_mcp_editor_status (the read side). Entering play mode is a
// disruptive editor transition: if any loaded scene is dirty, Unity's native
// save modal can interrupt the flow. The bridge preflights the scene setup and
// refuses with code `scene_dirty` when that would happen — pass
// `ignore_scene_dirty: true` to accept the risk.
//
// The call does NOT go through the gate envelope: editor state transitions
// write no assets (the gate validates asset-reference fallout, which does not
// apply). It is a gate-free direct-response tool that still runs on the main
// thread. Document settle/poll expectations: entering play mode is
// near-instant in the editor; the response reflects the post-transition state.
export const editorSetState = makeTool(
  "unity_open_mcp_editor_set_state",
  "Set the Unity Editor play / pause / stop state. Complements " +
    "unity_open_mcp_editor_status (the read side). play = enter play mode " +
    "(EditorApplication.EnterPlaymode); pause = toggle pause while playing; " +
    "stop = exit play mode. Entering play mode is disruptive — when a loaded " +
    "scene has unsaved changes, Unity's native save modal can interrupt the " +
    "flow, so the bridge preflights and refuses with code `scene_dirty`; pass " +
    "ignore_scene_dirty: true to accept the risk. Writes no assets, so the call " +
    "is gate-free and returns the post-transition state directly. Prefer this " +
    "over raw execute_csharp EditorApplication.isPlaying = true — schema-" +
    "validated, undo-safe, and the dirty-scene refusal is surfaced as a " +
    "structured error instead of a hanging save dialog.",
  {
    required: ["state"],
        properties: {
          state: {
            type: "string",
            enum: ["play", "pause", "stop"],
            description:
              "Target state. 'play' → enter play mode (refuses if already playing " +
              "unless force: true); 'pause' → toggle pause on the current play " +
              "session (no-op when not playing); 'stop' → exit play mode (refuses " +
              "when not playing).",
          },
          force: {
            type: "boolean",
            default: false,
            description:
              "When true, skip the idempotent 'already in target state' check. " +
              "Useful for re-entering play mode after a code change without first " +
              "stopping. Default false.",
          },
          ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "Opt out of the active-scene dirty guard. When false (default), the " + "call is refused with code `scene_dirty` if any loaded scene has " + "unsaved changes — entering play mode would otherwise trigger " + "Unity's native save modal. Set true to proceed and accept the risk." },
        },
  },
);
