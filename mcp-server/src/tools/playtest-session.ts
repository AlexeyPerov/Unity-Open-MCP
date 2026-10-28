// Playtest — session recorder (server-side; group `playtest`).
//
// start → mark … → end. While a session is open, the owner's input / wait /
// capture / project-command calls (and play-mode / Game view changes) are
// recorded automatically into an append-only log in the platform app-data
// folder; `end` writes report.md + frames + thumbnails next to it. Local
// route: the MCP server owns the log (it sees every call); the bridge only
// writes the frames. See src/playtest/recorder.ts.
import { makeTool } from "./schema-fragments.js";

export const PLAYTEST_SESSION_ACTIONS = ["start", "mark", "status", "end", "resume", "list"] as const;
export const PLAYTEST_VERDICTS = ["ok", "bug", "suspicious"] as const;
const SNAPSHOT_NAME_PATTERN = "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$";
export const SESSION_ID_SCHEMA_PATTERN = "^[0-9]{8}-[0-9]{6}-[a-z0-9-]{1,40}$";

/** Which arguments each action accepts (anything else is invalid_arguments). */
export const PLAYTEST_SESSION_ACTION_KEYS: Record<(typeof PLAYTEST_SESSION_ACTIONS)[number], string[]> = {
  start: ["name", "device", "snapshot", "use_snapshot"],
  mark: ["label", "note", "verdict", "session"],
  status: [],
  end: ["note", "diff", "session"],
  resume: ["session"],
  list: ["limit"],
};

export const playtestSession = makeTool(
  "unity_open_mcp_playtest_session",
  "Record a playtest so a human can review it: start → mark each step → end writes a report. While a session is " +
    "open, YOUR input (inputsim_*), wait (playtest_wait), capture (screenshots, capture_inline, capture_series, " +
    "visual_compare), project-command (project_commands invoke, project-command / play-mode jobs) and control " +
    "(editor_set_state, game_view set_*) calls are recorded automatically: tool, arguments with secrets redacted, " +
    "status, duration, the frames they wrote, and console errors / exceptions logged during them or right after " +
    "(matched by time from the bridge event stream). Other agents' calls are never recorded into your session. " +
    "ACTIONS: start {name, device (free-text summary; the Game view / Simulator state is read too), snapshot:true " +
    "(take a player_state snapshot now and diff it at end) or use_snapshot:<existing name>} — one open session per " +
    "agent, a second start answers session_already_open; mark {label, verdict ok|bug|suspicious (default ok), note} " +
    "after each player-visible step — answers how many errors the step logged; status — your open session, plus " +
    "orphaned ones; end {note, diff (default true)} — writes report.md + full frames + thumbnails (steps with " +
    "verdicts, the error list with first occurrence and stack, the state diff) and returns its path and a compact " +
    "summary: hand the path to the human. The log is appended to disk per call in the app-data folder " +
    "(…/unity-open-mcp/playtest-sessions/<company>/<product>/<session>/), so it survives a domain reload and an MCP " +
    "server restart: after a restart call end (or resume) with session:<id> — the report names any call that was " +
    "in flight and lost (no_open_session lists orphaned sessions). list = this project's sessions. Bounded: 500 " +
    "calls, 200 marks, 300 frames (512 MB), 60 thumbnails per session.",
  {
    required: ["action"],
    properties: {
      action: {
        type: "string",
        enum: [...PLAYTEST_SESSION_ACTIONS],
        description:
          "start: open a session (recording begins). mark: close a step with a verdict. status: your open session. " +
          "end: write the report. resume: re-attach a session left open by a stopped MCP server process. list: this project's sessions.",
      },
      name: {
        type: "string",
        minLength: 1,
        maxLength: 80,
        description: "[start] What the playtest covers (default \"playtest\"); also part of the session folder name.",
      },
      device: {
        type: "string",
        maxLength: 200,
        description:
          "[start] Device / resolution summary for the report (e.g. \"iPhone 13 portrait via Device Simulator\"). The " +
          "current Game view / Simulator state is recorded as well.",
      },
      snapshot: {
        type: "boolean",
        description:
          "[start] Take a player_state snapshot now (named playtest-<session>) and put its diff against the state at " +
          "end into the report. Fails the start (snapshot_failed) rather than run unisolated. Prefer the project's own " +
          "playtest.begin / playtest.end hook when it has one.",
      },
      use_snapshot: {
        type: "string",
        pattern: SNAPSHOT_NAME_PATTERN,
        description: "[start] Diff against this existing player_state snapshot at end instead of taking a new one.",
      },
      label: {
        type: "string",
        minLength: 1,
        maxLength: 120,
        description: "[mark] The step that just finished, as a tester would name it (\"Tap Start Wave\").",
      },
      verdict: {
        type: "string",
        enum: [...PLAYTEST_VERDICTS],
        description: "[mark] ok (default) | bug (wrong behaviour seen) | suspicious (needs a human look).",
      },
      note: {
        type: "string",
        maxLength: 2000,
        description: "[mark / end] Free text: what was seen, expected vs actual. Secrets are redacted.",
      },
      session: {
        type: "string",
        pattern: SESSION_ID_SCHEMA_PATTERN,
        description:
          "[mark / end / resume] The session id start returned. Needed only after an MCP server restart, to adopt the " +
          "session the stopped process left open.",
      },
      diff: {
        type: "boolean",
        description: "[end] Diff the session's player-state snapshot against the current state into the report (default true).",
      },
      limit: {
        type: "integer",
        minimum: 1,
        maximum: 50,
        description: "[list] Newest sessions to list (default 10).",
      },
    },
  },
);
