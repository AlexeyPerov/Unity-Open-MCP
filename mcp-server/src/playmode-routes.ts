// Per-tool wording and codes for the server-polled play-mode operations
// (tool-router `routePlaymodeOperation`): the inline budget, what a stalled or
// failed operation is called, and which start fields are kept in the result.
import { INPUTSIM_GESTURE_INLINE_LIMIT_MS } from "./tools/inputsim-gesture.js";
import { PLAYTEST_WAIT_INLINE_LIMIT_MS } from "./tools/playtest-wait.js";
import { CAPTURE_SERIES_INLINE_LIMIT_MS } from "./tools/capture-series.js";

export interface PlaymodeRouteSpec {
  /** Worst-case duration (ms) the direct call polls inline; longer runs become a job. */
  inlineLimitMs: number;
  label: string;
  noun: string;
  /** What the bridge-side cleanup guarantees, for error messages. */
  cleanup: string;
  /** Code when the inline poll gives up (cancel sent). */
  stalledCode: string;
  /** Code for a failed operation that set no error_code of its own. */
  failedCode: string;
  resultHint: string;
  /** Fields of the start answer kept on the terminal result. */
  startMeta: (started: Record<string, any>) => Record<string, unknown>;
  failureNextSteps: (code: string) => string[] | undefined;
}

export const PLAYMODE_ROUTES: Record<string, PlaymodeRouteSpec> = {
  unity_open_mcp_inputsim_gesture: {
    inlineLimitMs: INPUTSIM_GESTURE_INLINE_LIMIT_MS,
    label: "Gesture",
    noun: "gesture",
    cleanup: "released every held finger",
    stalledCode: "gesture_timeout",
    failedCode: "gesture_failed",
    resultHint: "the trace (and captures)",
    startMeta: started => ({ backend: started.backend, skipped: started.skipped, focusedGameView: started.focusedGameView }),
    failureNextSteps: () => undefined,
  },
  unity_open_mcp_playtest_wait: {
    inlineLimitMs: PLAYTEST_WAIT_INLINE_LIMIT_MS,
    label: "Wait",
    noun: "wait",
    cleanup: "restored the time scale",
    stalledCode: "wait_stalled",
    failedCode: "wait_failed",
    resultHint: "which condition fired (or the last observed state)",
    startMeta: () => ({}),
    failureNextSteps: code => code === "timed_out"
      ? ["The condition did not hold in time: read condition.last (per leaf) for what the game showed; raise timeout_ms, use time_scale, or fix the condition — do not re-poll in a loop."]
      : code === "exception_thrown"
        ? ["The game threw while waiting: read exception.message / exception.stack (and errors[]) before continuing the playtest."]
        : undefined,
  },
  unity_senses_capture_series: {
    inlineLimitMs: CAPTURE_SERIES_INLINE_LIMIT_MS,
    label: "Capture series",
    noun: "capture series",
    cleanup: "kept the frames already captured",
    stalledCode: "capture_series_stalled",
    failedCode: "capture_series_failed",
    resultHint: "the frames and files",
    startMeta: () => ({}),
    failureNextSteps: code => code === "timed_out"
      ? ["Either start_on never held (read start_on.condition.last; widen the condition or raise start_timeout_ms), " +
        "or a gameplay-time schedule stopped advancing (game paused / timeScale 0) and hit its wall-clock limit — the " +
        "frames already captured are kept (outcome capture_timed_out); unpause or use time \"realtime\"."]
      : undefined,
  },
};
