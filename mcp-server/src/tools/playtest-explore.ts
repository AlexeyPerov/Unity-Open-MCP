// Playtest — autonomous UI crawler (server-side; group `playtest`).
//
// Starts a crawl of the running game's uGUI as a `jobs` job: snapshot until the
// screen settles, tap the next untried control, record the edge, navigate back
// or restart when a screen is exhausted — no model in the loop. Every new screen
// is captured once and checked by the snapshot detectors; each tap is a recorded
// call of the caller's open playtest_session, and the crawl ends with one mark
// and an Exploration section in the session report. See src/playtest/explore.ts.
import { makeTool } from "./schema-fragments.js";

export const PLAYTEST_EXPLORE_TOOL = "unity_open_mcp_playtest_explore";
export const EXPLORE_RESTART_MODES = ["play_mode", "command", "none"] as const;

export const playtestExplore = makeTool(
  PLAYTEST_EXPLORE_TOOL,
  "Explore the running game's UI on your behalf and report what looks wrong — no model per step. Starts a crawl as a " +
    "jobs job and returns its job_id at once: follow it with unity_open_mcp_jobs {action:\"wait\"}; jobs cancel stops it " +
    "(the result so far is kept). The crawler snapshots the uGUI until it stops changing, taps every visible, " +
    "interactable, unoccluded button / toggle it has not tried on that screen (text fields first — it types fill_text, " +
    "default \"Test\", into each with inputsim_text; leaving controls such as Back / Close / OK last), files each screen it reaches (a screen = its visible controls and texts, numbers masked), walks the " +
    "shortest known path back to a screen with untried controls, and restarts the game when it is stuck (restart: " +
    "play_mode = exit and re-enter play mode (default), then restart_command if given — a boot from the launcher; command = a synchronous project command such as " +
    "project.<owner>.playtest.boot; none = stop; restart_wait = a playtest_wait run after it, e.g. the project's ready predicate). Every new screen is captured once (composed frame) and checked: text " +
    "overflow, controls / texts outside the safe area, overlapping controls, placeholder-looking text (a default " +
    "localisation-key pattern), touch targets under min_touch_dp (default 44 dp), zero-size controls; a tap that " +
    "changed nothing and logged nothing is a dead_button, a screen with nothing to tap a dead_end, console errors " +
    "during a tap are console_error findings (severity bug or suspicious). NEVER TAPPED: labels on the default " +
    "deny-list (buy, purchase, pay, subscribe, delete, remove, reset, log out, rate, share, restore, quit, exit, watch " +
    "ad…) and price-like labels; `deny` adds regexes (label or path), `allow` exempts exact labels / paths, " +
    "default_deny:false drops the built-in list. PROJECT PROFILE: Playtests/profile.json (playtest_memory) fills the " +
    "arguments you leave out (its deny / allow add to yours) and adds back labels, ignored console noise and known " +
    "findings (result known[], not counted). REQUIRES your open playtest_session (no_open_session otherwise), " +
    "isolated by its player_state snapshot (start {snapshot:true}) or the project's playtest.begin command — else " +
    "not_isolated unless allow_unsandboxed:true — and play mode (play_mode_required). Each tap is recorded into the " +
    "session (with its console errors and the new screen's frame); the crawl adds one mark (verdict from its findings) " +
    "and the report gets an Exploration section (screens with frames, findings, denied and untried controls). " +
    "Budgets: max_steps (taps, default 60, ≤ 300), max_minutes (default 10, ≤ 60), max_screens (default 40, ≤ 200), " +
    "max_restarts (default 3). The job result: stop_reason (explored | max_steps | max_time | max_screens | " +
    "no_way_back | cancelled | play_mode_exited | tool_failures | restart_failed | session_ended), coverage, screens, " +
    "edges, findings, denied, untried. Out of scope: sliders, scroll-to-reveal, world-space taps, UI " +
    "Toolkit. Requires com.unity.ugui.",
  {
    properties: {
      max_steps: { type: "integer", minimum: 1, maximum: 300, description: "Taps the crawl may make (exploring and navigating; default 60)." },
      max_minutes: { type: "number", minimum: 0.5, maximum: 60, description: "Wall-clock budget in minutes (default 10)." },
      max_screens: { type: "integer", minimum: 1, maximum: 200, description: "Distinct screens to file before stopping (default 40)." },
      same_screen: {
        type: "number", minimum: 0.5, maximum: 1,
        description: "Similarity (Jaccard over the screens' control / text sets, numbers masked) at which two snapshots are the same screen (default 0.8). Raise it when distinct screens merge; lower it when one screen splits into many.",
      },
      settle_ms: { type: "integer", minimum: 50, maximum: 2000, description: "Pause between the snapshots that decide the screen stopped changing (default 250)." },
      max_settle_ms: { type: "integer", minimum: 0, maximum: 20000, description: "Longest wait for a screen to stop changing before it is taken as it is (default 4000)." },
      restart: {
        type: "string", enum: [...EXPLORE_RESTART_MODES],
        description: "When no known path leads to an untried control: play_mode (default) = exit and re-enter play mode (then restart_command, if given); command = invoke restart_command; none = stop (stop_reason no_way_back).",
      },
      restart_command: {
        type: "string", pattern: "^project\\.",
        description: "A synchronous project command: with restart command, the restart itself; with restart play_mode, run after re-entering play mode — a boot that brings the game from its launcher to the first real screen (e.g. project.<owner>.playtest.boot).",
      },
      restart_args: { type: "object", description: "restart_command's typed arguments." },
      restart_wait: {
        type: "object",
        description: "playtest_wait arguments run after the restart (and restart_command), e.g. {\"condition\":{\"project_command\":{\"id\":\"project.<owner>.playtest.is_ready\",\"field\":\"met\"}},\"timeout_ms\":30000} — the crawl goes on only when it holds.",
      },
      max_restarts: { type: "integer", minimum: 0, maximum: 10, description: "Restarts allowed (default 3)." },
      deny: {
        type: "array", maxItems: 50, items: { type: "string", minLength: 1, maxLength: 200 },
        description: "More controls never to tap: case-insensitive regexes matched against the label and the hierarchy path.",
      },
      allow: {
        type: "array", maxItems: 100, items: { type: "string", minLength: 1, maxLength: 200 },
        description: "Exact labels or hierarchy paths (case-insensitive) exempt from every deny rule.",
      },
      default_deny: { type: "boolean", description: "false = drop the built-in deny-list and price check (only `deny` applies). Default true." },
      routing: {
        type: "string", enum: ["dispatch", "raycast_real"],
        description: "How taps are sent (inputsim_pointer routing): dispatch (default, ExecuteEvents on the control) or raycast_real (a real touch the project's input module resolves).",
      },
      placeholder_pattern: {
        type: "string", maxLength: 500,
        description: "Regex (.NET) for texts that look unresolved. Default: #KEY#, {key}, [KEY.part], snake_case_three_parts, UPPER_SNAKE_THREE, dotted.key.three. \"\" turns the check off.",
      },
      min_touch_dp: { type: "number", minimum: 0, maximum: 200, description: "Smallest acceptable control in dp (px = dp × dpi / 160); default 44, 0 turns the check off." },
      capture: { type: "boolean", description: "Capture each new screen once (default true; the frames go into the session report)." },
      fill_inputs: {
        type: "boolean",
        description: "Type fill_text into each visible text field once (inputsim_text, then end_edit), before the screen's buttons — a form is filled before it is sent. Default true.",
      },
      fill_text: { type: "string", minLength: 1, maxLength: 100, description: "What the crawler types into text fields (default \"Test\")." },
      allow_unsandboxed: {
        type: "boolean",
        description: "Crawl although the session has no player_state snapshot and the project's playtest.begin did not run. The crawl taps through the game for real: progress, currency and saves change.",
      },
    },
  },
);
