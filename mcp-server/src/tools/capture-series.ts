// Senses — frame-scheduled capture series inside the play-mode player loop.
//
// The bridge starts a capture operation on its shared play-mode scheduler and
// returns an operation id; this server polls POST /playmode-operations to the
// terminal result (tool-router `routePlaymodeOperation`), or starts a `jobs`
// play-mode operation when the series (plus its start_on wait and encoding)
// can outlast the inline budget. Live-only like every unity_senses_* tool.
import { makeTool } from "./schema-fragments.js";
import { conditionSchema } from "./playtest-conditions.js";

/** Mirrors the bridge's FrameSchedule.MaxFrames. */
export const CAPTURE_SERIES_MAX_FRAMES = 120;
export const CAPTURE_SERIES_MAX_MS = 600_000;
export const CAPTURE_SERIES_DEFAULT_START_TIMEOUT_MS = 30_000;
/** Longest estimated series (start_on wait + schedule + encoding) polled inline. */
export const CAPTURE_SERIES_INLINE_LIMIT_MS = 40_000;

const rect = (what: string) => ({
  type: "array",
  minItems: 4,
  maxItems: 4,
  items: { type: "number" },
  description: `${what} [x, y, w, h] in screen pixels, bottom-left origin — the space of ui_snapshot rects, so a node's rect can be passed as is.`,
}) as const;

export const captureSeries = makeTool(
  "unity_senses_capture_series",
  "Capture the composed game frame (every camera + Screen Space Overlay UI, the frame " +
    "unity_senses_screenshot view 'composed' returns in play mode; native size by default) at SCHEDULED " +
    "play-mode frames, inside the player loop — no round trip between frames, so a 300 ms tween or a " +
    "'breathing' effect is caught. SCHEDULE (one): at_ms [..] | every_ms + duration_ms | at_frames [..], " +
    "relative to the call or to a start_on condition (the playtest_wait condition shapes: log / ui / " +
    "object / project_command / frames / ms / all / any; start_timeout_ms bounds it). At most " +
    `${CAPTURE_SERIES_MAX_FRAMES} captures. Each frame entry: frame, play_time, elapsed_ms (offset from the ` +
    "series start), file, width/height, `unchanged` when the pixels equal the previous capture (a static " +
    "screen, or a hidden Game view tab that does not render), and optional metric {luminance (mean " +
    "Rec. 709, 0..1), diff + changed vs the previous " +
    "capture} inside metric_region — a breathing artifact shows as numbers. region crops every frame; " +
    "scale shrinks them; contact_sheet:true adds one tiled PNG labelled '#index F<frame> +<ms>MS' for cheap " +
    "review. Files go to ~/.unity-open-mcp/screenshots/series-<stamp>-<id>/. The call waits for the " +
    "terminal result; series estimated over " +
    `${CAPTURE_SERIES_INLINE_LIMIT_MS / 1000}s return a job_id (unity_open_mcp_jobs wait / cancel). For a tap and the ` +
    "frames after it use inputsim_gesture capture_at_ms. Play-mode only; needs a rendering Game view / " +
    "Simulator (render_target_unavailable otherwise); Unity 6 (unsupported_editor_version elsewhere). " +
    "Not allowed inside batch_execute.",
  {
    properties: {
      at_ms: {
        type: "array",
        minItems: 1,
        maxItems: CAPTURE_SERIES_MAX_FRAMES,
        items: { type: "number", minimum: 0, maximum: CAPTURE_SERIES_MAX_MS },
        description: "Capture at these offsets (ms, the `time` base) from the start, e.g. [0, 100, 200, 300].",
      },
      every_ms: {
        type: "integer",
        minimum: 10,
        maximum: CAPTURE_SERIES_MAX_MS,
        description: "With duration_ms: capture every N ms from 0 to duration_ms inclusive (count ≤ 120).",
      },
      duration_ms: {
        type: "integer",
        minimum: 0,
        maximum: CAPTURE_SERIES_MAX_MS,
        description: "With every_ms: length of the series in ms.",
      },
      at_frames: {
        type: "array",
        minItems: 1,
        maxItems: CAPTURE_SERIES_MAX_FRAMES,
        items: { type: "integer", minimum: 0, maximum: 36_000 },
        description: "Capture these frame offsets from the start (0 = the start frame).",
      },
      start_on: conditionSchema(
        "Start the schedule when this condition holds (same shapes as playtest_wait `condition`); offset 0 is " +
          "the frame that made it true, e.g. {\"log\":{\"pattern\":\"Popup opening\"}}. Omit to start at the call.",
      ),
      start_timeout_ms: {
        type: "integer",
        minimum: 1,
        maximum: CAPTURE_SERIES_MAX_MS,
        description: `With start_on: wall-clock limit for the trigger (default ${CAPTURE_SERIES_DEFAULT_START_TIMEOUT_MS}); on expiry → timed_out, nothing captured. Ignored without start_on.`,
      },
      time: {
        type: "string",
        enum: ["gameplay", "realtime"],
        default: "gameplay",
        description: "Clock for at_ms / every_ms: gameplay (scaled by Time.timeScale, what animations see) or realtime.",
      },
      scale: {
        type: "number",
        minimum: 0.05,
        maximum: 1,
        description: "Downscale every capture (1 = native size, the default).",
      },
      region: rect("Crop every capture to"),
      metric: {
        type: "string",
        enum: ["luminance", "diff"],
        description: "Per-frame numbers: luminance = mean luminance; diff = luminance + mean absolute luminance change and changed-pixel fraction vs the previous capture.",
      },
      metric_region: rect("Compute the metric inside"),
      contact_sheet: {
        type: "boolean",
        default: false,
        description: "Also write one tiled PNG of all captures with frame/time labels (contact_sheet.file).",
      },
      poll_every_frames: {
        type: "integer",
        minimum: 1,
        maximum: 600,
        default: 1,
        description: "Evaluate start_on every N frames.",
      },
      operation_id: {
        type: "string",
        pattern: "^[A-Za-z0-9-]{8,64}$",
        description: "Optional idempotency handle for the bridge operation; jobs set it to the job id.",
      },
    },
  },
);
