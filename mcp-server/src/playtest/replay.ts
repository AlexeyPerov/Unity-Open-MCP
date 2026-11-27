// Playtest replay — the engine behind unity_open_mcp_playtest_replay (action run).
//
// Runs a scenario (scenario.ts) against the running game with no model in the
// loop: restart to the scenario's start, then per step find the target on
// screen by path and label (never by instance id), act through the same bridge
// tools an agent uses, and check the screen after it against the step's
// expectations — the screen signature the crawler uses (Jaccard ≥ same_screen),
// required texts, the detector findings of that screen — and the console
// errors logged meanwhile (bridge event stream by time, plus the responses'
// logs[]). A step that fails ends the attempt; a failed attempt is run again
// from the start (`retries`), so a failure that does not repeat is reported as
// flaky. Tolerated changes (a renamed label at the same path, a moved control
// with the same label) pass as drift unless `strict`. A `record` run writes what
// it saw as the new baseline instead of failing on it.
//
// Like the crawler, the engine talks to the bridge only through the ToolCaller
// it is given and records each step as one call of the owner's open session
// (kind `replay`) through the ExploreRecorder interface.
import { toolOutput } from "../jobs/adapters.js";
import type { BridgeEvent } from "../event-stream.js";
import {
  findingKey, findingsFromSnapshot, isFillable, maskPath, maskText, minTouchPx, screenSignature, screenTitle, similarity,
  type Finding, type SnapshotNode,
} from "./detectors.js";
import { collectLogs, restartGame, type ExploreRecorder, type RecordedStep, type RestartPlan, type ToolCaller } from "./explore.js";
import { fillVars, usesSecret, type Scenario, type ScenarioStart, type ScenarioStep, type ScenarioTarget, type ScreenExpectation, type StepKind, type VisualSettings } from "./scenario.js";
import type { RgbaImage } from "./png.js";
import type { KnownFindings } from "./profile.js";
import { compareImages, describeRegion, diffImage, masksFor, nodesInRegion, visualKey } from "./visual.js";

export interface ReplayOptions {
  from: "start" | "current";
  retries: number;
  record: boolean;
  strict: boolean;
  capture: "failures" | "steps" | "none";
  vars: Record<string, string>;
  /** Console messages (first line) that do not fail a step: the scenario's and the call's. */
  ignoreErrors: RegExp[];
  placeholderPattern: string | null;
  minTouchDp: number | null;
  settleMs: number;
  /** How long a step without an expected screen waits for the screen to stop changing. */
  maxSettleMs: number;
  stepTimeoutMs: number;
  sameScreen: number;
  maxMinutes: number;
  /** Compare every step as a picture with its visual baseline (null = off). */
  visual: VisualSettings | null;
  /** Findings the project profile accepts: never a new finding. */
  known?: KnownFindings | null;
}

/** How the scenario's start restarts the game: play mode and / or a command, then an optional wait. */
export function startPlan(start: ScenarioStart, vars: Record<string, string>): RestartPlan {
  return {
    mode: start.restart,
    ...(start.command ? { command: start.command } : {}),
    ...(start.args ? { args: fillVars(start.args, vars) } : {}),
    ...(start.wait ? { wait: fillVars(start.wait, vars) } : {}),
  };
}

export const REPLAY_DEFAULTS = { retries: 1, settle_ms: 250, max_settle_ms: 4_000, max_minutes: 20, capture: "failures" as const };
export const REPLAY_LIMITS = { retries: 3, max_minutes: 60 };

export type FailReason =
  | "target_not_found" | "target_not_tappable" | "action_failed" | "wait_timed_out" | "command_failed" | "restart_failed"
  | "screen_changed" | "text_missing" | "console_error" | "new_finding" | "drift" | "play_mode_exited" | "snapshot_failed"
  | "visual_changed";

export interface VisualOutcome {
  status: "passed" | "failed" | "unbaselined" | "recorded" | "skipped";
  key?: string;
  /** Share of compared pixels that changed (over the noise floor). */
  ratio?: number;
  threshold?: number;
  regions?: Array<{ where: string; near: string[] }>;
  baseline?: string;
  current?: string;
  diff?: string;
  note?: string;
}

export interface Drift { kind: "label_changed" | "path_changed" | "index_changed" | "screen_drift" | "labels_changed" | "new_suspicious" | "fixed_finding"; detail: string }

export interface StepOutcome {
  n: number;
  do: StepKind | "start";
  label: string;
  status: "passed" | "failed" | "skipped";
  reason?: FailReason;
  message?: string;
  drift: Drift[];
  similarity?: number;
  title?: string;
  expected_title?: string;
  diff?: { added: string[]; removed: string[] };
  errors?: string[];
  new_findings?: Array<Record<string, unknown>>;
  fixed_findings?: string[];
  /** The screen changed against the baseline (record runs). */
  changed?: boolean;
  settled?: boolean;
  frame?: string;
  frame_call?: number;
  duration_ms: number;
  candidates?: string[];
  visual?: VisualOutcome;
}

interface Observed { screen: ScreenExpectation; findings: string[]; target?: ScenarioTarget }

interface Attempt {
  attempt: number;
  passed: boolean;
  start: StepOutcome;
  steps: StepOutcome[];
  observedStart?: Observed;
  observed: Array<Observed | undefined>;
  /** Record runs: the frame kept for each step (n 0 = start). */
  frames: Array<{ n: number; label: string; source: string; key: string; capturedAt?: Record<string, unknown> }>;
  duration_ms: number;
}

export type ReplayStopReason = "completed" | "cancelled" | "max_time" | "session_ended";

export class ReplayError extends Error {
  constructor(readonly code: string, message: string, readonly detail: Record<string, unknown> = {}) { super(message); }
}

/** Console errors from the bridge event stream, buffered for the replay to take step by step. */
export class ConsoleErrorFeed {
  private items: Array<{ ts: number; type: string; message: string }> = [];
  constructor(private readonly max = 500) {}
  onEvent(event: BridgeEvent): void {
    if (event.type !== "log" || !ERROR_TYPES.has(String(event.logType ?? "").toLowerCase())) return;
    if (this.items.length >= this.max) return;
    const ts = Date.parse(event.ts);
    this.items.push({ ts: Number.isFinite(ts) ? ts : Date.now(), type: String(event.logType), message: String(event.message ?? "") });
  }
  /** Errors buffered so far (removed from the buffer). */
  take(): Array<{ ts: number; type: string; message: string }> {
    const out = this.items;
    this.items = [];
    return out;
  }
}

export interface ReplayDeps {
  call: ToolCaller;
  recorder: ExploreRecorder;
  signal: AbortSignal;
  /** Console errors logged between calls (the event stream); without it only the responses' logs[] count. */
  console?: ConsoleErrorFeed;
  now?: () => number;
  sleep?: (ms: number) => Promise<void>;
  progress?: (phase: string, fraction: number, detail: Record<string, unknown>) => void;
  /** Visual baselines (required when options.visual is set). */
  visual?: ReplayVisualDeps;
}

export interface ReplayVisualDeps {
  /** The baseline frame of step n (0 = start) for a key, or why there is none. */
  baseline(key: string, n: number, label: string): { file: string; image: RgbaImage } | { missing: string; detail?: string };
  /** Decode a captured frame. */
  read(file: string): RgbaImage | null;
  /** Write a diff image; its file, or null. */
  writeDiff(image: RgbaImage, name: string): string | null;
}

const SNAPSHOT_TOOL = "unity_open_mcp_ui_snapshot";
const POINTER_TOOL = "unity_open_mcp_inputsim_pointer";
const TEXT_TOOL = "unity_open_mcp_inputsim_text";
const WAIT_TOOL = "unity_open_mcp_playtest_wait";
const COMMANDS_TOOL = "unity_open_mcp_project_commands";
const SCREENSHOT_TOOL = "unity_senses_screenshot";
const ERROR_TYPES = new Set(["error", "exception", "assert"]);
const clip = (value: string, max: number) => (value.length > max ? `${value.slice(0, max - 1)}…` : value);
const firstLine = (value: string) => value.split("\n")[0].trim();

class Stop { constructor(readonly reason: ReplayStopReason) {} }

interface Snapshot { mode: string; nodes: SnapshotNode[]; output: Record<string, any>; tokens: Set<string> }

/** The detector finding key a baseline stores: the element and kind, without screen / step / device. */
export function baselineKey(finding: Finding): string {
  return findingKey({ kind: finding.kind, path: finding.path, label: finding.label, detail: finding.detail });
}

/**
 * The signature with control labels dropped (a control is known by its role and
 * place): a renamed button is the same screen, a changed text is not.
 */
export function labelFree(tokens: Iterable<string>): Set<string> {
  const out = new Set<string>();
  for (const token of tokens) {
    if (!token.startsWith("c|")) { out.add(token); continue; }
    const parts = token.split("|");
    out.add(`c|${parts[1]}||${parts[parts.length - 1]}`);
  }
  return out;
}

/** How alike the screen is to the expected one: as recorded, and with control labels ignored. */
export function screenScore(expected: Set<string>, actual: Set<string>): { full: number; loose: number } {
  return { full: similarity(expected, actual), loose: similarity(labelFree(expected), labelFree(actual)) };
}

/** A signature token as a person reads it. */
export function describeToken(token: string): string {
  const parts = token.split("|");
  // The last two path segments name a control well enough ("Menu/ShopButton").
  if (parts[0] === "c") return `${parts[1]} ${parts[2] ? `'${parts[2]}' ` : ""}(${parts.slice(3).join("|").split("/").slice(-2).join("/")})`;
  if (parts[0] === "t") return `text '${parts.slice(1).join("|")}'`;
  return token;
}

export interface TargetMatch {
  node: SnapshotNode | null;
  drift: Drift[];
  /** Found but not usable now (covered, disabled, off). */
  blocked?: string;
  candidates: string[];
}

/**
 * Find a scenario target among the snapshot's nodes: same masked path and label
 * (exact), else same path with another label (label_changed), else the same label
 * (and role) at another path (path_changed); several equal matches → `index`.
 */
export function matchTarget(nodes: SnapshotNode[], target: ScenarioTarget, kind: "tap" | "type"): TargetMatch {
  const visible = nodes.filter(n => n.visible !== false);
  const pool = kind === "type" ? visible.filter(n => n.role === "input") : visible.filter(n => n.role !== "input" || target.role === "input");
  const path = target.path ? maskPath(target.path) : null;
  const label = target.text && kind !== "type" ? maskText(target.text) : null;
  const sameRole = (n: SnapshotNode) => !target.role || n.role === target.role;
  const drift: Drift[] = [];
  let matches = pool.filter(n => (!path || maskPath(n.path) === path) && (!label || maskText(n.text) === label) && (path !== null || sameRole(n)));
  if (!matches.length && path && label) {
    matches = pool.filter(n => maskPath(n.path) === path);
    if (matches.length) drift.push({ kind: "label_changed", detail: `'${target.text}' is now '${clip(String(matches[0].text ?? ""), 60)}' (${target.path})` });
    else {
      matches = pool.filter(n => maskText(n.text) === label && sameRole(n));
      if (matches.length) drift.push({ kind: "path_changed", detail: `'${target.text}' moved from ${target.path} to ${matches[0].path}` });
    }
  }
  const candidates = pool.filter(n => n.role !== "text" && n.role !== "image").slice(0, 12).map(n => `${n.role} '${clip(String(n.text ?? ""), 40)}' (${n.path})`);
  if (!matches.length) return { node: null, drift: [], candidates };
  // Siblings whose names differ only by a number ("Item (2)") share a masked path: the recorded one first.
  const exactPath = target.path ? matches.filter(n => n.path === target.path) : [];
  if (exactPath.length) matches = exactPath;
  const index = exactPath.length === 1 ? 0 : target.index ?? 0;
  let node = matches[Math.min(index, matches.length - 1)];
  if (index >= matches.length) drift.push({ kind: "index_changed", detail: `match ${index} of '${target.text ?? target.path}' is gone (${matches.length} now); the last one was used` });
  const usable = kind === "type" ? isFillable(node) : node.interactable !== false && !node.occluded_by;
  if (!usable) {
    // Another equal match that is usable now (a list with a disabled row) is not a substitute: the recorded one is reported.
    return {
      node, drift, candidates,
      blocked: node.occluded_by ? `covered by ${node.occluded_by}` : node.interactable === false ? "not interactable" : "not a usable text field",
    };
  }
  return { node, drift, candidates };
}

export class Replayer {
  private readonly now: () => number;
  private readonly sleep: (ms: number) => Promise<void>;
  private touchPx: number | null | undefined = undefined;
  private startedAt = 0;
  private readonly notes: string[] = [];
  private readonly totalSteps: number;
  /** Every attempt so far; the one running is filled in as it goes (a cancel keeps it). */
  private readonly attempts: Attempt[] = [];

  constructor(private readonly scenario: Scenario, private readonly options: ReplayOptions, private readonly deps: ReplayDeps) {
    this.now = deps.now ?? Date.now;
    this.sleep = deps.sleep ?? (ms => new Promise(resolve => setTimeout(resolve, ms)));
    this.totalSteps = scenario.steps.length;
  }

  /** Replay with retries; the result is always returned (never thrown) once the run started. */
  async run(): Promise<{ result: Record<string, unknown>; recorded: Scenario | null; frames: Attempt["frames"] }> {
    this.startedAt = this.now();
    const probe = await this.snapshotOnce(false);
    if (!probe) throw new ReplayError("snapshot_failed", "ui_snapshot failed before the replay started.");
    const restartsFromEdit = this.options.from === "start" && this.scenario.start.restart === "play_mode";
    if (probe.mode !== "play" && !restartsFromEdit)
      throw new ReplayError("play_mode_required", "This replay runs on the running game: enter play mode first (or replay from the start of a scenario that restarts play mode).");
    if (probe.mode === "play") this.touchPx = minTouchPx(this.options.minTouchDp, probe.output?.screen?.dpi);
    // A retry repeats the same conditions: the scenario's start. A run from the
    // current screen cannot be repeated from there, so its failure is not retried
    // (a pass from the start after it would read as flaky when it is not).
    const canRestart = this.scenario.start.restart !== "none";
    const fromCurrent = this.options.from === "current";
    const maxAttempts = this.options.record || !canRestart || fromCurrent ? 1 : 1 + this.options.retries;
    if (this.options.retries > 0 && !this.options.record) {
      if (!canRestart) this.notes.push("No retries: the scenario has no way to restart (start.restart none).");
      else if (fromCurrent) this.notes.push("No retries: a run from the current screen cannot be repeated from the same place; replay from the start to retry.");
    }
    let stop: ReplayStopReason = "completed";
    try {
      for (let a = 1; a <= maxAttempts; a++) {
        const attempt = await this.attempt(a, a > 1 || this.options.from === "start");
        if (attempt.passed) break;
      }
    } catch (error) {
      if (!(error instanceof Stop)) throw error;
      stop = error.reason;
    }
    return this.result(this.attempts, stop, maxAttempts);
  }

  // -------------------------------------------------------------------------
  // One attempt
  // -------------------------------------------------------------------------

  private async attempt(number: number, restart: boolean): Promise<Attempt> {
    const t0 = this.now();
    const attempt: Attempt = {
      attempt: number, passed: false, steps: [], observed: [], frames: [], duration_ms: 0,
      start: { n: 0, do: "start", label: "Start", status: "skipped", drift: [], duration_ms: 0 },
    };
    this.attempts.push(attempt);
    this.deps.console?.take();
    const start = await this.startStep(number, restart, attempt);
    attempt.start = start;
    if (start.status === "failed") {
      attempt.steps = this.scenario.steps.map((s, i) => this.skipped(i + 1, s));
      attempt.duration_ms = this.now() - t0;
      return attempt;
    }
    let failed = false;
    for (let i = 0; i < this.scenario.steps.length; i++) {
      const step = this.scenario.steps[i];
      if (failed) { attempt.steps.push(this.skipped(i + 1, step)); continue; }
      this.checkBudgets();
      // Errors that reached us after the previous step ended were logged during it.
      const previous = attempt.steps[attempt.steps.length - 1] ?? start;
      if (this.lateErrors(previous)) {
        failed = true;
        attempt.steps.push(this.skipped(i + 1, step));
        continue;
      }
      const { outcome, observed } = await this.runStep(i + 1, step, attempt);
      attempt.steps.push(outcome);
      attempt.observed[i] = observed;
      this.progress(number, i + 1, outcome.status);
      if (outcome.status === "failed") failed = true;
    }
    if (!failed) {
      // The last step's consequences: errors logged just after it.
      await this.sleep(300);
      const last = attempt.steps[attempt.steps.length - 1] ?? start;
      if (this.lateErrors(last)) failed = true;
    }
    attempt.passed = !failed;
    attempt.duration_ms = this.now() - t0;
    return attempt;
  }

  private async startStep(number: number, restart: boolean, attempt: Attempt): Promise<StepOutcome> {
    const start = this.scenario.start;
    const how = !restart ? "the current screen" : start.restart === "command" ? `restart with ${start.command}` : start.restart === "play_mode" ? "restart play mode" : "the current screen (no restart)";
    const label = `Start (${how})${number > 1 ? `, attempt ${number}` : ""}`;
    const t0 = this.now();
    const step = this.begin(`Replay ${this.scenario.name}: ${label}`, { scenario: this.scenario.name, n: 0, do: "start", attempt: number });
    const logs: unknown[] = [];
    const outcome: StepOutcome = { n: 0, do: "start", label, status: "passed", drift: [], duration_ms: 0 };
    if (restart && start.restart !== "none") {
      const failure = await restartGame(this.deps.call, startPlan(start, this.options.vars), logs);
      if (failure) this.fail(outcome, "restart_failed", failure.message);
    }
    if (outcome.status !== "failed") {
      if (this.touchPx === undefined) {
        const probe = await this.snapshotOnce(false, logs);
        this.touchPx = minTouchPx(this.options.minTouchDp, probe?.output?.screen?.dpi);
      }
      const observed = await this.observeAndCompare(outcome, { screen: start.screen, findings: start.findings }, logs);
      if (observed) attempt.observedStart = observed;
      if (this.options.visual && (outcome.status as string) !== "failed") await this.visualCheck(outcome, 0, "start", logs, attempt);
    }
    this.stepErrors(outcome, logs, t0);
    await this.finishStep(step, outcome, logs, t0);
    return outcome;
  }

  private async runStep(n: number, step: ScenarioStep, attempt: Attempt): Promise<{ outcome: StepOutcome; observed?: Observed }> {
    const label = step.label ?? defaultLabel(step);
    const t0 = this.now();
    const secret = step.do === "type" && (step.secret === true || usesSecret(step, this.scenario));
    const recordedArgs: Record<string, unknown> = {
      scenario: this.scenario.name, n, do: step.do,
      ...(step.target ? { target: step.target.text ?? step.target.path, ...(step.target.path ? { path: step.target.path } : {}) } : {}),
      ...(step.do === "type" && step.text !== undefined ? { text: secret ? `[redacted ${fillVars(step.text, this.options.vars).length} chars]` : fillVars(step.text, this.options.vars) } : {}),
      ...(step.command ? { command: step.command } : {}), ...(step.tool ? { tool: step.tool } : {}),
    };
    const recorded = this.begin(`Replay step ${n}: ${label}`, recordedArgs);
    const logs: unknown[] = [];
    const outcome: StepOutcome = { n, do: step.do, label, status: "passed", drift: [], duration_ms: 0 };
    const resolved: { target?: ScenarioTarget } = {};
    await this.act(step, outcome, logs, secret, node => {
      resolved.target = {
        path: node.path, ...(step.do !== "type" && node.text ? { text: String(node.text) } : {}), role: node.role,
        ...(step.target?.index ? { index: step.target.index } : {}),
      };
    });
    let observed: Observed | undefined;
    if (outcome.status !== "failed") {
      observed = await this.observeAndCompare(outcome, step.expect ?? {}, logs);
      if (observed && resolved.target) observed.target = resolved.target;
      const missing = (step.expect?.texts ?? []).filter(t => !this.lastTexts.some(v => v.includes(t.toLowerCase())));
      if (missing.length && (outcome.status as string) !== "failed")
        this.fail(outcome, "text_missing", `expected text not on screen: ${missing.map(t => `'${t}'`).join(", ")}`);
      if (this.options.visual && step.visual !== false && (outcome.status as string) !== "failed") await this.visualCheck(outcome, n, label, logs, attempt);
    }
    this.stepErrors(outcome, logs, t0);
    await this.finishStep(recorded, outcome, logs, t0);
    return { outcome, observed };
  }

  /** The step's action. Failures are written into `outcome`. */
  private async act(step: ScenarioStep, outcome: StepOutcome, logs: unknown[], secret: boolean, resolved: (node: SnapshotNode) => void): Promise<void> {
    const vars = this.options.vars;
    switch (step.do) {
      case "tap":
      case "type": {
        const match = await this.findTarget(step, logs);
        if (!match) { this.fail(outcome, "snapshot_failed", "ui_snapshot kept failing while looking for the target"); return; }
        if (!match.node || match.blocked) {
          const what = `'${step.target?.text ?? ""}'${step.target?.path ? ` (${step.target.path})` : ""}`.replace(/^'' /, "");
          if (!match.node) this.fail(outcome, "target_not_found", `${what} is not on screen`, { candidates: match.candidates });
          else this.fail(outcome, "target_not_tappable", `${what} is on screen but ${match.blocked}`);
          return;
        }
        outcome.drift.push(...match.drift);
        resolved(match.node);
        const result = step.do === "tap"
          ? await this.deps.call(POINTER_TOOL, { action: "click", object_id: String(match.node.id), routing: step.routing ?? this.routing() })
          : await this.deps.call(TEXT_TOOL, {
            object_id: String(match.node.id), mode: step.mode ?? "replace", end: step.end ?? "end_edit",
            ...(step.mode !== "clear" ? { text: fillVars(step.text ?? "", vars) } : {}), ...(secret ? { secret: true } : {}),
          });
        collectLogs(result, logs);
        const out = toolOutput(result);
        if (result.isError || out.error) this.fail(outcome, "action_failed", errorText(step.do === "tap" ? "the tap" : "typing", out.error));
        return;
      }
      case "wait": {
        const result = await this.deps.call(WAIT_TOOL, fillVars(step.args ?? {}, vars));
        collectLogs(result, logs);
        const out = toolOutput(result);
        const error = out.error as { code?: string } | undefined;
        if (result.isError || error) this.fail(outcome, error?.code === "timed_out" ? "wait_timed_out" : "action_failed", errorText("the wait", error));
        return;
      }
      case "command": {
        const result = await this.deps.call(COMMANDS_TOOL, { action: "invoke", command_id: step.command, args: fillVars(step.args ?? {}, vars) });
        collectLogs(result, logs);
        const out = toolOutput(result);
        if (result.isError || out.error) this.fail(outcome, "command_failed", errorText(`the command ${step.command}`, out.error));
        else if (typeof out.job_id === "string") this.fail(outcome, "command_failed", `${step.command} runs as a job; a replay step runs synchronous commands only.`);
        return;
      }
      case "input": {
        const result = await this.deps.call(step.tool!, fillVars(step.args ?? {}, vars));
        collectLogs(result, logs);
        const out = toolOutput(result);
        if (result.isError || out.error) this.fail(outcome, "action_failed", errorText(shortTool(step.tool!), out.error));
        return;
      }
      case "restart": {
        const start = this.scenario.start;
        if (start.restart === "none") { this.fail(outcome, "restart_failed", "the scenario has no way to restart (start.restart none)"); return; }
        const failure = await restartGame(this.deps.call, startPlan(start, vars), logs);
        if (failure) this.fail(outcome, "restart_failed", failure.message);
        return;
      }
      case "check":
        return;
    }
  }

  // -------------------------------------------------------------------------
  // Targets and screens
  // -------------------------------------------------------------------------

  private routing(): "dispatch" | "raycast_real" { return this.scenario.settings.routing; }

  /** Snapshot until the target is on screen and usable, or the step timeout passes (the last match is returned). */
  private async findTarget(step: ScenarioStep, logs: unknown[]): Promise<TargetMatch | null> {
    const deadline = this.now() + this.options.stepTimeoutMs;
    let last: TargetMatch | null = null;
    for (;;) {
      const snap = await this.requireSnapshot(logs);
      if (!snap) return null;
      last = matchTarget(snap.nodes, step.target ?? {}, step.do === "type" ? "type" : "tap");
      if (last.node && !last.blocked) return last;
      if (this.now() >= deadline) return last;
      await this.sleep(this.options.settleMs);
    }
  }

  private lastTexts: string[] = [];
  /** The snapshot the last step settled on (rects for masks and regions). */
  private lastSnap: Snapshot | null = null;

  /**
   * Snapshot until the screen matches the expected one and stops changing (or,
   * without an expectation, until it stops changing), then compare: similarity,
   * the controls / texts added and removed, detector findings against the baseline.
   */
  private async observeAndCompare(outcome: StepOutcome, expect: { screen?: ScreenExpectation; findings?: string[] }, logs: unknown[]): Promise<Observed | undefined> {
    const expected = expect.screen ? new Set(expect.screen.signature) : null;
    const t0 = this.now();
    const deadline = t0 + (expected ? this.options.stepTimeoutMs : this.options.maxSettleMs);
    let previous: Snapshot | null = null;
    let snap: Snapshot | null = null;
    let settled = false;
    for (;;) {
      snap = await this.requireSnapshot(logs);
      if (!snap) { this.fail(outcome, "snapshot_failed", "ui_snapshot failed after the step"); return undefined; }
      if (snap.mode !== "play") { this.fail(outcome, "play_mode_exited", "play mode ended during the step"); return undefined; }
      settled = previous !== null && sameTokens(previous.tokens, snap.tokens);
      const matches = !expected || (({ full, loose }) => Math.max(full, loose))(screenScore(expected, snap.tokens)) >= this.options.sameScreen;
      if (settled && matches) break;
      if (this.now() >= deadline) break;
      previous = snap;
      await this.sleep(this.options.settleMs);
    }
    outcome.settled = settled;
    this.lastSnap = snap;
    outcome.title = screenTitle(snap.nodes);
    this.lastTexts = snap.nodes.filter(n => n.visible !== false && n.text).map(n => String(n.text).toLowerCase());
    const signature = [...snap.tokens].sort();
    if (expected && expect.screen) {
      const { full: score, loose } = screenScore(expected, snap.tokens);
      outcome.similarity = Math.round(score * 1000) / 1000;
      if (expect.screen.title) outcome.expected_title = expect.screen.title;
      if (score < 1) {
        outcome.diff = {
          added: signature.filter(t => !expected.has(t)).slice(0, 10).map(describeToken),
          removed: [...expected].filter(t => !snap!.tokens.has(t)).sort().slice(0, 10).map(describeToken),
        };
      }
      if (Math.max(score, loose) < this.options.sameScreen) {
        if (this.options.record) outcome.changed = true;
        else this.fail(outcome, "screen_changed",
          `the screen is ${Math.round(score * 100)}% like the expected one ('${expect.screen.title ?? "?"}'), under ${Math.round(this.options.sameScreen * 100)}%; now '${outcome.title}'` +
          (outcome.n === 0 && this.options.from === "current" ? " — the run started on the screen shown now (from: current): go to the scenario's first screen, or replay from: \"start\"" : ""));
      } else if (score < this.options.sameScreen) {
        // The same controls in the same places, relabelled (copy or localisation changes).
        if (this.options.record) outcome.changed = true;
        outcome.drift.push({ kind: "labels_changed", detail: `the same controls in the same places, but ${Math.round(score * 100)}% like the baseline with their labels: +${outcome.diff!.added.length} / −${outcome.diff!.removed.length}` });
      } else if (score < 1) {
        if (this.options.record) outcome.changed = true;
        outcome.drift.push({ kind: "screen_drift", detail: `${Math.round(score * 100)}% like the baseline: +${outcome.diff!.added.length} / −${outcome.diff!.removed.length} controls or texts` });
      }
    }
    const findings = findingsFromSnapshot(snap.output);
    const keys = [...new Set(findings.map(baselineKey))].sort();
    // On the wrong screen the baseline's findings are another screen's: not compared.
    if (expect.findings && outcome.reason !== "screen_changed") {
      const before = new Set(expect.findings);
      const now = new Set(keys);
      const fresh = findings.filter(f => !before.has(baselineKey(f)) && !this.options.known?.match(f));
      const seen = new Set<string>();
      const freshUnique = fresh.filter(f => { const k = baselineKey(f); if (seen.has(k)) return false; seen.add(k); return true; });
      const fixed = expect.findings.filter(k => !now.has(k));
      if (freshUnique.length) {
        outcome.new_findings = freshUnique.slice(0, 20).map(f => ({ kind: f.kind, severity: f.severity, ...(f.path ? { path: f.path } : {}), ...(f.label ? { label: f.label } : {}), detail: f.detail }));
        if (this.options.record) outcome.changed = true;
        const bugs = freshUnique.filter(f => f.severity === "bug");
        for (const f of freshUnique.filter(x => x.severity !== "bug"))
          outcome.drift.push({ kind: "new_suspicious", detail: `${f.kind} ${f.path ?? ""}: ${clip(f.detail, 120)}` });
        if (bugs.length && !this.options.record)
          this.fail(outcome, "new_finding", `${bugs.length} new finding(s) on this screen: ${bugs.slice(0, 3).map(f => `${f.kind} ${f.path ?? ""}`).join(", ")}`);
      }
      if (fixed.length) {
        outcome.fixed_findings = fixed.slice(0, 20);
        if (this.options.record) outcome.changed = true;
        for (const k of fixed.slice(0, 5)) outcome.drift.push({ kind: "fixed_finding", detail: `no longer found: ${k}` });
      }
    }
    if (this.options.strict && outcome.status !== "failed") {
      const moved = outcome.drift.filter(d => d.kind === "label_changed" || d.kind === "path_changed" || d.kind === "index_changed" || d.kind === "labels_changed");
      if (moved.length) this.fail(outcome, "drift", `strict: ${moved.map(d => d.detail).join("; ")}`);
    }
    return { screen: { title: outcome.title, signature }, findings: keys };
  }

  // -------------------------------------------------------------------------
  // Pictures
  // -------------------------------------------------------------------------

  /**
   * Capture the composed frame and compare it with the step's visual baseline
   * (a record run keeps it instead, once two captures in a row match). A frame
   * that does not match is captured again (an animation settling) before the
   * step fails with the regions that changed and a diff image.
   */
  private async visualCheck(outcome: StepOutcome, n: number, label: string, logs: unknown[], attempt: Attempt): Promise<void> {
    const settings = this.options.visual!;
    const visual = this.deps.visual;
    if (!visual) { outcome.visual = { status: "skipped", note: "no baseline store" }; return; }
    const deadline = this.now() + this.options.stepTimeoutMs;
    const snap = this.lastSnap;
    const screen = snap?.output?.screen && Number(snap.output.screen.w) > 0 ? { w: Number(snap.output.screen.w), h: Number(snap.output.screen.h) } : null;
    let previous: RgbaImage | null = null;
    for (let tries = 1; ; tries++) {
      const shot = await this.deps.call(SCREENSHOT_TOOL, { view: "composed", max_edge: settings.max_edge });
      collectLogs(shot, logs);
      const out = toolOutput(shot);
      if (shot.isError || out.error || typeof out.filePath !== "string") {
        outcome.visual = { status: "skipped", note: `the capture failed (${(out.error as { code?: string } | undefined)?.code ?? "error"})` };
        return;
      }
      const file = out.filePath;
      const capturedAt = out.capturedAt && typeof out.capturedAt === "object" ? out.capturedAt as Record<string, unknown> : undefined;
      const key = visualKey(capturedAt);
      const image = visual.read(file);
      if (!image) { outcome.visual = { status: "skipped", key, note: `the frame could not be read (${file})` }; return; }
      const masks = masksFor(snap?.nodes ?? [], screen, image, settings.mask, settings.mask_numbers);
      const compare = (base: RgbaImage) => compareImages(base, image, { pixelTolerance: settings.pixel_tolerance, threshold: settings.threshold, masks });
      if (this.options.record) {
        // Keep a frame only once the picture stopped changing (two captures in a row alike).
        const still = previous ? compare(previous) : null;
        const settled = !!still && !("sizeMismatch" in still) && still.pass;
        if (settled || tries >= 3 || this.now() >= deadline) {
          attempt.frames.push({ n, label, source: file, key, ...(capturedAt ? { capturedAt } : {}) });
          outcome.visual = { status: "recorded", key, ...(settled ? {} : { note: "the picture kept changing; the last frame was kept" }) };
          return;
        }
        previous = image;
        await this.sleep(this.options.settleMs);
        continue;
      }
      const base = visual.baseline(key, n, label);
      if ("missing" in base) {
        outcome.visual = { status: "unbaselined", key, note: base.missing === "no_key" ? `no visual baseline for ${key}` : base.missing === "other_step" ? `the baseline frame was ${base.detail}: record again` : `${base.missing}${base.detail ? ` (${base.detail})` : ""}` };
        return;
      }
      const result = compare(base.image);
      if ("sizeMismatch" in result) {
        outcome.visual = { status: "unbaselined", key, note: `the baseline is ${result.sizeMismatch.baseline.join("×")}, this frame ${result.sizeMismatch.current.join("×")} (max_edge changed?): record again` };
        return;
      }
      const ratio = Math.round(result.ratio * 1e6) / 1e6;
      if (result.pass) { outcome.visual = { status: "passed", key, ratio }; return; }
      if (tries < 3 && this.now() + this.options.settleMs * 2 < deadline) { await this.sleep(this.options.settleMs * 2); continue; }
      const regions = result.regions.slice(0, 5).map(region => ({ where: describeRegion(region, image), near: nodesInRegion(region, snap?.nodes ?? [], screen, image) }));
      const diff = visual.writeDiff(diffImage(image, result), `step-${String(n).padStart(2, "0")}-diff`);
      outcome.visual = {
        status: "failed", key, ratio, threshold: settings.threshold, regions, baseline: base.file, current: file, ...(diff ? { diff } : {}),
      };
      outcome.frame = file;
      const pct = (v: number) => `${Math.round(v * 10000) / 100}%`;
      const first = regions[0];
      this.fail(outcome, "visual_changed",
        `the screen looks ${pct(ratio)} different from its visual baseline (over ${pct(settings.threshold)})${first ? ` ${first.where.startsWith("across") ? first.where : `at ${first.where}`}${first.near.length ? ` around ${first.near[0]}` : ""}` : ""}`);
      return;
    }
  }

  // -------------------------------------------------------------------------
  // Console errors
  // -------------------------------------------------------------------------

  private ignored(message: string): boolean {
    const line = firstLine(message);
    return this.options.ignoreErrors.some(r => r.test(line));
  }

  /** Errors of this step: the responses' logs[] and the stream's errors logged since it started. */
  private stepErrors(outcome: StepOutcome, logs: unknown[], t0: number): void {
    const lines = new Set<string>();
    for (const log of logs as Array<Record<string, unknown>>)
      if (ERROR_TYPES.has(String(log.severity ?? log.type ?? "").toLowerCase()) && typeof log.message === "string") lines.add(firstLine(log.message));
    // Anything the stream delivered meanwhile was logged during this step (or, rarely, at the very end of the one before).
    for (const e of this.deps.console?.take() ?? []) lines.add(firstLine(e.message));
    this.addErrors(outcome, [...lines]);
  }

  /** Errors that arrived after `outcome`'s step ended belong to it: true when they fail it. */
  private lateErrors(outcome: StepOutcome): boolean {
    const late = (this.deps.console?.take() ?? []).map(e => firstLine(e.message));
    if (!late.length) return false;
    const before = outcome.status;
    this.addErrors(outcome, late);
    return before !== "failed" && outcome.status === "failed";
  }

  private addErrors(outcome: StepOutcome, lines: string[]): void {
    const counted = lines.filter(l => l && !this.ignored(l));
    if (!counted.length) return;
    const all = new Set([...(outcome.errors ?? []), ...counted.map(l => clip(l, 200))]);
    outcome.errors = [...all].slice(0, 10);
    if (outcome.status !== "failed") this.fail(outcome, "console_error", `${all.size} console error(s): ${clip(counted[0], 160)}`);
  }

  // -------------------------------------------------------------------------
  // Bookkeeping
  // -------------------------------------------------------------------------

  private fail(outcome: StepOutcome, reason: FailReason, message: string, extra: { candidates?: string[] } = {}): void {
    if (outcome.status === "failed") return;
    outcome.status = "failed";
    outcome.reason = reason;
    outcome.message = message;
    if (extra.candidates?.length) outcome.candidates = extra.candidates;
  }

  private skipped(n: number, step: ScenarioStep): StepOutcome {
    return { n, do: step.do, label: step.label ?? defaultLabel(step), status: "skipped", drift: [], duration_ms: 0 };
  }

  private async finishStep(step: RecordedStep, outcome: StepOutcome, logs: unknown[], t0: number): Promise<void> {
    const shoot = !outcome.frame && (this.options.capture === "steps" || (this.options.capture === "failures" && outcome.status === "failed"));
    if (shoot) {
      const shot = await this.deps.call(SCREENSHOT_TOOL, { view: "composed", max_edge: 960 });
      collectLogs(shot, logs);
      const out = toolOutput(shot);
      if (!shot.isError && !out.error && typeof out.filePath === "string") {
        outcome.frame = out.filePath;
        if (step.seq >= 0) outcome.frame_call = step.seq;
      }
    }
    if (outcome.frame && outcome.frame_call === undefined && step.seq >= 0) outcome.frame_call = step.seq;
    outcome.duration_ms = this.now() - t0;
    // The visual baseline and the diff image go into the report with the frame.
    const visualFiles = [
      ...(outcome.visual?.baseline ? [{ file: outcome.visual.baseline, label: "visual baseline" }] : []),
      ...(outcome.visual?.diff ? [{ file: outcome.visual.diff, label: "visual diff" }] : []),
    ];
    step.finish({
      step: compactOutcome(outcome), ...(outcome.frame ? { filePath: outcome.frame } : {}), ...(visualFiles.length ? { visual_files: visualFiles } : {}),
    }, logs, outcome.status === "failed");
  }

  private begin(label: string, args: Record<string, unknown>): RecordedStep {
    const step = this.deps.recorder.begin(label, args);
    if (!step) throw new Stop("session_ended");
    return step;
  }

  private checkBudgets(): void {
    if (this.deps.signal.aborted) throw new Stop("cancelled");
    if (this.now() - this.startedAt >= this.options.maxMinutes * 60_000) throw new Stop("max_time");
  }

  private progress(attempt: number, n: number, status: string): void {
    const total = Math.max(1, this.totalSteps);
    this.deps.progress?.("replaying", Math.min(1, n / total), { attempt, step: n, of: this.totalSteps, last: status });
  }

  private snapshotArgs(full: boolean): Record<string, unknown> {
    if (!full) return { profile: "compact", page_size: 1, visible_only: true };
    return {
      profile: "compact", page_size: 200, visible_only: true,
      ...(this.options.placeholderPattern ? { placeholder_pattern: this.options.placeholderPattern } : {}),
      ...(this.touchPx ? { min_touch_size: this.touchPx } : {}),
    };
  }

  private async snapshotOnce(full: boolean, logs?: unknown[]): Promise<Snapshot | null> {
    const result = await this.deps.call(SNAPSHOT_TOOL, this.snapshotArgs(full));
    if (logs) collectLogs(result, logs);
    const out = toolOutput(result);
    if (result.isError || out.error) return null;
    const nodes = Array.isArray(out.nodes) ? out.nodes as SnapshotNode[] : [];
    return { mode: String(out.mode ?? ""), nodes, output: out, tokens: screenSignature(nodes) };
  }

  /** A full snapshot, retried a few times (a domain reload, a busy frame); null when it keeps failing. */
  private async requireSnapshot(logs: unknown[]): Promise<Snapshot | null> {
    for (let i = 0; i < 4; i++) {
      if (this.deps.signal.aborted) throw new Stop("cancelled");
      const snap = await this.snapshotOnce(true, logs);
      if (snap) return snap;
      await this.sleep(this.options.settleMs);
    }
    return null;
  }

  // -------------------------------------------------------------------------
  // Result
  // -------------------------------------------------------------------------

  private result(attempts: Attempt[], stop: ReplayStopReason, maxAttempts: number): { result: Record<string, unknown>; recorded: Scenario | null; frames: Attempt["frames"] } {
    const last = attempts[attempts.length - 1];
    const firstFailure = (a: Attempt) => [a.start, ...a.steps].find(s => s.status === "failed");
    const failures = attempts.filter(a => !a.passed).map(a => ({ attempt: a.attempt, failure: firstFailure(a) }));
    let verdict: string;
    if (stop !== "completed" && !(last?.passed)) verdict = stop === "cancelled" ? "cancelled" : "incomplete";
    else if (!last) verdict = "incomplete";
    else if (this.options.record) verdict = last.passed ? "recorded" : "failed";
    else if (last.passed) verdict = attempts.length > 1 ? "flaky" : "passed";
    else verdict = "failed";
    const consistent = verdict === "failed" && failures.length > 1
      && failures.every(f => f.failure?.n === failures[0].failure?.n && f.failure?.reason === failures[0].failure?.reason);
    const outcomes = last ? [last.start, ...last.steps] : [];
    const drift = outcomes.flatMap(o => o.drift.map(d => ({ step: o.n, ...d }))).slice(0, 40);
    const newFindings = outcomes.flatMap(o => (o.new_findings ?? []).map(f => ({ step: o.n, ...f }))).slice(0, 30);
    const fixed = [...new Set(outcomes.flatMap(o => o.fixed_findings ?? []))].slice(0, 30);
    const failure = last ? firstFailure(last) : undefined;
    const unbaselined = this.scenario.steps.filter(s => !s.expect?.screen).length;
    let recorded: Scenario | null = null;
    if (this.options.record && last?.passed) recorded = this.baseline(last);
    const result: Record<string, unknown> = {
      status: "ok",
      verdict,
      stop_reason: stop,
      scenario: { name: this.scenario.name, steps: this.scenario.steps.length },
      attempts: attempts.map(a => {
        const f = firstFailure(a);
        return { attempt: a.attempt, passed: a.passed, duration_ms: a.duration_ms, ...(f ? { failed_step: f.n, reason: f.reason } : {}) };
      }),
      ...(maxAttempts > 1 ? { max_attempts: maxAttempts } : {}),
      ...(verdict === "failed" && failures.length > 1 ? { consistent } : {}),
      steps: outcomes.map(compactOutcome),
      ...(failure ? { failure: compactOutcome(failure) } : {}),
      ...(verdict === "flaky" ? { earlier_failures: failures.map(f => ({ attempt: f.attempt, ...(f.failure ? compactOutcome(f.failure) : {}) })) } : {}),
      drift,
      findings: { new: newFindings, fixed },
      ...(this.options.record ? { changed_steps: outcomes.filter(o => o.changed).map(o => o.n) } : {}),
      unbaselined: this.options.record ? 0 : unbaselined,
      ...(this.options.visual ? { visual: visualSummary(outcomes, this.options.visual) } : {}),
      duration_ms: Math.max(0, this.now() - this.startedAt),
      ...(this.notes.length ? { notes: [...new Set(this.notes)] } : {}),
    };
    return { result, recorded, frames: this.options.record && last?.passed ? last.frames : [] };
  }

  /** The scenario with this attempt's observations as its expectations (and the targets as resolved). */
  private baseline(attempt: Attempt): Scenario {
    const copy = JSON.parse(JSON.stringify(this.scenario)) as Scenario;
    if (this.options.visual) copy.settings.visual = this.options.visual;
    if (attempt.observedStart) {
      copy.start.screen = attempt.observedStart.screen;
      copy.start.findings = attempt.observedStart.findings;
    }
    copy.steps.forEach((step, i) => {
      const seen = attempt.observed[i];
      if (!seen) return;
      step.expect = { ...(step.expect ?? {}), screen: seen.screen, findings: seen.findings };
      if (seen.target && (step.do === "tap" || step.do === "type")) step.target = seen.target;
    });
    return copy;
  }
}

function sameTokens(a: Set<string>, b: Set<string>): boolean {
  if (a.size !== b.size) return false;
  for (const t of a) if (!b.has(t)) return false;
  return true;
}

const shortTool = (tool: string) => tool.replace(/^unity_open_mcp_/, "");

function errorText(what: string, error: unknown): string {
  const e = (error ?? {}) as { code?: string; message?: string };
  return `${what} failed (${e.code ?? "error"})${e.message ? `: ${clip(String(e.message), 200)}` : ""}`;
}

/** A step's default label: what it does and to what. */
export function defaultLabel(step: ScenarioStep): string {
  const target = step.target ? `'${step.target.text ?? step.target.path?.split("/").pop() ?? "?"}'` : "";
  switch (step.do) {
    case "tap": return `Tap ${target}`;
    case "type": return `Type into ${target}`;
    case "wait": return `Wait for ${clip(JSON.stringify((step.args ?? {}).condition ?? {}), 60)}`;
    case "command": return `Run ${step.command}`;
    case "input": return `${shortTool(step.tool ?? "input")} ${clip(JSON.stringify(step.args ?? {}), 60)}`;
    case "restart": return "Restart the game";
    case "check": return "Check the screen";
  }
}

/** A step outcome as results and the session log carry it (bounded). */
function compactOutcome(o: StepOutcome): Record<string, unknown> {
  return {
    n: o.n, do: o.do, label: o.label, status: o.status,
    ...(o.reason ? { reason: o.reason, message: o.message } : {}),
    ...(o.similarity !== undefined ? { similarity: o.similarity } : {}),
    ...(o.title ? { title: o.title } : {}), ...(o.expected_title ? { expected_title: o.expected_title } : {}),
    ...(o.diff && (o.diff.added.length || o.diff.removed.length) ? { diff: o.diff } : {}),
    ...(o.drift.length ? { drift: o.drift.slice(0, 10) } : {}),
    ...(o.errors?.length ? { errors: o.errors } : {}),
    ...(o.new_findings?.length ? { new_findings: o.new_findings.slice(0, 10) } : {}),
    ...(o.fixed_findings?.length ? { fixed_findings: o.fixed_findings.slice(0, 10) } : {}),
    ...(o.candidates?.length ? { candidates: o.candidates } : {}),
    ...(o.changed ? { changed: true } : {}),
    ...(o.settled === false ? { settled: false } : {}),
    ...(o.visual ? { visual: o.visual } : {}),
    ...(o.frame ? { frame: o.frame } : {}), ...(o.frame_call !== undefined ? { frame_call: o.frame_call } : {}),
    duration_ms: o.duration_ms,
  };
}

/** The run's picture checks: the key compared on, and how many steps passed, failed or had no baseline. */
function visualSummary(outcomes: StepOutcome[], settings: VisualSettings): Record<string, unknown> {
  const counts: Record<string, number> = { passed: 0, failed: 0, unbaselined: 0, recorded: 0, skipped: 0 };
  let key: string | undefined;
  for (const o of outcomes) if (o.visual) { counts[o.visual.status]++; key ??= o.visual.key; }
  const notes = [...new Set(outcomes.map(o => o.visual?.note).filter((n): n is string => !!n))].slice(0, 5);
  return { ...(key ? { key } : {}), threshold: settings.threshold, max_edge: settings.max_edge, ...counts, ...(notes.length ? { notes } : {}) };
}
