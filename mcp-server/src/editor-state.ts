// What the Editor is doing right now, read from outside the Editor, and the
// one wait that outlives a compile, a domain reload or a play-mode change.
//
// Three signals, all readable while the bridge's listener is torn down for a
// domain reload:
//   - the instance lock (~/.unity-open-mcp/instances/<hash>.json): heartbeat
//     state (idle / compiling / reloading / entering_playmode / playing /
//     exiting_playmode), isPlaying / isCompiling, and its `compile` block (last
//     generation, failed, errorCount, and `pending`: a compile requested and
//     not started yet);
//   - one /ping probe: reachable, HTTP status (503 = listener up, session not
//     initialized), connected / compiling / isPlaying / transition;
//   - /compile-state, only when the bridge is settled (it needs the main
//     thread): status, generation, sourceMatches, pending.
//
// `bridge_status`, `editor_status` (plain and `wait_until`), `ping` during a
// reload, the router's mid-reload policy and the CLI `wait-for-ready` all read
// the Editor through this module, so they cannot disagree about whether it is
// settled.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import {
  classifyInstance,
  heartbeatAgeMs,
  readInstanceLock,
  type InstanceClassification,
  type InstanceLock,
  type LockCompile,
} from "./instance-discovery.js";
import type { PingProbe } from "./live-client.js";
import { bridgeCompileFailedBody, readBridgeLossEvidence, type BridgeLossEvidence } from "./bridge-loss.js";

export const WAIT_TOOL = "unity_open_mcp_editor_status";
export type WaitUntil = "idle" | "playing" | "edit";
export const WAIT_UNTIL_VALUES: readonly WaitUntil[] = ["idle", "playing", "edit"];

/** The canonical wait call an agent is told to make (mirrors the bridge's BridgeJson.WaitCall). */
export function waitCall(until: WaitUntil, afterGeneration?: number | null): string {
  const args: Record<string, unknown> = { wait_until: until };
  if (typeof afterGeneration === "number" && Number.isInteger(afterGeneration) && afterGeneration >= 0)
    args.after_generation = afterGeneration;
  return `${WAIT_TOOL} ${JSON.stringify(args)}`;
}

export type EditorTransition = "compiling" | "reloading" | "entering_playmode" | "exiting_playmode";
const TRANSITIONS: ReadonlySet<string> = new Set(["compiling", "reloading", "entering_playmode", "exiting_playmode"]);

/** A heartbeat state that names a transition in progress. */
export function isTransitionState(state: string | null | undefined): boolean {
  return typeof state === "string" && TRANSITIONS.has(state);
}

export interface EditorSignals {
  /** When the signals were read (ms since epoch). */
  at: number;
  lock: InstanceLock | null;
  classification: InstanceClassification;
  ping: PingProbe;
  /**
   * Read only when the heartbeat stopped and the listener is gone: the
   * Editor.log evidence that the bridge cannot come back on its own
   * (bridge-loss.ts), or null when the log shows none — then the stop is a
   * long reload or a stall, not a dead bridge.
   */
  bridgeLoss?: BridgeLossEvidence | null;
}

/** The part of a LiveClient the reader needs (tests pass a fake). */
export interface PingSource {
  pingProbe(fetchTimeoutMs?: number): Promise<PingProbe>;
}

/** Read the lock and probe /ping once. Never throws. `logPathOverride` is a test seam for the global Editor.log. */
export async function readEditorSignals(
  projectPath: string | undefined,
  live: PingSource,
  pingTimeoutMs = 2_000,
  logPathOverride?: string,
): Promise<EditorSignals> {
  let lock: InstanceLock | null = null;
  if (projectPath) {
    try { lock = readInstanceLock(projectPath); } catch { lock = null; }
  }
  let ping: PingProbe;
  try { ping = await live.pingProbe(pingTimeoutMs); }
  catch { ping = { reachable: false, httpStatus: null, body: null }; }
  const classification = classifyInstance(lock);
  const bridgeLoss = classification === "dead_bridge" && !ping.reachable
    ? readBridgeLossEvidence(projectPath, { livePid: lock?.pid, globalLogPathOverride: logPathOverride })
    : undefined;
  return { at: Date.now(), lock, classification, ping, ...(bridgeLoss !== undefined ? { bridgeLoss } : {}) };
}

/**
 * - `settled`: the bridge answers, its session is connected, nothing is
 *   compiling, reloading or changing play mode.
 * - `transition`: a compile, domain reload or play-mode change is in progress
 *   (or the listener is still initializing after one); it ends on its own.
 * - `unreachable`: Unity runs but the listener does not answer and nothing says
 *   a reload is in progress (toolbar off, a blip, or a heartbeat that stopped
 *   outside a transition with no evidence of why).
 * - `wedged`: the heartbeat stopped but /ping answers — the main thread is
 *   blocked (a modal dialog, or a long synchronous import).
 * - `dead_bridge`: the heartbeat stopped, the listener is gone, and Editor.log
 *   shows why the bridge cannot come back (Safe Mode, or a reload that
 *   finished without it — bridge-loss.ts). A stopped heartbeat alone is a
 *   long transition, never a dead bridge.
 * - `gone`: no live Editor for this project.
 */
export type EditorPhase = "settled" | "transition" | "unreachable" | "wedged" | "dead_bridge" | "gone";

export interface EditorView {
  phase: EditorPhase;
  transition: EditorTransition | null;
  /** Heartbeat state from the lock (null without a live lock). */
  state: string | null;
  isPlaying: boolean | null;
  isCompiling: boolean;
  /** The lock's compile block (null on an older bridge or without a lock). */
  compile: LockCompile | null;
  heartbeatAt: string | null;
  /** How long the heartbeat has been stopped (ms), when it is stale; null when it beats. */
  stalledMs: number | null;
  /** Why the bridge is down for good — present only on `dead_bridge`. */
  bridgeLoss?: BridgeLossEvidence;
}

/**
 * Fold the signals into one view. Any source that reports a transition wins
 * (the lock lags /ping by up to one 0.5 s heartbeat and vice versa), so the
 * view errs toward "not settled yet". A stopped heartbeat is a dead bridge only
 * with Editor.log evidence; otherwise it is a transition that runs long (a
 * large project's reload, a play-mode reload, a test run's reload — the
 * `pendingTestRun` signal) or, outside a transition, an unreachable listener.
 */
export function viewEditor(s: EditorSignals, pendingTestRun = false): EditorView {
  const lockLive = s.lock !== null && s.classification !== "gone";
  const lock = lockLive ? s.lock : null;
  const body = s.ping.reachable ? s.ping.body : null;
  const compile = lock?.compile ?? null;
  let transition: EditorTransition | null = null;
  const note = (t: string | null | undefined) => {
    if (!transition && t && TRANSITIONS.has(t)) transition = t as EditorTransition;
  };
  note(lock?.state);
  if (s.ping.reachable && s.ping.httpStatus === 503) note("reloading");
  if (body?.compiling) note("compiling");
  note(body?.transition ?? null);
  if (compile?.running) note("compiling");

  const stale = s.classification === "dead_bridge";
  const base = {
    state: lock?.state ?? null,
    isPlaying: typeof body?.isPlaying === "boolean" ? body.isPlaying : lock ? lock.isPlaying === true : null,
    isCompiling: body?.compiling === true || lock?.isCompiling === true || transition === "compiling",
    compile,
    heartbeatAt: lock?.heartbeatAt ?? null,
    stalledMs: stale && lock && Number.isFinite(heartbeatAgeMs(lock, s.at)) ? Math.round(heartbeatAgeMs(lock, s.at)) : null,
  };
  const view = (phase: EditorPhase, t: EditorTransition | null = transition): EditorView => ({ ...base, phase, transition: t });

  if (stale) {
    if (!s.ping.reachable && s.bridgeLoss) return { ...view("dead_bridge"), bridgeLoss: s.bridgeLoss };
    if (pendingTestRun) return view("transition", transition ?? "reloading");
    if (s.ping.reachable) return view("wedged");
    return view(transition ? "transition" : "unreachable");
  }
  if (!s.ping.reachable) {
    if (!lockLive) return view("gone");
    if (transition) return view("transition");
    return view("unreachable");
  }
  if (s.ping.httpStatus === 503 || body?.connected === false) return view("transition", transition ?? "reloading");
  if (transition) return view("transition");
  return view("settled");
}

/** Lock-derived fields every status answer carries (editor_status, ping during a reload). */
export function statusFields(v: EditorView): Record<string, unknown> {
  return {
    state: v.state,
    transition: v.transition,
    reloading: v.transition !== null || v.phase === "transition",
    heartbeatAt: v.heartbeatAt,
    compileGeneration: v.compile?.generation ?? null,
    compilePending: v.compile?.pending ?? null,
    lastCompileFailed: v.compile ? v.compile.failed : null,
    // The heartbeat stopped this long ago: a long reload, a busy main thread,
    // or (with `evidence`) a bridge that is not coming back.
    ...(v.stalledMs !== null ? { heartbeatStalledMs: v.stalledMs } : {}),
  };
}

/** How long to suggest before retrying a call refused during a transition. */
export function retryAfterMsFor(v: EditorView): number {
  return v.transition === "entering_playmode" || v.transition === "exiting_playmode" ? 1_000 : 2_000;
}

// ---------------------------------------------------------------------------
// The wait
// ---------------------------------------------------------------------------

/** A compile requested and not started gets this long (from its request) to start. */
export const PENDING_COMPILE_GRACE_MS = 60_000;
/** `after_generation` with nothing pending: how long a late compile start is awaited. */
export const AFTER_GENERATION_GRACE_MS = 10_000;
/** Sources newer than the last compile, nothing compiling: time for an auto-refresh to start one. */
export const STALE_SOURCES_GRACE_MS = 5_000;
/** Right after a reload the fingerprint cache is cold (`indeterminate`) for a second or two. */
export const INDETERMINATE_GRACE_MS = 5_000;
/** Settled in the other play mode for this long → the requested mode is not coming. */
export const MODE_GRACE_MS = 3_000;
/** No live Editor for this long → give up. */
export const GONE_GRACE_MS = 5_000;
/** Consecutive settled polls required, so a half-reloaded Editor between two transitions never reads as idle. */
export const SETTLED_STREAK = 2;
export const WAIT_POLL_MS = 250;
/** How long one editor_status call waits inline before it continues as a job. */
export const WAIT_INLINE_LIMIT_MS = 40_000;
export const WAIT_DEFAULT_TIMEOUT_MS = 120_000;
export const WAIT_MAX_TIMEOUT_MS = 600_000;

/** One /compile-state reading (or read_compile_errors' summary of it). */
export interface CompileReading {
  status: string;
  generation: number;
  errorCount: number;
  sourceMatches?: boolean;
  pending?: boolean;
  errorSource?: string;
  headline?: string;
  /** The first few diagnostics of a failed compile (file, line, code, message). */
  errors?: Array<Record<string, unknown>>;
}

export interface WaitRequest {
  until: WaitUntil;
  /** Require a compile that started after this generation (or none being due). */
  afterGeneration?: number;
  timeoutMs: number;
  pollMs?: number;
}

export interface WaitDeps {
  signals(): Promise<EditorSignals>;
  /** Cheap reading for the loop; null when /compile-state did not answer. */
  compileState(): Promise<CompileReading | null>;
  /** Final reading, the one read_compile_errors would give; defaults to compileState. */
  compileSummary?(): Promise<CompileReading | null>;
  pendingTestRun?(): boolean;
  now?(): number;
  sleep?(ms: number): Promise<void>;
  signal?: AbortSignal;
  onProgress?(phase: string): void;
}

export type WaitReason = "reached" | "timeout" | "not_reached" | "dead_bridge" | "gone" | "aborted";

export interface WaitOutcome {
  reached: boolean;
  reason: WaitReason;
  waitedMs: number;
  view: EditorView | null;
  compile: CompileReading | null;
  transitionsSeen: string[];
  /** after_generation given: did a compile after it run? */
  compileRan: boolean | null;
  /** A requested compile never started within its grace window. */
  compileDueNotStarted: boolean;
  /** What the wait was holding for when it gave up (timeout). */
  holdingFor: string | null;
}

const defaultSleep = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms));

/**
 * Poll until the Editor is settled in the requested mode with no compile due.
 * Survives the domain reload it waits for: every signal is read from outside
 * the Editor, and a refused / unreachable probe is just "not settled yet".
 *
 * The handshake: the gap between a refresh (or recompile request) and the
 * compile actually starting looks idle — `isCompiling` can read false for
 * seconds. The bridge records such a request (`pending`, cleared when the next
 * compile starts) in the lock and /compile-state, and the wait holds while it
 * is set (up to {@link PENDING_COMPILE_GRACE_MS} from the request). With
 * `afterGeneration` the wait also requires a compile after that generation,
 * unless nothing is due after {@link AFTER_GENERATION_GRACE_MS}.
 */
export async function waitForEditor(req: WaitRequest, deps: WaitDeps): Promise<WaitOutcome> {
  const now = deps.now ?? Date.now;
  const sleep = deps.sleep ?? defaultSleep;
  const pollMs = req.pollMs ?? WAIT_POLL_MS;
  const start = now();
  const seen = new Set<string>();
  let streak = 0;
  let view: EditorView | null = null;
  let compile: CompileReading | null = null;
  let compileDueNotStarted = false;
  let holdingFor: string | null = null;
  let pendingSince: number | null = null;
  let afterGenSince: number | null = null;
  let staleSince: number | null = null;
  let indeterminateSince: number | null = null;
  let modeSince: number | null = null;
  let goneSince: number | null = null;

  // Whether this wait is about a compile: an idle wait, a wait past a
  // generation, or any wait that saw a compile run or get requested. Only then
  // do changed-but-uncompiled sources and a still-cold compile reading hold it.
  let compileSeen = false;
  const compileMatters = () => req.until === "idle" || req.afterGeneration !== undefined || compileSeen || seen.has("compiling");

  const finish = async (reason: WaitReason): Promise<WaitOutcome> => {
    const settled = view?.phase === "settled";
    if (settled) {
      try { compile = (await (deps.compileSummary ?? deps.compileState)()) ?? compile; } catch { /* keep the loop's reading */ }
    }
    const generation = compile?.generation ?? view?.compile?.generation;
    return {
      reached: reason === "reached",
      reason,
      waitedMs: Math.max(0, now() - start),
      view,
      compile,
      transitionsSeen: [...seen],
      compileRan: req.afterGeneration === undefined || typeof generation !== "number" ? null : generation > req.afterGeneration,
      compileDueNotStarted,
      holdingFor: reason === "reached" ? null : holdingFor,
    };
  };

  while (true) {
    if (deps.signal?.aborted) return finish("aborted");
    const signals = await deps.signals();
    view = viewEditor(signals, deps.pendingTestRun?.() ?? false);
    holdingFor = null;

    if (view.phase === "dead_bridge") return finish("dead_bridge");
    if (view.phase === "gone") {
      goneSince ??= now();
      streak = 0;
      holdingFor = "editor_gone";
      if (now() - goneSince >= GONE_GRACE_MS) return finish("gone");
    } else if (view.phase !== "settled") {
      goneSince = null;
      streak = 0;
      modeSince = null;
      if (view.transition) seen.add(view.transition);
      if (view.phase === "wedged") seen.add("main_thread_wedged");
      holdingFor = view.transition ?? view.phase;
      deps.onProgress?.(holdingFor);
    } else {
      goneSince = null;
      const reading = await deps.compileState();
      if (!reading) {
        // The main thread did not answer /compile-state in time: busy, not settled.
        streak = 0;
        holdingFor = "main_thread_busy";
      } else {
        compile = reading;
        const t = now();
        let hold: string | null = null;
        const pending = reading.pending === true || view.compile?.pending === true;
        if (pending) compileSeen = true;
        if (reading.status === "currently_compiling") {
          seen.add("compiling");
          hold = "compiling";
        } else if (pending) {
          const requestedAt = view.compile?.requestedAt ? Date.parse(view.compile.requestedAt) : NaN;
          const since = Number.isFinite(requestedAt) ? Math.min(requestedAt, pendingSince ??= t) : (pendingSince ??= t);
          if (t - since < PENDING_COMPILE_GRACE_MS) hold = "compile_requested";
          else compileDueNotStarted = true;
        } else if (req.afterGeneration !== undefined && reading.generation <= req.afterGeneration) {
          afterGenSince ??= t;
          if (t - afterGenSince < AFTER_GENERATION_GRACE_MS) hold = "compile_after_generation";
        } else if (!compileMatters()) {
          // A play-mode wait with no compile in sight: the compile reading
          // is reported, not waited for.
        } else if (reading.status === "assembly_stale" || (reading.status === "compile_failed" && reading.sourceMatches === false)) {
          staleSince ??= t;
          if (t - staleSince < STALE_SOURCES_GRACE_MS) hold = "sources_changed";
        } else if (reading.status === "indeterminate") {
          indeterminateSince ??= t;
          if (t - indeterminateSince < INDETERMINATE_GRACE_MS) hold = "compile_state_warming";
        }
        if (!pending) pendingSince = null;
        if (reading.status !== "assembly_stale" && reading.status !== "compile_failed") staleSince = null;
        if (reading.status !== "indeterminate") indeterminateSince = null;

        if (!hold) {
          const modeOk = req.until === "idle" || (req.until === "playing") === (view.isPlaying === true);
          if (modeOk) modeSince = null;
          else {
            modeSince ??= t;
            if (t - modeSince >= MODE_GRACE_MS) return finish("not_reached");
            hold = req.until === "playing" ? "play_mode" : "edit_mode";
          }
        }
        if (hold) {
          streak = 0;
          holdingFor = hold;
          deps.onProgress?.(hold);
        } else if (++streak >= SETTLED_STREAK) {
          return finish("reached");
        }
      }
    }
    if (now() - start >= req.timeoutMs) return finish("timeout");
    await sleep(Math.max(0, Math.min(pollMs, req.timeoutMs - (now() - start))));
  }
}

/** The tool-facing body of a finished wait (inline or as a job result). */
export function describeWait(req: WaitRequest, outcome: WaitOutcome): { body: Record<string, unknown>; isError: boolean } {
  const v = outcome.view;
  const c = outcome.compile;
  const generation = c?.generation ?? v?.compile?.generation ?? null;
  const compile: Record<string, unknown> = {
    generation,
    status: c?.status ?? null,
    errorCount: c?.errorCount ?? v?.compile?.errorCount ?? null,
    ...(c?.errorSource ? { errorSource: c.errorSource } : {}),
    ...(outcome.compileRan !== null ? { ranAfterGeneration: outcome.compileRan } : {}),
    ...(outcome.compileDueNotStarted ? { dueButNotStarted: true } : {}),
  };
  const steps: string[] = [];
  const mode = v?.isPlaying === true ? "playing" : v?.isPlaying === false ? "edit" : null;
  const base: Record<string, unknown> = {
    waitUntil: req.until,
    ...(req.afterGeneration !== undefined ? { afterGeneration: req.afterGeneration } : {}),
    reached: outcome.reached,
    status: outcome.reached ? req.until : outcome.reason,
    waitedMs: outcome.waitedMs,
    mode,
    isPlaying: v?.isPlaying ?? null,
    isCompiling: v?.isCompiling ?? null,
    state: v?.state ?? null,
    transitionsSeen: outcome.transitionsSeen,
    compile,
  };

  if (c?.status === "compile_failed") {
    steps.push(`The compile failed with ${c.errorCount} error(s). Call unity_open_mcp_read_compile_errors for the diagnostics, fix them, then refresh and wait again.`);
  } else if (c?.status === "assembly_stale") {
    steps.push(`Script sources on disk are newer than the last compile and no compile started. Run unity_open_mcp_assets_refresh (or recompile_scripts), then ${waitCall("idle", generation)}.`);
  }
  if (outcome.compileDueNotStarted)
    steps.push(`A compile was requested but did not start within ${PENDING_COMPILE_GRACE_MS / 1000} s; Unity may have found nothing to compile. Check unity_open_mcp_read_compile_errors before relying on new code.`);
  if (outcome.compileRan === false && outcome.reached)
    steps.push(`No compile ran after generation ${req.afterGeneration}: nothing was due. If you expected one, run unity_open_mcp_assets_refresh and wait again.`);

  if (outcome.reached) return { body: { ...base, agentNextSteps: steps }, isError: false };

  let error: { code: string; message: string };
  switch (outcome.reason) {
    case "not_reached":
      error = {
        code: "wait_not_reached",
        message: `The Editor settled in ${mode ?? "another"} mode, not '${req.until}'.` +
          (req.until === "playing" && c?.status === "compile_failed" ? " Unity does not enter play mode while scripts have compile errors." : ""),
      };
      steps.push(req.until === "playing"
        ? "Check the compile result and the Console (unity_open_mcp_read_console), then call unity_open_mcp_editor_set_state {\"state\":\"play\"} again."
        : "Call unity_open_mcp_editor_set_state with the state you need, then wait again.");
      break;
    case "dead_bridge": {
      // Only reached with Editor.log evidence (viewEditor): a stopped heartbeat
      // alone keeps the wait going as a transition.
      const failed = v?.bridgeLoss ? bridgeCompileFailedBody(v.bridgeLoss) : null;
      error = (failed?.error as { code: string; message: string } | undefined)
        ?? { code: "bridge_compile_failed", message: "The bridge did not come back after the reload." };
      steps.push("Call unity_open_mcp_read_compile_errors (it reads Editor.log without the bridge), fix the errors, then recompile.");
      if (v?.bridgeLoss) base.evidence = v.bridgeLoss;
      break;
    }
    case "gone":
      error = { code: "bridge_offline", message: "No live Unity Editor for this project (no instance lock with a running process)." };
      steps.push("Open the project in Unity, then call unity_open_mcp_bridge_status to confirm the bridge is running.");
      break;
    case "aborted":
      error = { code: "wait_cancelled", message: "The wait was cancelled." };
      break;
    default:
      error = { code: "wait_timeout", message: `The Editor was not ${req.until} after ${outcome.waitedMs} ms` + (outcome.holdingFor ? ` (still ${outcome.holdingFor.replace(/_/g, " ")}).` : ".") +
        (v?.stalledMs != null ? ` Its heartbeat stopped ${Math.round(v.stalledMs / 1000)} s ago and Editor.log shows no compile failure: a long reload or a blocked main thread, not a dead bridge.` : "") };
      steps.push(`Wait again: ${waitCall(req.until, req.afterGeneration)} with a larger timeout_ms (up to ${WAIT_MAX_TIMEOUT_MS}; past ~${WAIT_INLINE_LIMIT_MS / 1000} s it continues as a job). If it never settles, check unity_open_mcp_bridge_status.`);
  }
  return { body: { ...base, ...(outcome.holdingFor ? { holdingFor: outcome.holdingFor } : {}), error, agentNextSteps: steps }, isError: true };
}

export type LostResponseCause = "timeout" | "connection_closed";

/**
 * A tool call whose response never arrived — the connection closed, or nothing
 * came back within the call's deadline — and that no Editor.log evidence
 * explains as a dead bridge. The request reached the bridge; whether it ran is
 * unknown (`dispatched: null`), so it is never replayed automatically.
 * `editor_reloading` when the Editor is compiling, reloading or changing play
 * mode (or its heartbeat stopped inside one), `response_lost` otherwise. Both
 * are retryable.
 */
export function lostResponseResult(toolName: string, v: EditorView | null, cause: LostResponseCause, waitedMs?: number): CallToolResult {
  const reloading = v !== null && (v.phase === "transition" || v.transition !== null);
  const what = cause === "timeout" ? "no response arrived before the call's deadline" : "the connection closed before the response arrived";
  const body = {
    error: {
      code: reloading ? "editor_reloading" : "response_lost",
      message: reloading
        ? `'${toolName}' reached the bridge, but ${what}: the Editor is ${String(v?.transition ?? "reloading").replace(/_/g, " ")}. ` +
          "The call may or may not have run."
        : `'${toolName}' reached the bridge, but ${what}` +
          (v?.phase === "wedged" ? " (the Editor's main thread is blocked)" : v?.phase === "unreachable" ? " (the listener is not answering now)" : "") +
          ". The call may or may not have run; Editor.log shows no compile failure, so this is not a dead bridge.",
    },
    dispatched: null,
    cause,
    transition: v?.transition ?? null,
    phase: v?.phase ?? null,
    ...(waitedMs !== undefined ? { waitedMs } : {}),
    retryAfterMs: v ? retryAfterMsFor(v) : 2_000,
    ...(v ? statusFields(v) : {}),
    agentNextSteps: [
      `Wait for the Editor with ${waitCall("idle")}, then send a read again; for a mutation, check its effect with the matching read tool before repeating it.`,
    ],
    _route: { route: "live", outcome: reloading ? "editor_reloading" : "response_lost" },
  };
  return { content: [{ type: "text", text: JSON.stringify(body) }], isError: true };
}

/** A tool result for a call the router did not send because the Editor did not settle in time. */
export function reloadingRefusal(toolName: string, v: EditorView | null, waitedMs: number): CallToolResult {
  const transition = v?.transition ?? (v?.phase === "settled" ? null : v?.phase ?? "reloading");
  const body = {
    error: {
      code: "editor_reloading",
      message: `The live Editor is still ${String(transition ?? "settling").replace(/_/g, " ")} after ${waitedMs} ms; '${toolName}' was not sent.`,
    },
    dispatched: false,
    transition,
    waitedMs,
    retryAfterMs: v ? retryAfterMsFor(v) : 2_000,
    ...(v ? statusFields(v) : {}),
    agentNextSteps: [
      `Wait for the Editor with ${waitCall("idle")} (it outlives the reload and continues as a job past ~${WAIT_INLINE_LIMIT_MS / 1000} s), then send the call again.`,
    ],
    _source: "local",
    _route: { route: "local", outcome: "editor_reloading" },
  };
  return { content: [{ type: "text", text: JSON.stringify(body) }], isError: true };
}
