// Playtest replay — export a session (or one of its UI crawls) as a scenario.
//
// Reads a session's log (the deliberate on-disk record; see recorder.ts) and
// turns the steps that drove the game into replayable steps (scenario.ts):
//
//   source "calls": the agent's own recorded calls, in order — taps by the
//     resolved path the bridge reported plus the label the agent used, typing
//     into the field the bridge reported, waits, project commands, explicit
//     screen checks, other input as sent. Failed calls, captures and Game view
//     changes are skipped; entering play mode or a boot-like project command
//     before the first action becomes the scenario's start; the project's
//     isolation hooks are left out (the replaying session isolates itself).
//     No expectations: a `record` run sets the baseline.
//   source "crawl": one UI crawl's taps, typing and restarts in order, with
//     the crawler's screens as the baseline (signature and findings per screen);
//     with `to_screen`, only the shortest path of exploring taps from the start
//     screen to that screen (plus the typing done on each screen before it).
//
// Pure: entries in, scenario + warnings out; the router reads and writes files.
import type { Finding } from "./detectors.js";
import { baselineKey } from "./replay.js";
import {
  REPLAY_INPUT_TOOLS, SCENARIO_DEFAULTS, SCENARIO_FORMAT, SCENARIO_LIMITS, ScenarioError,
  type Scenario, type ScenarioStart, type ScenarioStep, type ScreenExpectation,
} from "./scenario.js";

export interface ExportOptions {
  source: "calls" | "crawl";
  name: string;
  description?: string;
  session: string;
  /** source crawl: which crawl of the session (1-based; default the last). */
  crawl?: number;
  /** source crawl: only the path to this screen (S5). */
  toScreen?: string;
  now?: () => number;
}

export interface ExportResult {
  scenario: Scenario;
  warnings: string[];
  stats: Record<string, number>;
}

type Entry = Record<string, any>;

const POINTER = "unity_open_mcp_inputsim_pointer";
const TEXT = "unity_open_mcp_inputsim_text";
const WAIT = "unity_open_mcp_playtest_wait";
const COMMANDS = "unity_open_mcp_project_commands";
const JOBS = "unity_open_mcp_jobs";
const REDACTED_TEXT = /^\[redacted (\d+) chars\]$/;
const HOOK = /(^|\.)playtest\.(begin|end)$/;
const BOOT = /(^|\.)(playtest\.)?boot$/;
const SNAPSHOT_KINDS = new Set(["text_overflow", "outside_safe_area", "overlapping_controls", "placeholder_text", "small_touch_target", "zero_size_control"]);

const isObject = (value: unknown): value is Record<string, any> => !!value && typeof value === "object" && !Array.isArray(value);

export function exportScenario(entries: Entry[], meta: Entry, options: ExportOptions): ExportResult {
  return options.source === "crawl" ? exportCrawl(entries, meta, options) : exportCalls(entries, meta, options);
}

function baseScenario(options: ExportOptions, kind: string, start: ScenarioStart, extra: Record<string, unknown>): Scenario {
  return {
    format: SCENARIO_FORMAT,
    name: options.name,
    ...(options.description ? { description: options.description } : {}),
    source: { session: options.session, kind, exported_at: new Date((options.now ?? Date.now)()).toISOString(), ...extra },
    start,
    settings: { same_screen: SCENARIO_DEFAULTS.same_screen, step_timeout_ms: SCENARIO_DEFAULTS.step_timeout_ms, routing: SCENARIO_DEFAULTS.routing, ignore_errors: [] },
    vars: {},
    steps: [],
  };
}

function viewOf(meta: Entry): Record<string, unknown> | undefined {
  const view = meta?.device?.view;
  if (!isObject(view)) return undefined;
  return { mode: view.mode, ...(view.device ? { device: view.device } : {}), ...(view.size ? { size: view.size } : {}), ...(Array.isArray(view.screen) ? { screen: view.screen } : {}), ...(view.orientation ? { orientation: view.orientation } : {}) };
}

/** Any `object_id` left in a step's arguments (instance ids do not survive a new play session). */
function hasObjectId(value: unknown): boolean {
  if (Array.isArray(value)) return value.some(hasObjectId);
  if (!isObject(value)) return false;
  return Object.entries(value).some(([k, v]) => k === "object_id" || k === "from_object_id" || k === "to_object_id" || hasObjectId(v));
}

// ---------------------------------------------------------------------------
// The agent's calls
// ---------------------------------------------------------------------------

function exportCalls(entries: Entry[], meta: Entry, options: ExportOptions): ExportResult {
  const warnings: string[] = [];
  const stats = { calls: 0, steps: 0, skipped_failed: 0, skipped_captures: 0, skipped_server_steps: 0, skipped_other: 0, object_ids: 0 };
  const results = new Map<number, Entry>();
  const jobResults = new Map<number, Entry>();
  for (const e of entries) if (e.k === "call") (typeof e.of === "number" ? jobResults : results).set(e.seq, e);
  let start: ScenarioStart | null = null;
  let playEntered = false;
  let device: Record<string, unknown> | undefined = viewOf(meta);
  const steps: ScenarioStep[] = [];
  const vars: Scenario["vars"] = {};
  let secretCount = 0;
  let stepsSinceMark = 0;

  for (const e of entries) {
    if (e.k === "mark") {
      if (stepsSinceMark > 0 && steps.length) steps[steps.length - 1].checkpoint = String(e.label ?? "");
      stepsSinceMark = 0;
      continue;
    }
    if (e.k !== "call_started") continue;
    const result = results.get(e.seq);
    const kind = String(e.kind);
    // The crawler's and the replay's own steps are not the agent's: export a crawl with source "crawl".
    if (kind === "explore" || kind === "replay") { stats.skipped_server_steps++; continue; }
    stats.calls++;
    if (!result) { warnings.push(`call #${e.seq} ${e.tool} never finished (lost in flight): left out`); continue; }
    if (kind === "capture") { stats.skipped_captures++; continue; }
    const failedJob = result.status === "job_started" && jobResults.get(e.seq)?.status === "error";
    if (result.status === "error" || failedJob) { stats.skipped_failed++; continue; }
    if (isObject(e.args) && e.args.truncated === true) { warnings.push(`call #${e.seq} ${e.tool}: its arguments were too long to record; left out`); stats.skipped_other++; continue; }
    const label = String(e.tool ?? "");
    let tool = label.split(" ")[0];
    let args: Record<string, any> = isObject(e.args) ? e.args : {};
    let commandId: string | undefined;
    if (tool === JOBS) {
      tool = String(args.tool_or_command ?? label.split(" ").pop() ?? "");
      args = isObject(args.args) ? args.args : {};
      if (result.status === "job_started" && !jobResults.has(e.seq)) warnings.push(`call #${e.seq} ${tool}: its job result was never observed; exported anyway`);
    }
    if (tool === COMMANDS) { commandId = String(args.command_id ?? ""); args = isObject(args.args) ? args.args : {}; }
    else if (tool.startsWith("project.")) commandId = tool;
    const summary = isObject(result.summary) ? result.summary : {};
    const before = steps.length;
    const acted = () => steps.some(s => s.do !== "check");

    if (kind === "control") {
      if (tool === "unity_open_mcp_editor_set_state" && args.state === "play" && !acted()) playEntered = true;
      else if (tool === "unity_open_mcp_game_view" && (args.action === "set_device" || args.action === "set_resolution"))
        device = { ...(device ?? {}), ...(args.device ? { device: args.device, mode: "simulator" } : {}), ...(args.size_label ? { size: args.size_label, mode: "game" } : {}), ...(args.orientation ? { orientation: args.orientation } : {}) };
      continue;
    }
    if (kind === "check") {
      steps.push({ do: "check", label: typeof args.label === "string" ? args.label : "Check the screen" });
    } else if (commandId !== undefined) {
      if (!commandId) { stats.skipped_other++; continue; }
      if (HOOK.test(commandId)) continue;
      if (BOOT.test(commandId) && !acted() && !start) {
        start = { restart: "command", command: commandId, ...(Object.keys(args).length ? { args } : {}) };
        continue;
      }
      if (label.startsWith(JOBS)) warnings.push(`step ${steps.length + 1}: ${commandId} ran as a job; a replay runs commands synchronously and fails on an async one`);
      steps.push({ do: "command", command: commandId, ...(Object.keys(args).length ? { args } : {}) });
    } else if (tool === WAIT) {
      const waitArgs = { ...args };
      if (typeof waitArgs.timeout_ms === "number" && waitArgs.timeout_ms > SCENARIO_LIMITS.max_wait_ms) {
        warnings.push(`step ${steps.length + 1}: the wait's timeout_ms ${waitArgs.timeout_ms} was cut to ${SCENARIO_LIMITS.max_wait_ms} (a replay wait runs inline)`);
        waitArgs.timeout_ms = SCENARIO_LIMITS.max_wait_ms;
      }
      if (!isObject(waitArgs.condition)) { stats.skipped_other++; continue; }
      steps.push({ do: "wait", args: waitArgs });
    } else if (tool === POINTER && args.action === "click") {
      const path = typeof summary.target === "string" && summary.target ? summary.target : undefined;
      const exact = args.text_match === undefined || args.text_match === "exact";
      const text = typeof args.target_text === "string" && exact ? args.target_text : undefined;
      if (!path && !text) {
        warnings.push(`call #${e.seq}: a click whose target is known only by instance id (no resolved path was recorded): left out`);
        stats.object_ids++;
        continue;
      }
      steps.push({
        do: "tap", target: { ...(path ? { path } : {}), ...(text ? { text } : {}), ...(text && typeof args.index === "number" ? { index: args.index } : {}) },
        ...(args.routing === "raycast_real" ? { routing: "raycast_real" as const } : {}),
      });
    } else if (tool === TEXT) {
      const field = isObject(summary.field) ? summary.field : {};
      const path = typeof field.path === "string" && field.path ? field.path
        : typeof args.target === "string" && args.target.includes("/") ? args.target : undefined;
      if (!path) {
        warnings.push(`call #${e.seq}: typing into a field whose path was not recorded: left out`);
        stats.object_ids++;
        continue;
      }
      const mode = (args.mode ?? "replace") as ScenarioStep["mode"];
      const step: ScenarioStep = { do: "type", target: { path, role: "input" }, mode, end: (args.end ?? "end_edit") as ScenarioStep["end"] };
      if (mode !== "clear") {
        let text = typeof args.text === "string" ? args.text : "";
        const redacted = REDACTED_TEXT.exec(text);
        if (redacted) {
          const name = `secret_${++secretCount}`;
          vars[name] = { secret: true, description: `typed into ${path} (${redacted[1]} characters; redacted in the session)` };
          text = `\${${name}}`;
          warnings.push(`step ${steps.length + 1}: the typed text was a secret; pass it as vars.${name} when you run the scenario`);
        }
        step.text = text;
      }
      if (args.secret === true) step.secret = true;
      steps.push(step);
    } else if (REPLAY_INPUT_TOOLS.has(tool)) {
      const inputArgs = { ...args };
      if (typeof inputArgs.object_id === "string" && typeof summary.target === "string" && summary.target) {
        delete inputArgs.object_id;
        inputArgs.target = summary.target;
      }
      if (hasObjectId(inputArgs)) {
        stats.object_ids++;
        warnings.push(`step ${steps.length + 1} (${tool}): its arguments name an object by instance id, which changes every play session — edit the step to use target / target_text`);
      }
      steps.push({ do: "input", tool, args: inputArgs });
    } else {
      stats.skipped_other++;
      continue;
    }
    if (steps.length > before) stepsSinceMark++;
  }

  if (!start) {
    start = { restart: "play_mode" };
    if (!playEntered) warnings.push("The session did not record how the game was started (no play-mode entry, no boot command): the scenario restarts play mode — set start.command (restart: command) if the game needs a boot command to reach its first screen.");
  }
  if (!steps.length) throw new ScenarioError("nothing_to_export", "The session has no recorded input, wait, command or check calls to replay.", { stats });
  if (steps.length > SCENARIO_LIMITS.max_steps) {
    warnings.push(`Only the first ${SCENARIO_LIMITS.max_steps} of ${steps.length} steps were exported.`);
    steps.length = SCENARIO_LIMITS.max_steps;
  }
  const scenario = baseScenario(options, "calls", start, device ? { device } : {});
  scenario.vars = vars;
  scenario.steps = steps;
  stats.steps = steps.length;
  warnings.push("The steps have no baseline yet: run the scenario once with record:true to store each screen.");
  return { scenario, warnings, stats };
}

// ---------------------------------------------------------------------------
// A UI crawl
// ---------------------------------------------------------------------------

interface CrawlStep {
  seq: number;
  purpose: string;
  label: string;
  from?: string;
  to?: string;
  path?: string;
  target?: string;
  text?: string;
  outcome?: string;
}

function exportCrawl(entries: Entry[], meta: Entry, options: ExportOptions): ExportResult {
  const warnings: string[] = [];
  const notes = entries.map((e, i) => ({ e, i })).filter(x => x.e.k === "explore");
  if (!notes.length) throw new ScenarioError("nothing_to_export", "The session has no finished UI crawl (playtest_explore) to export; use source: \"calls\" for your own steps.");
  const which = options.crawl ?? notes.length;
  if (which < 1 || which > notes.length) throw new ScenarioError("invalid_arguments", `crawl ${which} does not exist; the session has ${notes.length} crawl(s).`);
  const { e: note, i: end } = notes[which - 1];
  const begin = which > 1 ? notes[which - 2].i : -1;
  const results = new Map<number, Entry>();
  for (const e of entries) if (e.k === "call" && typeof e.of !== "number") results.set(e.seq, e);
  const crawl: CrawlStep[] = [];
  for (let i = begin + 1; i < end; i++) {
    const e = entries[i];
    if (e.k !== "call_started" || e.kind !== "explore") continue;
    const result = results.get(e.seq);
    if (!result || result.status !== "ok") continue;
    const a = isObject(e.args) ? e.args : {};
    const s = isObject(result.summary) ? result.summary : {};
    crawl.push({
      seq: e.seq, purpose: String(a.purpose ?? s.purpose ?? ""), label: String(a.step ?? ""),
      from: a.screen ?? s.screen, to: s.to ?? (s.purpose === "observe" ? s.screen : undefined),
      path: a.path, target: a.target, text: typeof a.text === "string" ? a.text : undefined, outcome: s.outcome,
    });
  }
  const signatures = isObject(note.signatures) ? note.signatures as Record<string, string[]> : null;
  if (!signatures) warnings.push("This crawl carries no screen signatures (an older server recorded it): run the scenario once with record:true to store the baseline.");
  const titles = new Map<string, string>((Array.isArray(note.screens) ? note.screens : []).map((s: Entry) => [String(s.id), String(s.title ?? "")]));
  const findingsOf = new Map<string, Set<string>>();
  for (const f of Array.isArray(note.findings) ? note.findings as Finding[] : []) {
    if (!f.screen || !f.path || !SNAPSHOT_KINDS.has(f.kind)) continue;
    if (!findingsOf.has(f.screen)) findingsOf.set(f.screen, new Set());
    findingsOf.get(f.screen)!.add(baselineKey(f));
  }
  const screen = (id: string | undefined): ScreenExpectation | undefined =>
    id && signatures?.[id] ? { title: titles.get(id) ?? id, signature: [...signatures[id]].sort() } : undefined;
  const expectFor = (id: string | undefined) => {
    const s = screen(id);
    return s ? { screen: s, findings: [...(findingsOf.get(id!) ?? [])].sort() } : undefined;
  };

  const opts = isObject(note.options) ? note.options : {};
  const restart = (opts.restart === "command" || opts.restart === "none" ? opts.restart : "play_mode") as ScenarioStart["restart"];
  const observe = crawl.find(c => c.purpose === "observe");
  const startId = observe?.to ?? observe?.from ?? "S1";
  const start: ScenarioStart = {
    restart,
    ...(restart === "command" && typeof opts.restart_command === "string" ? { command: opts.restart_command } : {}),
    ...(restart === "command" && isObject(opts.restart_args) ? { args: opts.restart_args } : {}),
  };
  const startExpect = expectFor(startId);
  if (startExpect) { start.screen = startExpect.screen; start.findings = startExpect.findings; }
  if (restart === "none") warnings.push("The crawl ran with restart none: the scenario starts from whatever screen is shown and cannot retry.");

  let chosen: CrawlStep[];
  if (options.toScreen) {
    chosen = pathTo(crawl, startId, options.toScreen, titles);
  } else {
    chosen = crawl.filter(c => c.purpose !== "observe");
  }
  const steps: ScenarioStep[] = [];
  for (const c of chosen) {
    if (c.purpose === "restart") {
      if (restart === "none") continue;
      steps.push({ do: "restart", label: c.label || "Restart the game", ...(expectFor(c.to) ? { expect: expectFor(c.to) } : {}) });
      continue;
    }
    if (!c.path) continue;
    const typing = c.text !== undefined;
    // The crawler names a control without a label by its object name (the path's last part).
    const named = c.target && c.target !== c.path.split("/").pop() ? c.target : undefined;
    const step: ScenarioStep = typing
      ? { do: "type", label: c.label, target: { path: c.path, role: "input" }, text: c.text, mode: "replace", end: "end_edit" }
      : { do: "tap", label: c.label, target: { path: c.path, ...(named ? { text: named } : {}) } };
    const expect = expectFor(c.to);
    if (expect) step.expect = expect;
    steps.push(step);
  }
  if (!steps.length) throw new ScenarioError("nothing_to_export", "The crawl has no successful taps to replay.");
  const scenario = baseScenario(options, options.toScreen ? "crawl_path" : "crawl", start, {
    crawl: which, ...(options.toScreen ? { to_screen: options.toScreen } : {}),
    ...(note.device ? { device: { mode: "simulator", device: note.device, orientation: note.orientation } } : viewOf(meta) ? { device: viewOf(meta) } : {}),
  });
  if (typeof opts.same_screen === "number") scenario.settings.same_screen = opts.same_screen;
  if (opts.routing === "raycast_real") scenario.settings.routing = "raycast_real";
  if (steps.length > SCENARIO_LIMITS.max_steps) {
    warnings.push(`Only the first ${SCENARIO_LIMITS.max_steps} of ${steps.length} steps were exported.`);
    steps.length = SCENARIO_LIMITS.max_steps;
  }
  scenario.steps = steps;
  return { scenario, warnings, stats: { crawl_steps: crawl.length, steps: steps.length, screens: titles.size } };
}

/**
 * The shortest path of exploring taps from the start screen to `target`, each
 * hop preceded by the typing the crawl did on that screen before it (a form is
 * filled before it is sent).
 */
function pathTo(crawl: CrawlStep[], startId: string, target: string, titles: Map<string, string>): CrawlStep[] {
  if (!titles.has(target)) throw new ScenarioError("invalid_arguments", `The crawl has no screen ${target}; its screens: ${[...titles.keys()].join(", ")}.`);
  if (target === startId) throw new ScenarioError("invalid_arguments", `${target} is the start screen; there is no path to export.`);
  const hops = crawl.map((c, index) => ({ c, index })).filter(({ c }) => c.purpose === "explore" && c.text === undefined && c.from && c.to && c.from !== c.to);
  const queue: Array<{ id: string; path: Array<{ c: CrawlStep; index: number }> }> = [{ id: startId, path: [] }];
  const seen = new Set([startId]);
  let found: Array<{ c: CrawlStep; index: number }> | null = null;
  while (queue.length && !found) {
    const { id, path } = queue.shift()!;
    for (const hop of hops) {
      if (hop.c.from !== id || seen.has(hop.c.to!)) continue;
      const next = [...path, hop];
      if (hop.c.to === target) { found = next; break; }
      seen.add(hop.c.to!);
      queue.push({ id: hop.c.to!, path: next });
    }
  }
  if (!found) throw new ScenarioError("screen_not_reached", `The crawl never tapped its way from ${startId} to ${target} (it may have been reached only after a restart from another screen).`);
  const out: CrawlStep[] = [];
  for (const { c, index } of found) {
    const typed = new Map<string, CrawlStep>();
    for (let i = 0; i < index; i++) {
      const t = crawl[i];
      if (t.text !== undefined && t.from === c.from && t.path) typed.set(t.path, t);
    }
    out.push(...typed.values(), c);
  }
  return out;
}
