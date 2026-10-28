// Input simulation — keyframed touch gestures over real play-mode frames.
//
// The bridge starts the gesture on its shared play-mode scheduler (a player-loop
// tick at the start of EarlyUpdate) and returns an operation id at once; this
// server turns that into the terminal trace by polling POST /playmode-operations
// (tool-router `routeInputsimGesture`). A gesture longer than the inline budget
// runs as a `jobs` play-mode operation instead and the call returns its job id.
//
// Routing matches inputsim_touch: legacy Input Manager touches (+ emulated
// legacy mouse) and/or Input System Touchscreen events, `backend` reported as an
// array. Legacy is single-finger (id 0). Cancel / exit play / reload release
// every held finger.
import { makeTool } from "./schema-fragments.js";

/** Longest gesture (ms) the direct call waits for inline; longer ones become a job. */
export const INPUTSIM_GESTURE_INLINE_LIMIT_MS = 40_000;
/** Hard cap on a gesture's last keyframe (mirrors the bridge's GesturePlan.MaxDurationMs). */
export const INPUTSIM_GESTURE_MAX_DURATION_MS = 300_000;

const keyframe = {
  type: "object",
  required: ["at_ms"],
  properties: {
    at_ms: {
      type: "number",
      minimum: 0,
      maximum: INPUTSIM_GESTURE_MAX_DURATION_MS,
      description: "Time of this keyframe from the gesture start, in ms (strictly increasing per finger).",
    },
    x: { type: "number", description: "Screen X (coords: screen = pixels, bottom-left origin; normalized = 0..1; target = pixel offset from the target center)." },
    y: { type: "number", description: "Screen Y, same space as x." },
    target: { type: "string", description: "coords: target only — GameObject name or hierarchy path whose visual center is the point." },
    phase: {
      type: "string",
      enum: ["down", "move", "hold", "up"],
      description:
        "down = first keyframe (default there): finger touches. move = default in between: " +
        "interpolate from the previous keyframe. hold = stay at the previous point until at_ms " +
        "(x/y may be omitted). up = last keyframe (default there): move to this point and lift.",
    },
  },
  additionalProperties: false,
} as const;

export const inputsimGesture = makeTool(
  "unity_open_mcp_inputsim_gesture",
  "Play a touch gesture across REAL play-mode frames: per-finger keyframed paths " +
    "(down → move/hold → up) interpolated every frame, so polled gameplay input " +
    "(Input.GetTouch / Input.mousePosition / Touchscreen.current) follows the finger " +
    "for seconds — hold, drag, trace a curve. `time: gameplay` follows Time.timeScale. " +
    "Routing like inputsim_touch: legacy Input Manager touches (legacy_touch, also " +
    "driving the legacy mouse API and StandaloneInputModule) and/or Input System " +
    "Touchscreen events (input_system); `backend` in the result is an array. Legacy is " +
    "single-finger (id 0) — more fingers refuse with legacy_multitouch_unsupported; " +
    "no reachable backend refuses with input_backend_unavailable (with the detected " +
    "configuration), an unverified Editor with unsupported_editor_version. The call " +
    "waits for the terminal result: a bounded trace (per keyframe: frame, screen point, " +
    "uGUI top hit) plus console errors raised meanwhile. Gestures longer than " +
    `${INPUTSIM_GESTURE_INLINE_LIMIT_MS / 1000}s return a job_id instead — wait on it with ` +
    "unity_open_mcp_jobs (you can also start one there directly). Cancel, exiting play " +
    "mode and a domain reload release every held finger. One input gesture at a time " +
    "(playmode_operation_busy). Play-mode only; gate-free. Not allowed inside batch_execute.",
  {
    required: ["fingers"],
    properties: {
      fingers: {
        type: "array",
        minItems: 1,
        maxItems: 10,
        description: "One entry per finger: { id (0-9, default = index), path: [keyframes] }.",
        items: {
          type: "object",
          required: ["path"],
          properties: {
            id: { type: "integer", minimum: 0, maximum: 9 },
            path: { type: "array", minItems: 2, maxItems: 256, items: keyframe },
          },
          additionalProperties: false,
        },
      },
      coords: {
        type: "string",
        enum: ["screen", "normalized", "target"],
        default: "screen",
        description:
          "screen = Screen pixels (bottom-left origin, same as Input.mousePosition); normalized = " +
          "0..1 of the Game view size; target = each keyframe names a target, x/y are pixel offsets.",
      },
      time: {
        type: "string",
        enum: ["realtime", "gameplay"],
        default: "realtime",
        description: "realtime = unscaled wall time; gameplay = scaled by Time.timeScale (a paused game never advances).",
      },
      backend: {
        type: "string",
        enum: ["auto", "legacy_touch", "input_system"],
        default: "auto",
        description:
          "auto = every enabled, reachable backend (legacy_touch when the legacy Input Manager is " +
          "on; input_system when a Touchscreen device exists). Force one with legacy_touch / input_system.",
      },
      operation_id: {
        type: "string",
        pattern: "^[A-Za-z0-9-]{8,64}$",
        description:
          "Optional idempotency handle for the bridge operation (8-64 letters, digits, dashes). A retried " +
          "call with the same id returns the same operation instead of starting another. Omit it normally " +
          "(the server generates one); jobs always set it to the job id.",
      },
    },
  },
);
