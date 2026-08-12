// Input simulation — Input System keyboard device events.
//
// Compile-gated on com.unity.inputsystem (UNITY_OPEN_MCP_EXT_INPUTSIM_IS).
// Queues Keyboard state events through the new Input System.
//
// K1 fix: `tap`/`hold` accept `advance_frames` to pump N player-loop frames
// BETWEEN down and up, so polling game code (Keyboard.current[Key.Space]
// .wasPressedThisFrame) can observe the press. Without `advance_frames`, both
// updates run in one synchronous call and no MonoBehaviour.Update ticks while
// the key is down — the press is invisible to polling code (callback-driven
// InputAction.performed still fires). For polling input, pass advance_frames ≥ 1
// (or use `down`, inputsim_step, `up`).
//
// K3 doc: `up` queues an EMPTY KeyboardState, releasing EVERY held key (the
// named key, mods, and any key held by a prior `down`) — per-key release would
// require cross-call state. This is documented, not silent.
//
// Play-mode only. Gate-free. Does NOT cover legacy UnityEngine.Input.
import { makeTool } from "./schema-fragments.js";

export const inputsimKey = makeTool(
  "unity_open_mcp_inputsim_key",
  "Queue a keyboard event through the Input System (Keyboard.current) during " +
    "play mode. Covers gameplay reading Keyboard.current / Key enum / " +
    "InputAction.performed. `key` accepts a Unity Key enum name ('Space', 'W', " +
    "'LeftArrow', 'Digit1') or a single character ('a', '1'). " +
    "POLLING vs CALLBACK: without `advance_frames`, down+up process in a single " +
    "InputSystem.Update and no MonoBehaviour.Update runs between them — so polling " +
    "code (wasPressedThisFrame) CANNOT see a `tap`/`hold`; only callback-driven " +
    "InputAction.performed fires. Pass `advance_frames` ≥ 1 to pump that many " +
    "player-loop frames between down and up so polling code observes the press " +
    "(or split into `down` → inputsim_step → `up`). `up` releases ALL held keys " +
    "(the named key + mods + anything held by a prior `down`), since per-key " +
    "release would require cross-call state. Play-mode only — refuses with " +
    "`play_mode_required` otherwise. Gate-free. Requires com.unity.inputsystem. " +
    "Does NOT cover legacy UnityEngine.Input.",
  {
    required: ["action", "key"],
    properties: {
      action: {
        type: "string",
        enum: ["down", "up", "tap", "hold"],
        description:
          "down = key pressed (queued, processed next InputSystem.Update); up = " +
          "release ALL held keys (see tool description); tap = down+up (only visible " +
          "to callback-driven input unless advance_frames ≥ 1); hold = down+up after " +
          "advancing frames (so Hold interactions with a duration threshold can fire).",
      },
      key: {
        type: "string",
        description:
          "Unity Key enum name ('Space', 'W', 'LeftArrow', 'Digit1', 'F1') OR a " +
          "single character ('a', '1', '!'). Resolved case-insensitively against " +
          "UnityEngine.InputSystem.LowLevel.Key first, then as a character key.",
      },
      duration_ms: {
        type: "integer",
        default: 100,
        description:
          "Recorded hold duration in milliseconds (for action='hold'). The real " +
          "held-time gate is the number of advanced frames, not wall-clock — pair " +
          "with advance_frames for Input System Hold interactions. Default 100.",
      },
      advance_frames: {
        type: "integer",
        default: 0,
        minimum: 0,
        maximum: 60,
        description:
          "Pump this many player-loop frames (EditorApplication.Step) BETWEEN down " +
          "and up so polling game code observes the press. 0 (default) keeps the " +
          "old single-update behavior (only callback-driven input sees it). Cap 60. " +
          "For polling input (wasPressedThisFrame) pass ≥ 1.",
      },
      shift: { type: "boolean", default: false, description: "Also hold Left Shift. Default false." },
      ctrl: { type: "boolean", default: false, description: "Also hold Left Ctrl. Default false." },
      alt: { type: "boolean", default: false, description: "Also hold Left Alt. Default false." },
    },
  },
);
