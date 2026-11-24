// Playtest — replay a scenario as a regression check (server-local; group `playtest`).
//
// A scenario (src/playtest/scenario.ts) is a JSON file in the project
// (Playtests/Scenarios/<name>.json): steps by hierarchy path and label, waits,
// project commands, and what each screen must look like afterwards. `export`
// turns a playtest session's own calls or one of its UI crawls into one; `run`
// replays it as a jobs job with no model in the loop — pass / fail per step,
// drift for tolerated changes, retries to tell flaky from broken, `record` to
// (re)write the baseline — recorded into the caller's open playtest session.
// See src/playtest/replay.ts and src/playtest/export.ts.
import { makeTool } from "./schema-fragments.js";
import { SESSION_ID_SCHEMA_PATTERN } from "./playtest-session.js";

export const PLAYTEST_REPLAY_TOOL = "unity_open_mcp_playtest_replay";
export const PLAYTEST_REPLAY_ACTIONS = ["run", "export", "list", "show"] as const;

/** Which arguments each action accepts (anything else is invalid_arguments). */
export const PLAYTEST_REPLAY_ACTION_KEYS: Record<(typeof PLAYTEST_REPLAY_ACTIONS)[number], string[]> = {
  run: [
    "scenario", "from", "retries", "record", "strict", "capture", "vars", "ignore_errors", "step_timeout_ms", "same_screen",
    "placeholder_pattern", "min_touch_dp", "max_minutes", "allow_unsandboxed", "visual",
  ],
  export: ["scenario", "session", "source", "crawl", "to_screen", "description", "overwrite"],
  list: [],
  show: ["scenario"],
};

export const playtestReplay = makeTool(
  PLAYTEST_REPLAY_TOOL,
  "Re-check a flow after a change with no model per step: replay a saved scenario and get pass / fail per step. A " +
    "SCENARIO is a JSON file in the project, Playtests/Scenarios/<name>.json (outside Assets/, versioned with the code; " +
    "name it by name or by a project-relative .json path): a start (restart play_mode | command | none), steps — tap " +
    "{target {path, text, role, index}}, type {target, text, mode, end}, wait {args: playtest_wait arguments}, command " +
    "{command, args}, input {tool, args}, restart, check — and per step what the screen must look like after it " +
    "(expect: screen signature, texts, detector findings). Targets are paths and labels, never instance ids; `${name}` " +
    "in a typed text or arguments is filled from vars. ACTIONS: export {source: crawl | calls, scenario (where to " +
    "write), session (default your open session, else the latest ended one), crawl (which run, default the last), " +
    "to_screen (S5: only the shortest path there), description, overwrite} — crawl = a playtest_explore run's taps " +
    "with its screens as the baseline; calls = your own recorded taps / typing / waits / commands / checks in order " +
    "(no baseline yet: run it once with record:true); list; show {scenario} — its steps, missing baselines, the vars " +
    "it needs; run {scenario, …} (default) starts a jobs job and returns its job_id: jobs wait until terminal, jobs " +
    "cancel stops it. A RUN restarts to the scenario's start (from: current = the screen shown now), then per step " +
    "finds the target (same path + label = exact; same path, other label = label_changed; same label elsewhere = " +
    "path_changed — drift, passes unless strict), acts, waits until the screen matches the expected one and settles " +
    "(similarity ≥ same_screen, default 0.8) and checks: target_not_found / target_not_tappable, action_failed, " +
    "wait_timed_out, command_failed, screen_changed (with the controls / texts added and removed), text_missing, " +
    "console_error (event stream + responses; ignore_errors regexes exempt), new_finding (a new bug-severity detector " +
    "finding; new suspicious ones and fixed ones are drift). A failed attempt is replayed from the start `retries` " +
    "times (default 1; not from: current): verdict passed | flaky (failed, then passed) | failed (consistent: same step, same reason) | " +
    "recorded | cancelled | incomplete. record:true writes what it saw (screens, findings, resolved targets) into the " +
    "file as the new baseline — only when every step executed. VISUAL (settings.visual in the file, or visual:true): " +
    "every step is also compared as a picture — a composed frame (max_edge 640) against <scenario>.visual/<key>/NN.png " +
    "(key = platform + Simulator device / orientation or Game view size; recorded by record:true); a step whose frame " +
    "differs over threshold (default 0.02% of pixels; stray pixels and masks — mask paths, digit texts — ignored) fails " +
    "visual_changed with the regions and the controls there, the baseline, the current frame and a diff image. REQUIRES your open playtest_session (each step is " +
    "recorded with its frame — capture: failures (default) | steps | none — and the report gets a Replays section) " +
    "and an isolated save (start {snapshot:true} or the project's playtest.begin) unless allow_unsandboxed; play mode " +
    "unless the run restarts play mode. Needs the live Editor; requires com.unity.ugui.",
  {
    properties: {
      action: { type: "string", enum: [...PLAYTEST_REPLAY_ACTIONS], description: "run (default) | export | list | show." },
      scenario: {
        type: "string", minLength: 1, maxLength: 300,
        description: "The scenario: a name (Playtests/Scenarios/<name>.json in the project) or a .json path (project-relative; absolute to read only). export: where to write it.",
      },
      session: { type: "string", pattern: SESSION_ID_SCHEMA_PATTERN, description: "[export] The session to export (default: your open session, else the project's latest ended one)." },
      source: {
        type: "string", enum: ["crawl", "calls"],
        description: "[export] crawl = a playtest_explore run (with its screens as the baseline); calls = your own recorded steps.",
      },
      crawl: { type: "integer", minimum: 1, description: "[export, source crawl] Which crawl of the session (1-based; default the last)." },
      to_screen: { type: "string", pattern: "^S[0-9]+$", description: "[export, source crawl] Only the shortest path from the start screen to this screen (crawl screen id)." },
      description: { type: "string", maxLength: 500, description: "[export] What the scenario checks." },
      overwrite: { type: "boolean", description: "[export] Replace an existing file (default false: scenario_exists)." },
      from: {
        type: "string", enum: ["start", "current"],
        description: "[run] start (default) = restart as the scenario's start says; current = run once on the screen shown now (no restart, no retries).",
      },
      retries: { type: "integer", minimum: 0, maximum: 3, description: "[run] Replays of a failed attempt from the start (default 1; none from: current); a later pass = flaky." },
      record: { type: "boolean", description: "[run] Write what this run saw as the scenario's baseline (screens, findings, resolved targets). No retries." },
      strict: { type: "boolean", description: "[run] Fail on drift too (a renamed label, a moved control, a changed match index)." },
      capture: { type: "string", enum: ["failures", "steps", "none"], description: "[run] Frames: failures (default) = a composed frame of the failing step; steps = after every step; none." },
      vars: {
        type: "object", additionalProperties: { type: "string", maxLength: 1000 },
        description: "[run] Values for the scenario's ${name} variables (secret ones are never written to the session log).",
      },
      ignore_errors: {
        type: "array", maxItems: 20, items: { type: "string", minLength: 1, maxLength: 300 },
        description: "[run] More console messages (regex on the first line) that do not fail a step, besides the scenario's settings.ignore_errors.",
      },
      step_timeout_ms: { type: "integer", minimum: 500, maximum: 60000, description: "[run] Per-step wait for the target and the expected screen (default the scenario's, 5000)." },
      same_screen: { type: "number", minimum: 0.5, maximum: 1, description: "[run] Similarity at which the screen counts as the expected one (default the scenario's, 0.8)." },
      placeholder_pattern: { type: "string", maxLength: 500, description: "[run] Regex (.NET) for unresolved-looking texts in the findings check; default the localisation-key pattern; \"\" turns it off." },
      min_touch_dp: { type: "number", minimum: 0, maximum: 200, description: "[run] Smallest acceptable control in dp for the findings check (default 44; 0 = off)." },
      max_minutes: { type: "number", minimum: 0.5, maximum: 60, description: "[run] Wall-clock budget for the whole run, retries included (default 20)." },
      allow_unsandboxed: {
        type: "boolean",
        description: "[run] Replay although the session has no player_state snapshot and the project's playtest.begin did not run. The replay taps through the game for real: progress, currency and saves change.",
      },
      visual: {
        type: "boolean",
        description: "[run] Compare each step as a picture with its visual baseline (default: as the scenario's settings.visual says). true on a record run also turns it on in the file (default settings) and records the frames for this device; false skips it.",
      },
    },
  },
);
