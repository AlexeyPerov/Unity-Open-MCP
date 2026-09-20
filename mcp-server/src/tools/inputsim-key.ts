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
// Named-key release preserves unrelated device state.
//
// Play-mode only. Gate-free. Does NOT cover legacy UnityEngine.Input.
import { makeTool } from "./schema-fragments.js";

export const inputsimKey = makeTool(
  "unity_open_mcp_inputsim_key",
  "Queue a keyboard event through the Input System (Keyboard.current) during " +
    "play mode. Covers gameplay reading Keyboard.current / Key enum / " +
    "InputAction.performed. `key` accepts a Unity Key enum name ('Space', 'W', " +
    "'LeftArrow', 'Digit1') or a single character ('a', '1'). " +
    "POLLING vs CALLBACK: without `advance_frames`, down+up process within one " +
    "dispatch and no MonoBehaviour.Update runs between them — so polling " +
    "code (wasPressedThisFrame) CANNOT see a `tap`/`hold`; only callback-driven " +
    "InputAction.performed fires. Pass `advance_frames` ≥ 1 to pump that many " +
    "player-loop frames between down and up so polling code observes the press " +
    "(split down/step/up can inspect held state). `up` releases the named key " +
    "and explicitly requested modifiers, preserving other held keys. " +
    "Play-mode only — refuses with " +
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
          "release the named key and requested modifiers; tap = down+up (only visible " +
          "to callback-driven input unless advance_frames ≥ 1); hold = down+up after " +
          "advancing frames. Hold interactions still require actual elapsed input time.",
      },
      key: {
        type: "string",
        description:
          "Unity Key enum name ('Space', 'W', 'LeftArrow', 'Digit1', 'F1') OR a " +
          "single character ('a', '1'). Resolved case-insensitively against " +
          "UnityEngine.InputSystem.Key first, then as a character key.",
      },
      duration_ms: {
        type: "integer",
        default: 100,
        description:
          "Recorded hold duration in milliseconds (for action='hold'). The real " +
          "held-time gate is the number of advanced frames, not wall-clock — pair " +
          "with advance_frames for held-state polling. Does not wait this duration or guarantee a Hold interaction. Default 100.",
      },
      advance_frames: {
        type: "integer",
        default: 0,
        minimum: 0,
        maximum: 60,
        description:
          "Pump this many player-loop frames (EditorApplication.Step) BETWEEN down " +
          "and up so polling game code observes the press. 0 (default) keeps the " +
          "old single-dispatch behavior (only callback-driven input sees it). Cap 60. " +
          "For polling input (wasPressedThisFrame) pass ≥ 1.",
      },
      shift: { type: "boolean", default: false, description: "Also hold Left Shift. Default false." },
      ctrl: { type: "boolean", default: false, description: "Also hold Left Ctrl. Default false." },
      alt: { type: "boolean", default: false, description: "Also hold Left Alt. Default false." },
    },
  },
);
