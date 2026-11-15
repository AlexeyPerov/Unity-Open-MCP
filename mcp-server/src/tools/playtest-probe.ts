// Playtest — frame probe: sample a value every N play-mode frames into a
// bounded buffer the bridge owns, read it incrementally across calls.
//
// The bridge registers one operation per probe on its shared play-mode
// scheduler (the player-loop tick playtest_wait runs on), so the per-frame hook
// is the bridge's: stop, until, max_samples, repeated errors, an over-budget
// sample, exiting play mode and a domain reload all remove it. Every action
// answers at once (no server polling); read is incremental by cursor. Group
// `playtest`; gate-free, not a project mutation.
import { makeTool } from "./schema-fragments.js";
import { conditionSchema } from "./playtest-conditions.js";

export const PROBE_ACTIONS = ["start", "read", "stop", "list"] as const;
/** Mirrors the bridge's ProbeRegistry caps. */
export const PROBE_MAX_RUNNING = 8;
export const PROBE_MAX_SAMPLES = 5000;
export const PROBE_DEFAULT_MAX_SAMPLES = 200;
export const PROBE_MAX_EVERY_FRAMES = 600;
export const PROBE_DEFAULT_BUDGET_MS = 5;
export const PROBE_MAX_BUDGET_MS = 100;
export const PROBE_MAX_READ = 500;
export const PROBE_DEFAULT_READ = 50;
export const PROBE_ID_PATTERN = "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$";

export const playtestProbe = makeTool(
  "unity_open_mcp_playtest_probe",
  "Trace values over play-mode frames without per-frame hooks of your own: the bridge samples a value every " +
    "every_frames frames into a bounded buffer that later calls read incrementally — instead of " +
    "Application.onBeforeRender handlers and AppDomain.SetData logs from execute_csharp, which leak until a reload. " +
    "SOURCE (exactly one): member = a field / property path read by reflection, on a target GameObject " +
    "(game_object_path or instance_id) or its component (component_type), e.g. member \"health.current\" or " +
    "\"transform.position\" (private fields included; list[0] indexes), or without a target a static path " +
    "Namespace.Type.Member (\"UnityEngine.Time.frameCount\"); expression = a C# expression compiled once (the " +
    "execute_csharp compiler, default usings + `usings`), with a target available as `target` (e.g. " +
    "\"target.velocity.magnitude\" with component_type UnityEngine.Rigidbody). The expression must only read: an " +
    "assignment, ++/--, a `;`, a known write call (SetActive, Destroy, AddForce, Set*, …) or anything execute_csharp " +
    "treats as disruptive is refused at start (expression_not_read_only). ACTIONS: start {id, source, every_frames, " +
    "max_samples, until, budget_ms} — play mode only (play_mode_required); samples carry seq, frame, time " +
    "(gameplay s), realtime (s) and value (numbers, strings, enums, vectors / colours as small objects, the first " +
    "items of a collection; large values become a $truncated marker) or error; values are read at the start of " +
    "the frame, i.e. the state the previous frame left. read {id, since: next_cursor, max} returns only samples " +
    "from the cursor on and never clears the buffer. stop {id} ends it and returns a summary (first / last, " +
    "numeric min / max / mean, errors, cost). list = every probe, running count and the scheduler hooks still " +
    "installed. STATES: running, until_met (until = a playtest_wait condition: object / log / ui / " +
    "project_command / probe / frames / ms, all / any — polled at each sample), max_samples_reached (the buffer's " +
    `upper bound, at most ${PROBE_MAX_SAMPLES}), stopped, play_mode_exited, error (reason sample_errors after ` +
    `consecutive failing samples, or over_budget: a sample costing over budget_ms 3 times in a row, or 10x once). ` +
    `At most ${PROBE_MAX_RUNNING} probes run at once (probe_capacity). A finished buffer stays readable — also in ` +
    "edit mode after play mode ends — until the id is started again or the domain reloads (entering play mode " +
    "usually reloads it). Wait for a probe to end with playtest_wait {\"condition\":{\"probe\":{\"id\":…}}}. Gate-free.",
  {
    required: ["action"],
    properties: {
      action: {
        type: "string",
        enum: [...PROBE_ACTIONS],
        description:
          "start: begin sampling (play mode). read: samples from `since` on (incremental, does not clear). stop: end " +
          "it, return the summary. list: every probe and the hooks still installed.",
      },
      id: {
        type: "string",
        pattern: PROBE_ID_PATTERN,
        description:
          "[start / read / stop] Probe id: 1-64 of [A-Za-z0-9_.-]. start refuses a running id (probe_running); " +
          "starting a finished id replaces its buffer.",
      },
      member: {
        type: "string",
        minLength: 1,
        description:
          "[start] Field / property path. With a target: on the component (component_type) or the GameObject, e.g. " +
          "\"hp\", \"stats.current\", \"transform.position.y\", \"waves[0].count\". Without a target: a static path " +
          "Namespace.Type.Member, e.g. \"UnityEngine.Time.frameCount\". Alternative to expression.",
      },
      expression: {
        type: "string",
        minLength: 1,
        maxLength: 4000,
        description:
          "[start] A read-only C# expression evaluated every sample, e.g. \"UnityEngine.Time.frameCount * 2\", " +
          "\"target.velocity.magnitude\", \"UnityEngine.Object.FindObjectsByType<Enemy>(UnityEngine.FindObjectsSortMode.None).Length\". " +
          "`target` is the target's component (component_type) or GameObject. Alternative to member.",
      },
      game_object_path: {
        type: "string",
        description: "[start] Target GameObject path (loaded scenes, the open prefab stage, DontDestroyOnLoad).",
      },
      instance_id: {
        type: ["string", "integer"],
        default: 0,
        description: "[start] Target GameObject instance id. Alternative to game_object_path.",
      },
      component_type: {
        type: "string",
        description: "[start] Component on the target to read (member) or to bind as `target` (expression).",
      },
      usings: {
        type: "array",
        items: { type: "string" },
        maxItems: 16,
        description: "[start, expression] Extra using namespaces (System, System.Linq, UnityEngine and UnityEditor are always in).",
      },
      every_frames: {
        type: "integer",
        minimum: 1,
        maximum: PROBE_MAX_EVERY_FRAMES,
        default: 1,
        description: "[start] Sample every N frames (the first sample is the first frame after start). until is polled at each sample.",
      },
      max_samples: {
        type: "integer",
        minimum: 1,
        maximum: PROBE_MAX_SAMPLES,
        default: PROBE_DEFAULT_MAX_SAMPLES,
        description: "[start] Buffer size; reaching it ends the probe as max_samples_reached.",
      },
      until: conditionSchema(
        "[start] End the probe when this playtest_wait condition holds (state until_met; reason.fired says which leaf " +
          "and frame), e.g. {\"object\":{\"path\":\"Enemies/Boss\",\"state\":\"destroyed\"}}, {\"ms\":5000}, " +
          "{\"any\":[{\"frames\":600},{\"log\":{\"pattern\":\"Game over\"}}]}.",
      ),
      time: {
        type: "string",
        enum: ["gameplay", "realtime"],
        default: "gameplay",
        description: "[start] Clock for until ms conditions: gameplay (scaled by Time.timeScale) or realtime.",
      },
      budget_ms: {
        type: "number",
        minimum: 0.1,
        maximum: PROBE_MAX_BUDGET_MS,
        default: PROBE_DEFAULT_BUDGET_MS,
        description:
          "[start] Cost budget of one sample (evaluation + serialisation). A sample over it carries cost_ms; 3 in a " +
          "row, or one over 10x, end the probe as error / over_budget.",
      },
      since: {
        type: "integer",
        minimum: 0,
        default: 0,
        description: "[read] Cursor: the next_cursor of the previous read (0 = from the first sample).",
      },
      max: {
        type: "integer",
        minimum: 1,
        maximum: PROBE_MAX_READ,
        default: PROBE_DEFAULT_READ,
        description: "[read] Most samples to return; has_more says whether more are buffered.",
      },
    },
  },
);
