// Playtest — the project's playtest memory (server-local; group `playtest`).
//
// Two files in the Unity project, next to the replay scenarios: Playtests/NOTES.md
// (what earlier sessions learned, for the agent and the team) and
// Playtests/profile.json (settings the playtest tools apply on their own:
// deny-list, devices, detector thresholds, back labels, console noise, accepted
// findings). Files only — no bridge, no session needed. See src/playtest/profile.ts.
import { makeTool } from "./schema-fragments.js";
import { SESSION_ID_SCHEMA_PATTERN } from "./playtest-session.js";

export const PLAYTEST_MEMORY_TOOL = "unity_open_mcp_playtest_memory";
export const PLAYTEST_MEMORY_ACTIONS = ["show", "note", "map", "set", "add"] as const;

/** Which arguments each action accepts (anything else is invalid_arguments). */
export const PLAYTEST_MEMORY_ACTION_KEYS: Record<(typeof PLAYTEST_MEMORY_ACTIONS)[number], string[]> = {
  show: ["section"],
  note: ["section", "text"],
  map: ["session", "crawl"],
  set: ["profile"],
  add: ["profile"],
};

export const playtestMemory = makeTool(
  PLAYTEST_MEMORY_TOOL,
  "What earlier playtests of this project learned, kept in the project so the next session (or a teammate's agent) " +
    "starts from it. Two files next to the replay scenarios, versioned with the code, written only on request: " +
    "Playtests/NOTES.md — prose: how to boot, the screen map, dangerous controls, known issues, flaky spots " +
    "(playtest_session start returns its first 4000 characters); Playtests/profile.json — settings the playtest tools " +
    "apply on their own: explore {playtest_explore arguments: deny, allow, restart, restart_command, fill_text, " +
    "same_screen, budgets…}, devices {devices | preset, orientations, max_devices}, detectors {placeholder_pattern, " +
    "min_touch_dp — for every detector run: crawl, devices, session check, replay}, back_labels (labels that leave a " +
    "screen, tried last — e.g. localised \"Back\"), ignore_errors (regexes on console messages that are never findings " +
    "nor replay failures), known [{kind, path (or its tail), text (words of the label or detail), device, reason}] " +
    "(accepted findings: listed as known, not counted; a replay's new finding that matches is not new). A call's own " +
    "arguments win over the profile; deny / allow / ignore_errors add up. An invalid profile makes those tools refuse " +
    "(invalid_profile). ACTIONS: show {section} (default) — the notes (or one section) and the profile with its " +
    "problems; note {section, text} — append a dated bullet under that heading of NOTES.md (created as needed; secrets " +
    "redacted); map {session, crawl} — write the Screen map section from a playtest_explore crawl (default your open " +
    "session's last crawl, else the latest ended session): each screen and the labels tapped to reach it, denied " +
    "controls, dead ends — replaced as a whole by the next map; set {profile} — a JSON merge patch (objects merge, " +
    "null removes a key, lists are replaced); add {profile} — append to lists (explore.deny, explore.allow, " +
    "devices.devices, devices.orientations, back_labels, ignore_errors, known), duplicates skipped. set / add are " +
    "validated before writing: nothing is written on a problem. Works without the Editor.",
  {
    properties: {
      action: { type: "string", enum: [...PLAYTEST_MEMORY_ACTIONS], description: "show (default) | note | map | set | add." },
      section: {
        type: "string", minLength: 1, maxLength: 60,
        description: "[show] Only this section of NOTES.md. [note] The heading to append under (e.g. Boot, Dangerous controls, Known issues, Flaky spots; created if missing).",
      },
      text: { type: "string", minLength: 1, maxLength: 2000, description: "[note] What was learned, one bullet (a date is added). Facts a later session can act on." },
      session: { type: "string", pattern: SESSION_ID_SCHEMA_PATTERN, description: "[map] The session whose crawl to map (default your open session, else the project's latest ended one)." },
      crawl: { type: "integer", minimum: 1, description: "[map] Which crawl of the session (1-based; default the last)." },
      profile: {
        type: "object",
        description: "[set] A JSON merge patch for profile.json (sections explore, devices, detectors, back_labels, ignore_errors, known; null removes). [add] Lists to append to, e.g. {\"explore\":{\"deny\":[\"Reset progress\"]},\"known\":[{\"kind\":\"small_touch_target\",\"path\":\"Settings/Close\",\"reason\":\"…\"}]}.",
      },
    },
  },
);
