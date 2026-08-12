// Input simulation — Input System touch / swipe device events.
//
// Compile-gated on com.unity.inputsystem (UNITY_OPEN_MCP_EXT_INPUTSIM_IS).
// Queues Touchscreen state events through the new Input System.
//
// K2 fix: `swipe` accepts `advance_frames` to advance a frame AFTER each Moved
// phase, so the swipe genuinely unfolds across frames for polling code (per-frame
// primaryTouch.delta, distance/velocity gesture recognizers). Without
// `advance_frames`, all Moved phases collapse into a single InputSystem.Update —
// documented honestly now (previously mis-advertised as "across real frames").
//
// Play-mode only. Gate-free. Requires com.unity.inputsystem.
import { makeTool } from "./schema-fragments.js";

export const inputsimTouch = makeTool(
  "unity_open_mcp_inputsim_touch",
  "Queue a touch / swipe event sequence through the Input System " +
    "(Touchscreen.current) during play mode. `tap` = press+release at a point; " +
    "`swipe` = press at from_, move across `steps` interpolated points, release " +
    "at to_; `press`/`release` = single-phase control. POLLING vs CALLBACK: " +
    "without `advance_frames`, all touch phases process in a single " +
    "InputSystem.Update — so polling code (per-frame primaryTouch.delta, gesture " +
    "recognizers) sees a collapsed jump, not a real swipe. Pass `advance_frames` " +
    "≥ 1 to advance a frame after each Moved phase so the swipe unfolds across " +
    "real frames for polling code. Callback-driven InputAction.performed fires " +
    "either way. Endpoints resolve target-first (object_id/name/path → screen " +
    "point) or accept screen_x/screen_y directly. NOTE: target resolution here " +
    "uses a plain GameObject.Find name/path match (no ambiguity detection or " +
    "partial-path matching — that lives in the uGUI-gated inputsim_pointer, and " +
    "this tool compiles against the Input System package only); prefer an " +
    "object_id or a full unique path for the target. Play-mode only — refuses " +
    "with `play_mode_required` otherwise. Gate-free. Requires com.unity.inputsystem.",
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
        description: "Touch finger index (0–9) for multi-touch. Default 0.",
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
          "into one update — only callback-driven input sees it then. Cap 60.",
      },
    },
  },
);
