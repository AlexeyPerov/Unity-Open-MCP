// Per-tool wording and codes for the server-polled play-mode operations
// (tool-router `routePlaymodeOperation`): the inline budget, what a stalled or
// failed operation is called, and which start fields are kept in the result.
// A slow operation is never cancelled: it continues as a job. Only the bridge
// ends one that stopped moving, with the tool's stalled code and a reason.
import { INPUTSIM_GESTURE_INLINE_LIMIT_MS } from "./tools/inputsim-gesture.js";
import { PLAYTEST_WAIT_INLINE_LIMIT_MS, PLAYTEST_WAIT_MAX_TIMEOUT_MS } from "./tools/playtest-wait.js";
import { CAPTURE_SERIES_INLINE_LIMIT_MS } from "./tools/capture-series.js";

/**
 * How long past the inline window a direct call keeps polling an operation
 * before it continues as a job (bounded by the host-safe cap); an operation
 * whose observed rate says it cannot end by then is handed over at once.
 */
export const PLAYMODE_INLINE_GRACE_MS = 10_000;

export interface PlaymodeRouteSpec {
  /** Worst-case duration (ms) the direct call polls inline; longer runs become a job. */
  inlineLimitMs: number;
  label: string;
  noun: string;
  /** What the bridge-side cleanup guarantees, for error messages. */
  cleanup: string;
  /** Code for a failed operation that set no error_code of its own. */
  failedCode: string;
  resultHint: string;
  /** Fields of the start answer kept on the terminal result. */
  startMeta: (started: Record<string, any>) => Record<string, unknown>;
  /**
   * Next steps by error code. The bridge names a stall with the tool's own code
   * (gesture_stalled / wait_stalled / capture_series_stalled) and `reason`
   * (editor_paused / time_scale_zero / no_frames / no_progress).
   */
  failureNextSteps: (code: string, reason?: string) => string[] | undefined;
}

/** Next steps for a stalled operation, by why it stood still. */
function stallSteps(noun: string, reason: string | undefined, frozen: string): string[] {
  const why = reason === "editor_paused" ? "The Editor is paused: unpause it (editor_set_state play), then run the " + noun + " again."
    : reason === "time_scale_zero" ? "Time.timeScale is 0 (a pause menu, game code): restore it, or " + frozen + "."
      : reason === "no_frames" ? "No play-mode frame ran: check that the Editor is in play mode, not minimized, and that project code did not replace the player loop."
        : "The operation stopped moving: check editor_status and the console, then run the " + noun + " again.";
  return [why];
}

export const PLAYMODE_ROUTES: Record<string, PlaymodeRouteSpec> = {
  unity_open_mcp_inputsim_gesture: {
    inlineLimitMs: INPUTSIM_GESTURE_INLINE_LIMIT_MS,
    label: "Gesture",
    noun: "gesture",
    cleanup: "released every held finger",
    failedCode: "gesture_failed",
    resultHint: "the trace (and captures)",
    startMeta: started => ({ backend: started.backend, skipped: started.skipped, focusedGameView: started.focusedGameView }),
    failureNextSteps: (code, reason) => code === "gesture_stalled"
      ? stallSteps("gesture", reason, "use time \"realtime\" for a frozen game")
      : undefined,
  },
  unity_open_mcp_playtest_wait: {
    inlineLimitMs: PLAYTEST_WAIT_INLINE_LIMIT_MS,
    label: "Wait",
    noun: "wait",
    cleanup: "restored the time scale",
    failedCode: "wait_failed",
    resultHint: "which condition fired (or the last observed state)",
    startMeta: () => ({}),
    failureNextSteps: (code, reason) => code === "timed_out"
      ? ["The condition did not hold in time: read condition.last (per leaf) for what the game showed; raise timeout_ms, use time_scale, or fix the condition — do not re-poll in a loop.",
        `One longer wait beats repeating this one: timeout_ms goes up to ${PLAYTEST_WAIT_MAX_TIMEOUT_MS} (over ${PLAYTEST_WAIT_INLINE_LIMIT_MS} ms the call runs as a job and returns a job_id), ` +
        "and time_scale (e.g. 4) runs the game faster for the wait only."]
      : code === "exception_thrown"
        ? ["The game threw while waiting: read exception.message / exception.stack (and errors[]) before continuing the playtest."]
        : code === "wait_stalled" ? stallSteps("wait", reason, "wait on a log / ui / object condition, which holds without game time")
          : undefined,
  },
  unity_senses_capture_series: {
    inlineLimitMs: CAPTURE_SERIES_INLINE_LIMIT_MS,
    label: "Capture series",
    noun: "capture series",
    cleanup: "kept the frames already captured",
    failedCode: "capture_series_failed",
    resultHint: "the frames and files",
    startMeta: () => ({}),
    failureNextSteps: (code, reason) => code === "timed_out"
      ? ["start_on never held: read start_on.condition.last, then widen the condition or raise start_timeout_ms."]
      : code === "capture_series_stalled"
        ? [...stallSteps("capture series", reason, "capture the frozen game with time \"realtime\" or at_frames"),
          "The frames captured before the stall are in series.frames (the rest are dropped: \"stalled\")."]
        : code === "play_mode_exited"
          ? ["Play mode ended mid-series: the frames captured before it are in series.frames (the rest are listed captured: false). Enter play mode and run the series again for the rest."]
          : undefined,
  },
};
