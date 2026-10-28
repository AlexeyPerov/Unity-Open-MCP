// Input simulation — backend-aware touch / swipe.
//
// The bridge routes each call to the project's real input backend(s) and reports
// them as `backend` (an array): legacy Input Manager touches (`legacy_touch`,
// which also drive the legacy mouse API through Input.simulateMouseWithTouches
// and therefore StandaloneInputModule) and/or Input System Touchscreen state
// events (`input_system`). With activeInputHandler Both, `auto` injects into both.
// The tool compiles in every project (no com.unity.inputsystem requirement).
//
// K2 fix: `swipe` accepts `advance_frames` to advance a frame AFTER each Moved
// phase, so the swipe genuinely unfolds across frames for polling code. The
// legacy backend always advances at least one frame between phases (its phases
// collapse otherwise). Real-time gestures that last seconds: inputsim_gesture.
//
// Play-mode only. Gate-free.
import { makeTool } from "./schema-fragments.js";

export const inputsimTouch = makeTool(
  "unity_open_mcp_inputsim_touch",
  "Simulate a touch / swipe during play mode through the project's REAL input " +
    "backend, reported as `backend` (array): legacy Input Manager touches (legacy_touch — " +
    "Input.GetTouch, plus the legacy mouse API via Input.simulateMouseWithTouches, which " +
    "StandaloneInputModule reads) and/or Input System Touchscreen events (input_system). " +
    "`backend: auto` (default) injects into every enabled, reachable backend " +
    "(activeInputHandler Both → both when a Touchscreen device exists; skipped backends are " +
    "listed). Refusals: input_backend_unavailable (with the detected configuration and next " +
    "steps), legacy_multitouch_unsupported (legacy is finger 0 only), " +
    "unsupported_editor_version, no_device (explicit input_system without a Touchscreen). " +
    "`tap` = press+release at a point; `swipe` = press at from_, move across `steps` " +
    "interpolated points, release at to_; `press`/`release` = single-phase control. " +
    "POLLING vs CALLBACK: with input_system only and no `advance_frames`, all phases process " +
    "within one dispatch — polling code sees a collapsed jump. Pass `advance_frames` ≥ 1 to " +
    "advance frames after Began and each Moved phase; legacy_touch always advances at least " +
    "one frame between phases. For seconds-long holds/drags use inputsim_gesture. Endpoints " +
    "resolve target-first (name/path to visual center) or accept screen coordinates. " +
    "Root-anchored paths win over suffix matches; ambiguous names/paths return " +
    "ambiguous_target with candidate paths. This tool has no object_id parameter; " +
    "use a unique path or screen coordinates. Play-mode only — refuses " +
    "with `play_mode_required` otherwise. Gate-free.",
  {
    required: ["action"],
    properties: {
      action: {
        type: "string",
        enum: ["tap", "swipe", "press", "release"],
        description:
          "tap = press+release at one point; swipe = press at from_, move across " +
          "`steps` interpolated points, release at to_; press = touch down only; " +
          "release = touch up only.",
      },
      finger: {
        type: "integer",
        default: 0,
        minimum: 0,
        maximum: 9,
        description: "Touch finger index (0–9). The legacy_touch backend supports finger 0 only. Default 0.",
      },
      target: {
        type: "string",
        description:
          "GameObject to tap/press/release — name or hierarchy path. Resolved to a " +
          "screen point via the active camera. Omit when using screen_x/screen_y.",
      },
      screen_x: { type: "number", description: "Screen X (pixels) for tap/press/release. Pair with screen_y." },
      screen_y: { type: "number", description: "Screen Y (pixels) for tap/press/release. Pair with screen_x." },
      from_target: {
        type: "string",
        description: "Swipe start — target name/path (alternative to from_x/from_y).",
      },
      from_x: { type: "number", description: "Swipe start — screen X (alternative to from_target)." },
      from_y: { type: "number", description: "Swipe start — screen Y (alternative to from_target)." },
      to_target: { type: "string", description: "Swipe end — target name/path (alternative to to_x/to_y)." },
      to_x: { type: "number", description: "Swipe end — screen X (alternative to to_target)." },
      to_y: { type: "number", description: "Swipe end — screen Y (alternative to to_target)." },
      duration_ms: { type: "integer", default: 200, description: "Swipe total duration in milliseconds (recorded). Default 200." },
      steps: {
        type: "integer",
        default: 10,
        minimum: 1,
        maximum: 100,
        description:
          "Number of interpolated move events in a swipe. Capped at 100 to bound " +
          "main-thread cost (each step can run advance_frames × Step). Default 10.",
      },
      advance_frames: {
        type: "integer",
        default: 0,
        minimum: 0,
        maximum: 60,
        description:
          "Pump this many player-loop frames (EditorApplication.Step) AFTER each " +
          "Moved phase so the swipe unfolds across real frames for polling code " +
          "(per-frame delta, gesture recognizers). 0 (default) collapses all phases " +
          "into one dispatch — only callback-driven input sees it then. Also advances after Began. " +
          "The legacy_touch backend uses at least 1. Cap 60.",
      },
      backend: {
        type: "string",
        enum: ["auto", "legacy_touch", "input_system"],
        default: "auto",
        description:
          "auto = every enabled, reachable backend; legacy_touch = legacy Input Manager only; " +
          "input_system = Input System Touchscreen only (allows fingers 1–9).",
      },
    },
  },
);
