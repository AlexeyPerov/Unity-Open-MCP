// Input simulation — frame advance. The missing middle of the play-mode
// testing loop: inject input (inputsim_pointer / _key / _touch), then advance N
// frames so gameplay MonoBehaviour.Update / coroutines / tweens run, THEN
// screenshot. Without this, a click that opens a panel with a 0.3s tween
// screenshots as the old panel.
//
// Also the fix for K1/K2/`hold`: a synchronous tool cannot otherwise let a key
// press or swipe be observed by polling game code. `step` lets the documented
// `down → advance → up` workflow actually work.
//
// Implementation: EditorApplication.Step() is synchronous — it runs one full
// player-loop frame inline (Update/FixedUpdate/coroutines/render) before
// returning, so a tight loop pumps N frames within one tool dispatch. Unlike
// QueuePlayerLoopUpdate + Thread.Sleep (documented broken elsewhere in the
// bridge — the sleeping thread is the one that would service the update), Step
// does not deadlock. Play-mode only (no-op in edit mode → play_mode_required).
import { makeTool } from "./schema-fragments.js";

export const inputsimStep = makeTool(
  "unity_open_mcp_inputsim_step",
  "Advance the Unity play-mode player loop by N frames, then return. Pump the " +
    "frames synchronously via EditorApplication.Step (each Step runs one full " +
    "Update/FixedUpdate/coroutines/render cycle inline). Use between an input " +
    "injection (inputsim_pointer / _key / _touch) and a screenshot so gameplay " +
    "code, tweens, and animations actually run before you look — and so polling " +
    "game code (`wasPressedThisFrame`, per-frame touch delta) can observe a " +
    "press/swipe injected with inputsim_key/_touch (which otherwise process in a " +
    "single InputSystem.Update and are invisible to polling). Play-mode only — " +
    "refuses with `play_mode_required` otherwise (Step is a no-op in edit mode). " +
    "Gate-free (writes no assets). Cap 60 frames per call to bound dispatch cost; " +
    "for longer advances, call repeatedly.",
  {
    required: [],
    properties: {
      frames: {
        type: "integer",
        default: 1,
        minimum: 0,
        maximum: 60,
        description:
          "Number of player-loop frames to advance. Default 1. Hard cap 60 to " +
          "bound dispatch cost (each frame is a full Update cycle). 0 is allowed " +
          "and useful as a pure settle tick (one QueuePlayerLoopUpdate).",
      },
      settle_ms: {
        type: "integer",
        default: 0,
        minimum: 0,
        description:
          "Optional extra wall-clock settle in milliseconds AFTER stepping, as a " +
          "single QueuePlayerLoopUpdate (lets one more render tick land before a " +
          "screenshot). Default 0 (no extra tick).",
      },
    },
  },
);
