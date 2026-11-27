// Glue between the router and the scenario replay (unity_open_mcp_playtest_replay):
//   - exportAction / listAction / showAction: the synchronous actions (files only);
//   - prepareReplay + replayOperation: the scenario and options checked before the
//     job starts, then the `jobs` operation that runs one
//     scenario for one owner, closes it with a `replay` block and a mark in the
//     session, and writes the baseline back on a `record` run.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join } from "node:path";
import { randomBytes } from "node:crypto";
import type { JobOperation } from "../jobs/job-manager.js";
import { DEFAULT_MIN_TOUCH_DP, DEFAULT_PLACEHOLDER_PATTERN } from "./detectors.js";
import { NO_MEMORY, type MemoryOptions } from "./profile.js";
import { exploreRecorder } from "./explore-job.js";
import { type ToolCaller } from "./explore.js";
import { exportScenario } from "./export.js";
import type { PlaytestRecorder } from "./recorder.js";
import { ConsoleErrorFeed, REPLAY_DEFAULTS, REPLAY_LIMITS, ReplayError, Replayer, defaultLabel, type ReplayOptions, type ReplayVisualDeps } from "./replay.js";
import { decodePng, encodePng } from "./png.js";
import { VisualBaselines, visualDirFor, visualKeys, writeVisualBaseline } from "./visual-store.js";
import {
  ScenarioError, VISUAL_SETTING_DEFAULTS, displayPath, listScenarios, readScenarioFile, resolveScenarioPath, scenarioDir, unbaselinedSteps, usedVars,
  writeScenarioFile, type Scenario, type VisualSettings,
} from "./scenario.js";
import type { SessionOwner } from "./session-store.js";
import { PLAYTEST_REPLAY_TOOL } from "../tools/playtest-replay.js";

const num = (value: unknown, fallback: number) => (typeof value === "number" && Number.isFinite(value) ? value : fallback);

// ---------------------------------------------------------------------------
// export / list / show
// ---------------------------------------------------------------------------

export function exportAction(recorder: PlaytestRecorder, owner: SessionOwner, projectPath: string, args: Record<string, unknown>): Record<string, unknown> {
  if (args.source !== "crawl" && args.source !== "calls")
    throw new ScenarioError("invalid_arguments", "export needs source: \"crawl\" (a playtest_explore run, with its screens as the baseline) or \"calls\" (your own recorded steps).");
  if (typeof args.scenario !== "string") throw new ScenarioError("invalid_arguments", "export needs scenario: the name (or project-relative .json path) to write.");
  if (args.source === "calls" && (args.crawl !== undefined || args.to_screen !== undefined))
    throw new ScenarioError("invalid_arguments", "crawl / to_screen are used only with source: \"crawl\".");
  const file = resolveScenarioPath(projectPath, args.scenario, true);
  if (existsSync(file) && args.overwrite !== true)
    throw new ScenarioError("scenario_exists", `${displayPath(projectPath, file)} already exists; pass overwrite:true to replace it (or another scenario name).`, { file });
  const record = recorder.sessionRecord(owner, typeof args.session === "string" ? args.session : undefined);
  const name = basename(file).replace(/\.json$/i, "");
  const { scenario, warnings, stats } = exportScenario(record.entries, record.meta as unknown as Record<string, unknown>, {
    source: args.source, name, session: record.id,
    ...(typeof args.description === "string" && args.description ? { description: args.description } : {}),
    ...(typeof args.crawl === "number" ? { crawl: args.crawl } : {}),
    ...(typeof args.to_screen === "string" ? { toScreen: args.to_screen } : {}),
  });
  writeScenarioFile(file, scenario);
  const unbaselined = unbaselinedSteps(scenario);
  const vars = Object.keys(scenario.vars);
  return {
    status: "ok", action: "export", scenario: name, file, path: displayPath(projectPath, file), session: record.id,
    ...(record.open ? { session_open: true } : {}),
    source: args.source, steps: scenario.steps.length, start: { restart: scenario.start.restart, ...(scenario.start.command ? { command: scenario.start.command } : {}), ...(scenario.start.wait ? { wait: scenario.start.wait } : {}) },
    unbaselined, ...(vars.length ? { vars } : {}), stats,
    ...(warnings.length ? { warnings } : {}),
    preview: scenario.steps.slice(0, 15).map((s, i) => `${i + 1}. ${s.label ?? defaultLabel(s)}`),
    agentNextSteps: [
      unbaselined > 0
        ? `Store the baseline: playtest_replay {"scenario":"${name}","record":true} (in a playtest session with an isolated save), then review the file's diff.`
        : `Replay it after a change: playtest_replay {"scenario":"${name}"} in a playtest session with an isolated save.`,
      `The file is in the project (${displayPath(projectPath, file)}): read and edit it like code (steps, expect.texts, settings.ignore_errors); commit it so the team can run it.`,
    ],
  };
}

export function listAction(projectPath: string): Record<string, unknown> {
  const scenarios = listScenarios(projectPath);
  return {
    status: "ok", action: "list", folder: scenarioDir(projectPath), total: scenarios.length,
    scenarios: scenarios.map(s => {
      const keys = scenarioVisualKeys(s.file);
      return { ...s, file: displayPath(projectPath, s.file), ...(keys.length ? { visual_keys: keys } : {}) };
    }),
    ...(scenarios.length ? {} : { agentNextSteps: ["No scenarios yet: export one from a playtest session (playtest_replay {\"action\":\"export\",\"source\":\"crawl\"|\"calls\",\"scenario\":\"<name>\"})."] }),
  };
}

export function showAction(projectPath: string, args: Record<string, unknown>): Record<string, unknown> {
  if (typeof args.scenario !== "string") throw new ScenarioError("invalid_arguments", "show needs scenario.");
  const file = resolveScenarioPath(projectPath, args.scenario);
  const { scenario, problems } = readScenarioFile(file);
  return {
    status: "ok", action: "show", scenario: scenario.name, file: displayPath(projectPath, file),
    ...(scenario.description ? { description: scenario.description } : {}),
    ...(scenario.source ? { source: scenario.source } : {}),
    start: { restart: scenario.start.restart, ...(scenario.start.command ? { command: scenario.start.command } : {}), baseline: !!scenario.start.screen },
    settings: scenario.settings,
    ...(scenario.settings.visual || scenarioVisualKeys(file).length ? { visual_keys: scenarioVisualKeys(file) } : {}),
    vars: scenario.vars,
    steps: scenario.steps.map((s, i) => ({
      n: i + 1, do: s.do, label: s.label ?? defaultLabel(s),
      ...(s.target ? { target: s.target } : {}),
      baseline: !!s.expect?.screen, ...(s.expect?.texts ? { texts: s.expect.texts } : {}),
      ...(s.expect?.findings?.length ? { findings: s.expect.findings.length } : {}),
      ...(s.checkpoint ? { checkpoint: s.checkpoint } : {}),
    })),
    unbaselined: unbaselinedSteps(scenario),
    ...(problems.length ? { problems } : {}),
  };
}

// ---------------------------------------------------------------------------
// run
// ---------------------------------------------------------------------------

export interface PreparedReplay {
  file: string;
  scenario: Scenario;
  options: ReplayOptions;
}

/** Reads the scenario and the call's options; refuses before anything is tapped (invalid scenario, missing vars). */
export function prepareReplay(projectPath: string, args: Record<string, unknown>, memory: MemoryOptions = NO_MEMORY): PreparedReplay {
  if (typeof args.scenario !== "string") throw new ScenarioError("invalid_arguments", "run needs scenario (a name in Playtests/Scenarios or a .json path); list them with {\"action\":\"list\"}.");
  const file = resolveScenarioPath(projectPath, args.scenario);
  const { scenario, problems } = readScenarioFile(file);
  if (problems.length) throw new ScenarioError("scenario_invalid", `${displayPath(projectPath, file)}: ${problems.slice(0, 8).join("; ")}`, { problems });
  const vars: Record<string, string> = {};
  if (args.vars && typeof args.vars === "object")
    for (const [k, v] of Object.entries(args.vars as Record<string, unknown>)) if (typeof v === "string") vars[k] = v;
  const missing = usedVars(scenario).filter(name => !(name in vars));
  if (missing.length)
    throw new ScenarioError("missing_vars", `The scenario needs vars: ${missing.join(", ")} (${missing.map(n => scenario.vars[n]?.description ?? "").filter(Boolean).join("; ") || "declared in its vars"}).`, { missing });
  const patterns = [...scenario.settings.ignore_errors, ...(Array.isArray(args.ignore_errors) ? args.ignore_errors.filter((p): p is string => typeof p === "string") : [])];
  const ignoreErrors: RegExp[] = [];
  for (const pattern of patterns) {
    try { ignoreErrors.push(new RegExp(pattern)); }
    catch (error) { throw new ScenarioError("invalid_arguments", `ignore_errors '${pattern}' is not a valid regular expression: ${error instanceof Error ? error.message : String(error)}`); }
  }
  // The project profile's console noise counts for every scenario.
  ignoreErrors.push(...memory.ignoreErrors);
  if (args.record === true && args.strict === true) throw new ScenarioError("invalid_arguments", "record and strict exclude each other: a record run accepts the changes it sees.");
  // visual: the scenario's settings.visual, turned on (with defaults when it has none) or off by the call.
  const visual: VisualSettings | null = args.visual === false ? null
    : args.visual === true ? scenario.settings.visual ?? { ...VISUAL_SETTING_DEFAULTS, mask: [] }
    : scenario.settings.visual ?? null;
  const minTouch = num(args.min_touch_dp, DEFAULT_MIN_TOUCH_DP);
  const options: ReplayOptions = {
    from: args.from === "current" ? "current" : "start",
    retries: Math.min(Math.max(num(args.retries, REPLAY_DEFAULTS.retries), 0), REPLAY_LIMITS.retries),
    record: args.record === true,
    strict: args.strict === true,
    capture: args.capture === "steps" || args.capture === "none" ? args.capture : REPLAY_DEFAULTS.capture,
    vars,
    ignoreErrors,
    placeholderPattern: typeof args.placeholder_pattern === "string" ? (args.placeholder_pattern || null) : DEFAULT_PLACEHOLDER_PATTERN,
    minTouchDp: minTouch > 0 ? minTouch : null,
    settleMs: REPLAY_DEFAULTS.settle_ms,
    maxSettleMs: REPLAY_DEFAULTS.max_settle_ms,
    stepTimeoutMs: num(args.step_timeout_ms, scenario.settings.step_timeout_ms),
    sameScreen: num(args.same_screen, scenario.settings.same_screen),
    maxMinutes: Math.min(num(args.max_minutes, REPLAY_DEFAULTS.max_minutes), REPLAY_LIMITS.max_minutes),
    visual,
    known: memory.known,
  };
  return { file, scenario, options };
}

export interface ReplayJobInput extends PreparedReplay {
  projectPath: string;
  call: ToolCaller;
  recorder: PlaytestRecorder;
  owner: SessionOwner;
  session: string;
  /** Subscribe to the bridge event stream (console errors between calls); returns the unsubscribe. */
  subscribe?: (feed: ConsoleErrorFeed) => () => void;
  /** What the project profile supplied (detectors, console noise, accepted findings). */
  profile?: { file: string; applied: string[] };
}

const MARK_VERDICT: Record<string, "ok" | "bug" | "suspicious"> = {
  passed: "ok", recorded: "ok", flaky: "suspicious", failed: "bug", cancelled: "suspicious", incomplete: "suspicious",
};

/** One scenario run as a `jobs` operation (registered per call: it closes over the owner's route). */
export function replayOperation(input: ReplayJobInput): JobOperation {
  return {
    mutating: false,
    cancellable: true,
    validate() { /* prepared before the job starts */ },
    async run(_args, context) {
      const feed = new ConsoleErrorFeed();
      const unsubscribe = input.subscribe?.(feed);
      const path = displayPath(input.projectPath, input.file);
      const visualDir = visualDirFor(input.file);
      let outcome: Awaited<ReturnType<Replayer["run"]>>;
      try {
        outcome = await new Replayer(input.scenario, input.options, {
          call: input.call,
          recorder: exploreRecorder(input.recorder, input.owner, "replay", PLAYTEST_REPLAY_TOOL),
          signal: context.signal,
          console: feed,
          progress: (phase, fraction, detail) => context.report({ phase, fraction, detail }),
          ...(input.options.visual ? { visual: visualDeps(visualDir) } : {}),
        }).run();
      } catch (error) {
        if (error instanceof ReplayError) return { state: "failed", error: { code: error.code, message: error.message } };
        return { state: "failed", error: { code: "replay_failed", message: error instanceof Error ? error.message : String(error) } };
      } finally {
        unsubscribe?.();
      }
      const { result, recorded, frames } = outcome;
      if (input.profile) result.profile = input.profile;
      const verdict = String(result.verdict);
      let written: Record<string, unknown> | null = null;
      if (recorded) {
        try {
          writeScenarioFile(input.file, recorded);
          written = { file: path, steps: recorded.steps.length, changed_steps: result.changed_steps ?? [] };
          if (input.options.visual && frames.length) {
            const key = frames[0].key;
            const stored = writeVisualBaseline(visualDir, key, frames.filter(f => f.key === key), { max_edge: input.options.visual.max_edge, captured_at: frames[0].capturedAt });
            written.visual = { key, folder: displayPath(input.projectPath, stored.folder), frames: stored.frames };
            if (frames.some(f => f.key !== key)) written.visual_note = "The view changed during the run (another device or size); only the frames of the first were kept.";
          }
        } catch (error) {
          written = { file: path, error: error instanceof Error ? error.message : String(error) };
        }
      }
      const scenarioInfo = { name: input.scenario.name, file: path, steps: input.scenario.steps.length };
      const noted = input.recorder.note(input.owner, { k: "replay", ...result, scenario: scenarioInfo, record: input.options.record, ...(written ? { baseline: written } : {}) });
      if (noted) {
        const failure = result.failure as Record<string, unknown> | undefined;
        try {
          input.recorder.mark(input.owner, {
            label: `Replay ${input.scenario.name}: ${verdict}`,
            verdict: MARK_VERDICT[verdict] ?? "suspicious",
            note: failure
              ? `Step ${failure.n} (${failure.label}) failed: ${failure.reason} — ${failure.message}`
              : verdict === "flaky" ? "Passed on a retry after a failure — see the Replays section." : `${scenarioInfo.steps} step(s).`,
          });
        } catch { /* mark limit */ }
      }
      const unbaselined = Number(result.unbaselined ?? 0);
      const next: string[] = [];
      if (verdict === "failed") next.push("Open the failing step's frame (failure.frame) and its diff / candidates: a real regression, or an intended change? Intended → run again with record:true to accept it as the new baseline.");
      if (verdict === "flaky") next.push("The run failed and then passed (earlier_failures): report the step as flaky — a timing or state dependency — rather than as broken.");
      if (verdict === "recorded") next.push(`The baseline was written to ${path}: review its diff in version control and commit it.`);
      if (verdict === "failed" && input.options.record) next.push("A record run writes the baseline only when every step executed: fix the failing step (or the scenario) and record again.");
      if (unbaselined > 0) next.push(`${unbaselined} step(s) have no baseline yet (they pass on execution alone): run once with record:true.`);
      const visual = result.visual as Record<string, number | string> | undefined;
      if (visual && Number(visual.unbaselined) > 0 && !input.options.record)
        next.push(`${visual.unbaselined} step(s) have no visual baseline for ${visual.key ?? "this device"} (baselines are per platform and device / Game view size): run once with record:true on it.`);
      if (visual && Number(visual.failed) > 0)
        next.push("A step looks different from its visual baseline: compare failure.visual's baseline, current frame and diff (red = changed pixels; regions name the controls there). Mask dynamic areas in settings.visual.mask; an intended change → record:true.");
      if (Array.isArray(result.drift) && result.drift.length) next.push("Drift (drift[]) passed but shows what changed: renamed labels, moved controls, new suspicious or fixed findings.");
      next.push(noted ? "End the session with playtest_session {\"action\":\"end\"}: the report has a Replays section." : "The playtest session ended during the run; this result is the record.");
      const final = { ...result, scenario: scenarioInfo, ...(written ? { baseline: written } : {}), session: input.session, agentNextSteps: next };
      if (context.signal.aborted && verdict === "cancelled") return { state: "cancelled", result: final };
      return { state: "succeeded", result: final };
    },
  };
}

/** Baselines from <scenario>.visual/<key>/, frames decoded from the capture files, diffs written to a temp folder. */
function visualDeps(dir: string): ReplayVisualDeps {
  const stores = new Map<string, VisualBaselines>();
  const diffDir = join(tmpdir(), "unity-open-mcp-replay", `${Date.now()}-${randomBytes(3).toString("hex")}`);
  return {
    baseline(key, n, label) {
      if (!stores.has(key)) stores.set(key, new VisualBaselines(dir, key));
      return stores.get(key)!.frame(n, label);
    },
    read(file) {
      try { return decodePng(readFileSync(file)); } catch { return null; }
    },
    writeDiff(image, name) {
      try {
        mkdirSync(diffDir, { recursive: true });
        const file = join(diffDir, `${name}.png`);
        writeFileSync(file, encodePng(image));
        return file;
      } catch { return null; }
    },
  };
}

/** The visual baseline keys a scenario has recorded. */
export function scenarioVisualKeys(file: string): string[] {
  return visualKeys(visualDirFor(file));
}
