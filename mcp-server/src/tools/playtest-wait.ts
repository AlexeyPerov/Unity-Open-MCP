// Playtest — wait in play mode until a condition holds.
//
// The bridge parses the condition and starts the wait on its shared play-mode
// scheduler (a player-loop tick at the start of EarlyUpdate), returning an
// operation id at once; this server polls POST /playmode-operations to the
// terminal result (tool-router `routePlaymodeOperation`). A wait whose timeout
// is beyond the inline budget runs as a `jobs` play-mode operation and the call
// returns its job id. `timeout_ms` is the wait's own limit: the live client
// forwards it unclamped for this tool (TOOL_OWNED_TIMEOUT_TOOLS); the bridge
// reads it both as the tool parameter and as the start call's dispatch budget.
import { makeTool } from "./schema-fragments.js";
import { conditionSchema } from "./playtest-conditions.js";

/** Longest wait (timeout_ms) the direct call polls inline; longer ones become a job. */
export const PLAYTEST_WAIT_INLINE_LIMIT_MS = 40_000;
export const PLAYTEST_WAIT_DEFAULT_TIMEOUT_MS = 30_000;
/** Mirrors the bridge's PlaytestWaitTools.MaxTimeoutMs. */
export const PLAYTEST_WAIT_MAX_TIMEOUT_MS = 600_000;
export const PLAYTEST_WAIT_MAX_POLL_EVERY_FRAMES = 600;

export const playtestWait = makeTool(
  "unity_open_mcp_playtest_wait",
  "Wait in play mode until a condition holds — instead of polling in a loop of calls. CONDITION " +
    "(exactly one key): log {pattern (regex), level} = a console message logged after the start matches; " +
    "ui {text_contains, path, visible, matches} = a ui_snapshot query matches (matches:false = stops " +
    "matching, e.g. a popup closed); object {path | object_id, state: exists | active | inactive | " +
    "destroyed}; project_command {id, args, field} = the project's own read-only predicate returns true " +
    "(no arbitrary C#); frames N; ms N; all / any [leaves] (one level). The bridge evaluates it inside " +
    "the player loop every poll_every_frames frames and the call returns the terminal result: `fired` " +
    "(which condition — e.g. condition.any[1] — at which frame, observed_frame, play_time, elapsed_ms), " +
    "every leaf's last observed state, frames, and console errors logged meanwhile. timeout_ms is " +
    "wall-clock: on expiry the call fails with timed_out plus the last observed state. time_scale " +
    "applies Time.timeScale for the wait only and is restored on every exit (success, timeout, cancel, " +
    "exit play, domain reload) unless game code changed it meanwhile (reported). fail_on [\"exception\"] " +
    "stops at the first exception the game logs (exception_thrown, with message and stack); \"error\" " +
    "also stops on error logs. ms conditions count `time` (gameplay = scaled by Time.timeScale, default; " +
    "realtime). Waits with timeout_ms over " +
    `${PLAYTEST_WAIT_INLINE_LIMIT_MS / 1000}s return a job_id instead — wait on it with unity_open_mcp_jobs ` +
    "(cancel there; exiting play mode / a reload end it, time scale restored). Play-mode only; gate-free; " +
    "one time-scaled wait at a time (playmode_operation_busy). Not allowed inside batch_execute.",
  {
    required: ["condition"],
    properties: {
      condition: conditionSchema(
        "What to wait for: one key — log / ui / object / project_command / frames / ms — or all / any over " +
          "those, e.g. {\"log\":{\"pattern\":\"Wave \\\\d+ cleared\"}}, {\"ui\":{\"text_contains\":\"Victory\"}}, " +
          "{\"object\":{\"path\":\"Enemies/Boss\",\"state\":\"destroyed\"}}, " +
          "{\"any\":[{\"ms\":5000},{\"log\":{\"pattern\":\"Game over\"}}]}.",
      ),
      timeout_ms: {
        type: "integer",
        minimum: 1,
        maximum: PLAYTEST_WAIT_MAX_TIMEOUT_MS,
        default: PLAYTEST_WAIT_DEFAULT_TIMEOUT_MS,
        description:
          "Wall-clock limit for the wait (ms). On expiry → timed_out with the last observed state. Over " +
          `${PLAYTEST_WAIT_INLINE_LIMIT_MS} ms the call runs as a job.`,
      },
      poll_every_frames: {
        type: "integer",
        minimum: 1,
        maximum: PLAYTEST_WAIT_MAX_POLL_EVERY_FRAMES,
        default: 1,
        description:
          "Evaluate the condition every N frames (logs and exceptions are captured every frame regardless). " +
          "Raise it for expensive ui / project_command conditions; fired.frame then lies within the last N frames.",
      },
      time_scale: {
        type: "number",
        exclusiveMinimum: 0,
        maximum: 100,
        description: "Time.timeScale during the wait only (e.g. 4 finishes a 5 s game timer in ~1.25 s); restored on every exit path.",
      },
      time: {
        type: "string",
        enum: ["gameplay", "realtime"],
        default: "gameplay",
        description: "Clock for ms conditions and elapsed_ms: gameplay (scaled by Time.timeScale; a paused game never advances) or realtime (unscaled).",
      },
      fail_on: {
        type: "array",
        items: { type: "string", enum: ["exception", "error"] },
        maxItems: 2,
        description: "Stop early: exception = the game logs an exception (exception_thrown, with stack); error = any error log (error_logged).",
      },
      operation_id: {
        type: "string",
        pattern: "^[A-Za-z0-9-]{8,64}$",
        description:
          "Optional idempotency handle for the bridge operation (8-64 letters, digits, dashes); a retried call with " +
          "the same id returns the same operation. Omit it normally; jobs always set it to the job id.",
      },
    },
  },
);
