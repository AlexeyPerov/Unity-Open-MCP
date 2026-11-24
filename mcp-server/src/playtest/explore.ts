// Playtest UI crawler — the engine behind unity_open_mcp_playtest_explore.
//
// It walks the running game's uGUI on its own, with no model in the loop:
// snapshot the screen until it stops changing, tap the next control it has not
// tried there, settle, and file the edge screen → screen. A screen is a set of
// visible controls and texts with numbers masked (see detectors.ts); two
// snapshots whose sets are similar enough are the same screen. When a screen
// has nothing left to try, the crawler walks the shortest known path to a
// screen that has, or restarts the game (play mode, or the project's own boot
// command), within its budgets. Money, destructive and leaving actions are
// never tapped (deny-list). Every new screen is captured once and checked by
// the snapshot detectors; a tap that changed nothing and logged nothing is a
// dead button; a screen with nothing to tap is a dead end.
//
// The engine talks to the bridge only through the ToolCaller it is given (the
// router's normal dispatch, as the session owner) and records each tap as one
// call of the open playtest session through ExploreRecorder; it never touches
// the session log itself. Pure scheduling: no timers besides `sleep`.
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { toolOutput, toolResultBody } from "../jobs/adapters.js";
import {
  FindingSet, actionKey, controlLabel, denyReason, findingsFromSnapshot, isBackLike, isFillable, isTappable, minTouchPx,
  screenSignature, screenTitle, similarity, sortFindings, truncatedDiagnostics,
  type ActionPolicy, type Finding, type SnapshotNode,
} from "./detectors.js";
import { knownEntry, type KnownMatch, type MemoryOptions } from "./profile.js";

export type ToolCaller = (tool: string, args: Record<string, unknown>) => Promise<CallToolResult>;

/** One recorded crawler step: the router turns it into a session call of kind `explore`. */
export interface RecordedStep {
  seq: number;
  finish(output: Record<string, unknown>, logs: unknown[], failed?: boolean): void;
}
export interface ExploreRecorder {
  /** null when the owner's session is gone (ended by the agent): the crawl stops. */
  begin(label: string, args: Record<string, unknown>): RecordedStep | null;
}

export type RestartMode = "play_mode" | "command" | "none";

export interface ExploreOptions {
  maxSteps: number;
  maxMinutes: number;
  maxScreens: number;
  sameScreen: number;
  settleMs: number;
  maxSettleMs: number;
  restart: RestartMode;
  restartCommand?: string;
  restartArgs?: Record<string, unknown>;
  maxRestarts: number;
  policy: ActionPolicy;
  routing: "dispatch" | "raycast_real";
  /** null = no placeholder check. */
  placeholderPattern: string | null;
  /** null = no touch-size check. */
  minTouchDp: number | null;
  capture: boolean;
  /** Type fillText into each visible text field once (inputsim_text, end_edit). */
  fillInputs: boolean;
  fillText: string;
  /** The project profile's accepted findings, console noise and extra back labels (Playtests/profile.json). */
  memory?: MemoryOptions;
}

export const EXPLORE_DEFAULTS = {
  max_steps: 60, max_minutes: 10, max_screens: 40, same_screen: 0.8, settle_ms: 250, max_settle_ms: 4_000,
  restart: "play_mode" as RestartMode, max_restarts: 3, routing: "dispatch" as const, min_touch_dp: 44, capture: true,
  fill_inputs: true, fill_text: "Test",
};
export const EXPLORE_LIMITS = { max_steps: 300, max_minutes: 60, max_screens: 200, max_restarts: 10 };

export type StopReason =
  | "explored" | "max_steps" | "max_time" | "max_screens" | "no_way_back" | "cancelled" | "play_mode_exited"
  | "tool_failures" | "restart_failed" | "session_ended";

export interface ExploreDeps {
  call: ToolCaller;
  recorder: ExploreRecorder;
  signal: AbortSignal;
  now?: () => number;
  sleep?: (ms: number) => Promise<void>;
  progress?: (phase: string, fraction: number, detail: Record<string, unknown>) => void;
}

export class ExploreError extends Error {
  constructor(readonly code: string, message: string, readonly detail: Record<string, unknown> = {}) { super(message); }
}

interface Action {
  key: string;
  label: string;
  path: string;
  role: string;
  back: boolean;
  tried: boolean;
  /** Visits that ended with this action untried because it was not tappable then (covered, disabled, gone). */
  misses: number;
  outcome?: string;
  to?: string;
}

interface Screen {
  id: string;
  title: string;
  tokens: Set<string>;
  labels: string[];
  actions: Map<string, Action>;
  denied: number;
  /** Session call that captured it (its frame is in the report). */
  frameCall?: number;
  frame?: string;
  firstStep: number;
  visits: number;
  deadEnd: boolean;
}

interface Snapshot {
  mode: string;
  nodes: SnapshotNode[];
  output: Record<string, any>;
  tokens: Set<string>;
  settled: boolean;
}

interface Edge { from: string; action: string; to: string; outcome: string; step: number }

const SNAPSHOT_TOOL = "unity_open_mcp_ui_snapshot";
const POINTER_TOOL = "unity_open_mcp_inputsim_pointer";
const TEXT_TOOL = "unity_open_mcp_inputsim_text";
const SCREENSHOT_TOOL = "unity_senses_screenshot";
const MAX_CONSECUTIVE_FAILURES = 3;
const MAX_NAV_MISSES = 2;
/** Visits an untried control may be untappable before the crawler gives it up. */
const MAX_MISSES = 2;
const ERROR_SEVERITIES = new Set(["error", "exception", "assert"]);

/** Stop the loop (a budget, a cancel, a lost session): unwinds to run(). */
class Stop { constructor(readonly reason: StopReason, readonly note?: string) {} }

function sameTokens(a: Set<string>, b: Set<string>): boolean {
  if (a.size !== b.size) return false;
  for (const t of a) if (!b.has(t)) return false;
  return true;
}

const clip = (value: string, max: number) => (value.length > max ? `${value.slice(0, max - 1)}…` : value);
const firstLine = (value: string) => value.split("\n")[0].trim();

export class Explorer {
  private readonly now: () => number;
  private readonly sleep: (ms: number) => Promise<void>;
  private readonly screens: Screen[] = [];
  private readonly edges: Edge[] = [];
  private readonly findings = new FindingSet();
  /** Findings the project profile accepts (listed, not counted). */
  private readonly known = new FindingSet();
  private readonly denied = new Map<string, { screen: string; label: string; path: string; reason: string }>();
  private readonly navFailures: Array<{ from: string; action: string; expected: string; reached: string; step: number }> = [];
  private readonly notes: string[] = [];
  private current!: Screen;
  private snap!: Snapshot;
  private start!: Screen;
  private startedAt = 0;
  private steps = 0;
  private restarts = 0;
  private failures = 0;
  private navMisses = 0;
  private minTouchPx: number | null = null;
  private restarting = false;

  constructor(private readonly options: ExploreOptions, private readonly deps: ExploreDeps) {
    this.now = deps.now ?? Date.now;
    this.sleep = deps.sleep ?? (ms => new Promise(resolve => setTimeout(resolve, ms)));
  }

  /** Crawl until a budget, a cancel or nothing left; the result is always returned (never thrown) once the crawl started. */
  async run(): Promise<Record<string, unknown>> {
    this.startedAt = this.now();
    const probe = await this.snapshotOnce(false);
    if (!probe) throw new ExploreError("snapshot_failed", "ui_snapshot failed before the crawl started.");
    if (probe.mode !== "play") throw new ExploreError("play_mode_required", "The crawler explores the running game: enter play mode first (editor_set_state {\"state\":\"play\"}, then editor_status {\"wait_until\":\"playing\"}).");
    this.minTouchPx = minTouchPx(this.options.minTouchDp, probe.output?.screen?.dpi);
    if (this.options.minTouchDp !== null && this.minTouchPx === null)
      this.notes.push("The touch-size check was skipped: the game reports no screen dpi.");
    let reason: StopReason = "explored";
    let stopNote: string | undefined;
    try {
      await this.observeStart();
      reason = await this.loop();
    } catch (error) {
      if (!(error instanceof Stop)) throw error;
      reason = error.reason;
      stopNote = error.note;
    }
    if (stopNote) this.notes.push(stopNote);
    return this.result(reason);
  }

  // -------------------------------------------------------------------------
  // Loop
  // -------------------------------------------------------------------------

  private async observeStart(): Promise<void> {
    const step = this.begin("Observe the start screen", { purpose: "observe" });
    const logs: unknown[] = [];
    const snap = await this.settledSnapshot(logs);
    this.snap = snap;
    const { screen, created, findings, frame } = await this.identify(snap, step.seq, logs);
    this.current = this.start = screen;
    step.finish({ step: { purpose: "observe", screen: screen.id, title: screen.title, new_screen: created, findings: compact(findings) }, ...(frame ? { filePath: frame } : {}) }, logs);
  }

  private async loop(): Promise<StopReason> {
    for (;;) {
      this.checkBudgets();
      const next = this.nextAction(this.current);
      if (next) { await this.tap(next.action, next.node, "explore"); continue; }
      this.reportDeadEnd(this.current);
      if (!this.screens.some(s => this.untried(s).length > 0)) return "explored";
      const path = this.navMisses < MAX_NAV_MISSES ? this.pathToUntried(this.current) : null;
      if (path && path.length) {
        const hop = path[0];
        const node = this.nodeFor(hop.action);
        if (node) { await this.tap(hop.action, node, "navigate", hop.to); continue; }
      }
      if (this.options.restart === "none" || this.restarts >= this.options.maxRestarts) return "no_way_back";
      await this.restart();
    }
  }

  private checkBudgets(): void {
    if (this.deps.signal.aborted) throw new Stop("cancelled");
    if (this.steps >= this.options.maxSteps) throw new Stop("max_steps");
    if (this.now() - this.startedAt >= this.options.maxMinutes * 60_000) throw new Stop("max_time");
  }

  /** A control the crawler acts on now: a tap target, or a text field it fills. */
  private actionable(node: SnapshotNode): boolean {
    return isTappable(node) || (this.options.fillInputs && isFillable(node));
  }

  /** The next untried action of the screen that is on screen now: text fields first (a form is filled before it is sent), leaving actions last. */
  private nextAction(screen: Screen): { action: Action; node: SnapshotNode } | null {
    const candidates: Array<{ action: Action; node: SnapshotNode; order: number }> = [];
    let order = 0;
    for (const node of this.snap.nodes) {
      order++;
      if (!this.actionable(node)) continue;
      const action = screen.actions.get(actionKey(node));
      if (!action || action.tried) continue;
      if (candidates.some(c => c.action === action)) continue;
      candidates.push({ action, node, order });
    }
    const rank = (a: Action) => (a.role === "input" ? 0 : a.back ? 2 : 1);
    candidates.sort((a, b) => rank(a.action) - rank(b.action) || a.order - b.order);
    if (candidates.length) return candidates[0];
    // Nothing tappable is left on this visit: an untried control that is covered,
    // disabled or gone now gets one more visit, then is given up.
    for (const action of this.untried(screen))
      if (++action.misses >= MAX_MISSES) { action.tried = true; action.outcome = "not_tappable"; }
    return null;
  }

  private untried(screen: Screen): Action[] {
    return [...screen.actions.values()].filter(a => !a.tried);
  }

  private nodeFor(action: Action): SnapshotNode | null {
    return this.snap.nodes.find(n => this.actionable(n) && actionKey(n) === action.key) ?? null;
  }

  /** Shortest known path (by tried edges) from `from` to a screen with untried actions. */
  private pathToUntried(from: Screen): Array<{ action: Action; to: string }> | null {
    const byId = new Map(this.screens.map(s => [s.id, s]));
    const queue: Array<{ id: string; path: Array<{ action: Action; to: string }> }> = [{ id: from.id, path: [] }];
    const seen = new Set([from.id]);
    while (queue.length) {
      const { id, path } = queue.shift()!;
      const screen = byId.get(id)!;
      if (id !== from.id && this.untried(screen).length) return path;
      for (const action of screen.actions.values()) {
        if (!action.tried || !action.to || action.to === id || seen.has(action.to)) continue;
        seen.add(action.to);
        queue.push({ id: action.to, path: [...path, { action, to: action.to }] });
      }
    }
    return null;
  }

  private reportDeadEnd(screen: Screen): void {
    if (screen === this.start || screen.deadEnd || screen.actions.size > 0) return;
    screen.deadEnd = true;
    this.addFindings([{ kind: "dead_end", severity: "suspicious", screen: screen.id, step: this.steps,
      detail: `screen '${screen.title}' has nothing to tap${screen.denied ? ` except ${screen.denied} denied control(s)` : ""} and did not move on by itself` }]);
  }

  // -------------------------------------------------------------------------
  // One tap
  // -------------------------------------------------------------------------

  private async tap(action: Action, node: SnapshotNode, purpose: "explore" | "navigate", expect?: string): Promise<void> {
    const from = this.current;
    const before = this.snap;
    const typing = action.role === "input";
    const label = typing
      ? `Type '${this.options.fillText}' into '${action.label}' on ${from.id}`
      : purpose === "explore"
        ? `Tap '${action.label}' on ${from.id}`
        : `Navigate: tap '${action.label}' on ${from.id} toward ${expect}`;
    const step = this.begin(label, { purpose, screen: from.id, target: action.label, path: action.path, ...(typing ? { text: this.options.fillText } : {}) });
    this.steps++;
    const logs: unknown[] = [];
    const click = typing
      ? await this.deps.call(TEXT_TOOL, { object_id: String(node.id), text: this.options.fillText, mode: "replace", end: "end_edit" })
      : await this.deps.call(POINTER_TOOL, { action: "click", object_id: String(node.id), routing: this.options.routing });
    collectLogs(click, logs);
    const clickOut = toolOutput(click);
    if (click.isError || clickOut.error) {
      if (purpose === "explore") { action.tried = true; action.outcome = typing ? "type_failed" : "tap_failed"; }
      const failure = (clickOut.error ?? {}) as { code?: string; message?: string };
      step.finish({ step: { purpose, screen: from.id, target: action.label, outcome: typing ? "type_failed" : "tap_failed", error: failure } }, logs, true);
      this.failed(`${typing ? "typing into" : "tap"} '${action.label}' failed: ${failure.code ?? "error"}`);
      return;
    }
    const after = await this.settledSnapshot(logs);
    this.failures = 0;
    const { screen: to, created, findings, frame } = await this.identify(after, step.seq, logs);
    const outcome = to === from ? "same_screen" : created ? "new_screen" : "known_screen";
    const stepFindings = [...findings];
    const ignored = this.options.memory?.ignoreErrors ?? [];
    const consoleErrors = errorLines(logs).filter(line => !ignored.some(r => r.test(line)));
    for (const message of consoleErrors)
      stepFindings.push(...this.addFindings([{ kind: "console_error", severity: "bug", screen: from.id, label: clip(message, 160), step: this.steps,
        detail: `logged while ${typing ? "typing into" : "tapping"} '${action.label}' on ${from.id}` }]));
    if (purpose === "explore") {
      action.tried = true;
      action.outcome = outcome;
      action.to = to.id;
      this.edges.push({ from: from.id, action: action.label, to: to.id, outcome, step: this.steps });
      const interactableAfter = clickOut.interactableAfter;
      if (outcome === "same_screen" && action.role === "button" && sameTokens(before.tokens, after.tokens)
          && logs.length === 0 && interactableAfter !== false)
        stepFindings.push(...this.addFindings([{ kind: "dead_button", severity: "suspicious", screen: from.id, path: action.path,
          label: action.label, step: this.steps, detail: `tapping '${action.label}' changed nothing on screen and logged nothing` }]));
    } else {
      if (expect && to.id !== expect) {
        this.navMisses++;
        this.navFailures.push({ from: from.id, action: action.label, expected: expect, reached: to.id, step: this.steps });
        if (this.navMisses >= MAX_NAV_MISSES)
          stepFindings.push(...this.addFindings([{ kind: "navigation_failed", severity: "suspicious", screen: from.id, path: action.path,
            label: action.label, step: this.steps, detail: `tapping '${action.label}' on ${from.id} reached ${to.id}, not ${expect} as before` }]));
      } else this.navMisses = 0;
    }
    this.current = to;
    this.snap = after;
    step.finish({
      step: { purpose, screen: from.id, target: action.label, outcome, to: to.id, new_screen: created,
        ...(expect ? { expected: expect } : {}), ...(typing ? { typed: clickOut.typed ?? null } : {}),
        settled: after.settled, findings: compact(stepFindings) },
      ...(frame ? { filePath: frame } : {}),
    }, logs);
    this.progress();
  }

  private async restart(): Promise<void> {
    this.restarts++;
    const mode = this.options.restart;
    const step = this.begin(`Restart the game (${mode === "command" ? this.options.restartCommand : "play mode"})`, { purpose: "restart", mode });
    const logs: unknown[] = [];
    this.restarting = true;
    try {
      const failure = await restartGame(this.deps.call, mode, this.options.restartCommand, this.options.restartArgs, logs);
      if (failure) {
        step.finish({ step: { purpose: "restart", mode, outcome: "failed", at: failure.at, error: failure.error } }, logs, true);
        throw new Stop("restart_failed", failure.message);
      }
    } finally {
      this.restarting = false;
    }
    const after = await this.settledSnapshot(logs);
    const { screen, created, findings, frame } = await this.identify(after, step.seq, logs);
    this.current = screen;
    this.snap = after;
    this.navMisses = 0;
    step.finish({ step: { purpose: "restart", mode, outcome: created ? "new_screen" : "known_screen", to: screen.id, findings: compact(findings) },
      ...(frame ? { filePath: frame } : {}) }, logs);
  }

  // -------------------------------------------------------------------------
  // Screens
  // -------------------------------------------------------------------------

  /** The known screen this snapshot shows, or a new one (captured and checked once). */
  private async identify(snap: Snapshot, seq: number, logs: unknown[]):
    Promise<{ screen: Screen; created: boolean; findings: Finding[]; frame?: string }> {
    let best: Screen | null = null, bestScore = -1;
    for (const screen of this.screens) {
      const score = similarity(screen.tokens, snap.tokens);
      if (score > bestScore) { best = screen; bestScore = score; }
    }
    if (best && bestScore >= this.options.sameScreen) {
      best.visits++;
      this.learnActions(best, snap);
      return { screen: best, created: false, findings: [] };
    }
    if (this.screens.length >= this.options.maxScreens) throw new Stop("max_screens");
    const screen: Screen = {
      id: `S${this.screens.length + 1}`, title: screenTitle(snap.nodes), tokens: snap.tokens,
      labels: snap.nodes.filter(n => this.actionable(n)).map(controlLabel).slice(0, 8),
      actions: new Map(), denied: 0, firstStep: this.steps, visits: 1, deadEnd: false,
    };
    this.screens.push(screen);
    this.learnActions(screen, snap);
    const findings = this.addFindings(findingsFromSnapshot(snap.output, { screen: screen.id, step: this.steps }));
    const truncated = truncatedDiagnostics(snap.output);
    if (Object.keys(truncated).length)
      this.notes.push(`${screen.id}: diagnostics listed only their first 20 items (${Object.entries(truncated).map(([k, v]) => `${k} +${v}`).join(", ")}).`);
    let frame: string | undefined;
    if (this.options.capture) {
      const shot = await this.deps.call(SCREENSHOT_TOOL, { view: "composed", max_edge: 960 });
      collectLogs(shot, logs);
      const out = toolOutput(shot);
      if (!shot.isError && !out.error && typeof out.filePath === "string") {
        frame = out.filePath;
        screen.frame = out.filePath;
        screen.frameCall = seq;
      } else this.notes.push(`${screen.id}: the screenshot failed (${(out.error as { code?: string } | undefined)?.code ?? "error"}).`);
    }
    return { screen, created: true, findings, frame };
  }

  /** Register the snapshot's tappable controls as the screen's actions (new ones only; denied ones listed). */
  private learnActions(screen: Screen, snap: Snapshot): void {
    for (const node of snap.nodes) {
      if (!this.actionable(node)) continue;
      const key = actionKey(node);
      if (screen.actions.has(key) || this.denied.has(`${screen.id}|${key}`)) continue;
      const reason = denyReason(node, this.options.policy);
      if (reason) {
        screen.denied++;
        this.denied.set(`${screen.id}|${key}`, { screen: screen.id, label: controlLabel(node), path: node.path, reason });
        continue;
      }
      screen.actions.set(key, { key, label: controlLabel(node), path: node.path, role: node.role, back: node.role !== "input" && isBackLike(node, this.options.memory?.backLabels), tried: false, misses: 0 });
    }
  }

  // -------------------------------------------------------------------------
  // Snapshots
  // -------------------------------------------------------------------------

  private snapshotArgs(full: boolean): Record<string, unknown> {
    if (!full) return { profile: "compact", page_size: 1, visible_only: true };
    return {
      profile: "compact", page_size: 200, visible_only: true,
      ...(this.options.placeholderPattern ? { placeholder_pattern: this.options.placeholderPattern } : {}),
      ...(this.minTouchPx !== null ? { min_touch_size: this.minTouchPx } : {}),
    };
  }

  private async snapshotOnce(full: boolean, logs?: unknown[]): Promise<Snapshot | null> {
    const result = await this.deps.call(SNAPSHOT_TOOL, this.snapshotArgs(full));
    if (logs) collectLogs(result, logs);
    const out = toolOutput(result);
    if (result.isError || out.error) {
      if (full) this.failed(`ui_snapshot failed: ${(out.error as { code?: string } | undefined)?.code ?? "error"}`);
      return null;
    }
    const nodes = Array.isArray(out.nodes) ? out.nodes as SnapshotNode[] : [];
    return { mode: String(out.mode ?? ""), nodes, output: out, tokens: screenSignature(nodes), settled: true };
  }

  /** Snapshot until two in a row show the same screen (or max_settle_ms passes). */
  private async settledSnapshot(logs: unknown[]): Promise<Snapshot> {
    const deadline = this.now() + this.options.maxSettleMs;
    let previous = await this.requireSnapshot(logs);
    for (;;) {
      if (this.now() >= deadline) return { ...previous, settled: false };
      await this.sleep(this.options.settleMs);
      const next = await this.requireSnapshot(logs);
      if (sameTokens(previous.tokens, next.tokens)) return next;
      previous = next;
    }
  }

  private async requireSnapshot(logs: unknown[]): Promise<Snapshot> {
    for (;;) {
      if (this.deps.signal.aborted) throw new Stop("cancelled");
      const snap = await this.snapshotOnce(true, logs);
      if (snap) {
        if (snap.mode !== "play" && !this.restarting) throw new Stop("play_mode_exited", "Play mode ended during the crawl.");
        return snap;
      }
      await this.sleep(this.options.settleMs);
    }
  }

  // -------------------------------------------------------------------------
  // Bookkeeping
  // -------------------------------------------------------------------------

  private begin(label: string, args: Record<string, unknown>): RecordedStep {
    const step = this.deps.recorder.begin(label, args);
    if (!step) throw new Stop("session_ended", "The playtest session ended during the crawl; the crawl stopped with it.");
    return step;
  }

  private failed(message: string): void {
    this.notes.push(message);
    if (++this.failures >= MAX_CONSECUTIVE_FAILURES) throw new Stop("tool_failures", `${MAX_CONSECUTIVE_FAILURES} tool calls failed in a row; the last: ${message}`);
  }

  private addFindings(findings: Finding[]): Finding[] {
    const known = this.options.memory?.known;
    if (!known) return this.findings.addAll(findings);
    const { kept, known: accepted } = known.split(findings);
    for (const f of accepted) this.known.add(f);
    return this.findings.addAll(kept);
  }

  private progress(): void {
    const tried = this.screens.reduce((n, s) => n + [...s.actions.values()].filter(a => a.tried && a.outcome !== "not_tappable").length, 0);
    this.deps.progress?.("exploring", Math.min(1, this.steps / this.options.maxSteps), {
      steps: this.steps, screens: this.screens.length, actions_tried: tried, findings: this.findings.size, restarts: this.restarts,
      current: this.current?.id,
    });
  }

  /** The crawl's outcome; bounded for a jobs result. */
  result(reason: StopReason): Record<string, unknown> {
    let total = 0, tried = 0, untried = 0;
    for (const s of this.screens) for (const a of s.actions.values()) {
      total++;
      if (a.tried && a.outcome !== "not_tappable") tried++; else untried++;
    }
    const findings = sortFindings(this.findings.all);
    const counts = this.findings.counts();
    return {
      status: "ok",
      stop_reason: reason,
      steps: this.steps,
      restarts: this.restarts,
      duration_ms: Math.max(0, this.now() - this.startedAt),
      coverage: { screens: this.screens.length, actions_total: total, actions_tried: tried, untried },
      finding_counts: counts,
      findings: findings.slice(0, 100),
      ...(findings.length > 100 ? { findings_truncated: findings.length - 100 } : {}),
      ...(this.known.size ? { known: this.known.all.slice(0, 50).map(f => knownEntry(f as Finding & KnownMatch)) } : {}),
      screens: this.screens.map(s => ({
        id: s.id, title: s.title, labels: s.labels, visits: s.visits, first_step: s.firstStep,
        actions: s.actions.size, tried: [...s.actions.values()].filter(a => a.tried && a.outcome !== "not_tappable").length, denied: s.denied,
        ...(s.deadEnd ? { dead_end: true } : {}), ...(s.frame ? { frame: s.frame } : {}), ...(s.frameCall !== undefined ? { frame_call: s.frameCall } : {}),
      })),
      edges: this.edges.slice(0, 200),
      denied: [...this.denied.values()].slice(0, 50),
      untried: this.screens.flatMap(s => [...s.actions.values()].filter(a => !a.tried || a.outcome === "not_tappable")
        .map(a => ({ screen: s.id, label: a.label, path: a.path, ...(a.tried ? { reason: "never tappable when visited" } : {}) }))).slice(0, 50),
      ...(this.navFailures.length ? { navigation_failures: this.navFailures.slice(0, 20) } : {}),
      ...(this.notes.length ? { notes: [...new Set(this.notes)].slice(0, 20) } : {}),
      // For the session log only (a replay scenario exported from this crawl): not shown to the agent.
      signatures: Object.fromEntries(this.screens.map(s => [s.id, [...s.tokens].sort()])),
      options: {
        restart: this.options.restart,
        ...(this.options.restartCommand ? { restart_command: this.options.restartCommand } : {}),
        ...(this.options.restartArgs ? { restart_args: this.options.restartArgs } : {}),
        routing: this.options.routing, same_screen: this.options.sameScreen, fill_text: this.options.fillText,
      },
    };
  }
}

/** Keys of a crawl result kept for the session log (a scenario export) but not returned to the agent. */
export const LOG_ONLY_KEYS = ["signatures", "options"] as const;

/** The crawl result as the agent sees it (without the log-only keys). */
export function shownResult(result: Record<string, unknown>): Record<string, unknown> {
  const shown = { ...result };
  for (const key of LOG_ONLY_KEYS) delete shown[key];
  return shown;
}

/**
 * Bring the game back to its start: a synchronous project command (`command`),
 * or exit and re-enter play mode (`play_mode`). Returns why it failed, or null.
 */
export async function restartGame(call: ToolCaller, mode: RestartMode, command: string | undefined,
  commandArgs: Record<string, unknown> | undefined, logs: unknown[]): Promise<{ at: string; error: unknown; message: string } | null> {
  const steps: Array<[string, Record<string, unknown>]> = mode === "command"
    ? [["unity_open_mcp_project_commands", { action: "invoke", command_id: command, args: commandArgs ?? {} }]]
    : [
      ["unity_open_mcp_editor_set_state", { state: "stop" }],
      ["unity_open_mcp_editor_status", { wait_until: "edit", timeout_ms: 30_000 }],
      ["unity_open_mcp_editor_set_state", { state: "play" }],
      ["unity_open_mcp_editor_status", { wait_until: "playing", timeout_ms: 40_000 }],
    ];
  for (const [tool, args] of steps) {
    const result = await call(tool, args);
    collectLogs(result, logs);
    const out = toolOutput(result);
    if (result.isError || out.error) {
      const at = `${tool} ${JSON.stringify(args)}`;
      return {
        at, error: out.error ?? null,
        message: mode === "command"
          ? `The restart command ${command} failed: ${JSON.stringify(out.error ?? null).slice(0, 300)}`
          : `Restarting play mode failed at ${at}: ${JSON.stringify(out.error ?? null).slice(0, 300)}`,
      };
    }
  }
  return null;
}

/** Console lines a tool response carried (`logs[]` at the top of its body). */
export function collectLogs(result: CallToolResult, into: unknown[]): void {
  const logs = toolResultBody(result).logs;
  if (Array.isArray(logs)) for (const log of logs) if (log && typeof log === "object") into.push(log);
}

/** First lines of the error / exception messages among the step's logs. */
function errorLines(logs: unknown[]): string[] {
  const out = new Set<string>();
  for (const log of logs as Array<Record<string, unknown>>)
    if (ERROR_SEVERITIES.has(String(log.severity ?? log.type ?? "").toLowerCase()) && typeof log.message === "string")
      out.add(firstLine(log.message));
  return [...out];
}

/** Findings as a step summary carries them. */
function compact(findings: Finding[]): Array<Record<string, unknown>> {
  return findings.slice(0, 20).map(f => ({ kind: f.kind, severity: f.severity, ...(f.path ? { path: f.path } : {}), ...(f.label ? { label: f.label } : {}), detail: f.detail }));
}
