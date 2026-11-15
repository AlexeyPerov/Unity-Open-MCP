import { setTimeout as delay } from "node:timers/promises";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { ProjectJobHttpError, type LiveClient } from "../live-client.js";
import { validateSchema } from "../tool-contract.js";
import { runTests } from "../tools/run-tests.js";
import { inputsimGesture } from "../tools/inputsim-gesture.js";
import { playtestWait, PLAYTEST_WAIT_DEFAULT_TIMEOUT_MS } from "../tools/playtest-wait.js";
import { captureSeries, CAPTURE_SERIES_DEFAULT_START_TIMEOUT_MS } from "../tools/capture-series.js";
import { JobManagerError, type JobOperation, type JobOutcome, type JobOwner } from "./job-manager.js";
import { PLAYMODE_ROUTES } from "../playmode-routes.js";
import { editorStatus } from "../tools/editor-status.js";
import { describeWait, waitForEditor, WAIT_DEFAULT_TIMEOUT_MS, type WaitDeps, type WaitRequest, type WaitUntil } from "../editor-state.js";

type ClientFor = (owner: JobOwner) => LiveClient;
function invalid(errors: string[]): void {
  if (errors.length) throw new JobManagerError("invalid_arguments", errors.join("; "));
}
export function testRunOperation(client: ClientFor): JobOperation {
  return {
    // A test run has side effects even though the legacy tool is classified as a sense.
    mutating: true, cancellable: false,
    validate(args) {
      invalid(validateSchema(args, runTests.inputSchema));
      if (args.run_id !== undefined) throw new JobManagerError("invalid_arguments", "Jobs own run_id; use idempotency_key to retry.");
    },
    async run(args, context) {
      context.report({ phase: "starting test run" });
      const response = await client(context.owner).runTestsJob({ ...args, run_id: context.jobId, timeout_ms: args.timeout_ms ?? 600000 }, () => {
        context.lifecycle("connected", `Test results owned by run id ${context.jobId}`, true);
        context.report({ phase: "awaiting test result file" });
      });
      const text = response.content.find(item => item.type === "text");
      const result = text?.type === "text" ? JSON.parse(text.text) : {};
      if (result.error?.code === "test_results_timeout" || result.error?.code === "timeout") {
        context.lifecycle("disconnected", "Test result observation budget expired; run may still be executing.", false);
      }
      if (response.isError || result.status === "aborted" || result.summary?.failed > 0)
        return { state: "failed", error: { code: result.error?.code ?? "test_run_failed", message: result.error?.message ?? "Test run did not pass." }, result };
      if (result.status !== "completed" || result.runId !== context.jobId || !result.summary)
        throw new Error("Test result did not confirm terminal ownership.");
      context.report({ phase: "test results received" });
      return { state: "succeeded", result };
    },
  };
}

/** Bound the bridge status poll so a hung async command cannot own the project queue forever. */
export const PROJECT_JOB_DEFAULT_DEADLINE_MS = 30 * 60_000;
/** How long the bridge may leave status polls unanswered before the job's outcome counts as unknown. */
export const PROJECT_JOB_DEFAULT_UNREACHABLE_MS = 5 * 60_000;
export interface ProjectJobPollOptions { deadlineMs?: number; pollIntervalMs?: number; unreachableMs?: number }

export function projectCommandOperation(id: string, cancellable: boolean, client: ClientFor, poll: ProjectJobPollOptions = {}): JobOperation {
  const deadlineMs = poll.deadlineMs ?? PROJECT_JOB_DEFAULT_DEADLINE_MS;
  const pollIntervalMs = poll.pollIntervalMs ?? 500;
  const unreachableMs = poll.unreachableMs ?? PROJECT_JOB_DEFAULT_UNREACHABLE_MS;
  return {
    mutating: true, cancellable,
    validate(args) {
      const allowed = ["args", "schema_version", "paths_hint", "gate", "ignore_scene_dirty", "confirm_bypass"];
      invalid(Object.keys(args).filter(key => !allowed.includes(key)).map(key => `Unknown project job option: ${key}`));
    },
    async run(args, context) {
      const live = client(context.owner);
      context.report({ phase: "preflight" });
      const catalog = await live.projectCommands({ action: "describe", id });
      const command = catalog.command as Record<string, any> | undefined;
      if (!command?.available || !command.async || command.cancellable !== cancellable)
        return { state: "failed", error: { code: "command_unavailable", message: "Describe the command again; async contract is unavailable or changed." } };
      const errors = validateSchema(args.args ?? {}, command.inputSchema);
      if (errors.length) return { state: "failed", error: { code: "invalid_arguments", message: errors.join("; ") } };
      let response = await live.projectCommandJob({ action: "start", job_id: context.jobId,
        invocation: { ...args, action: "invoke", command_id: id, schema_version: args.schema_version ?? command.schemaVersion } });
      let cancelSent = false;
      let phase: string | undefined;
      let ownershipConfirmed = false;
      const startedAt = Date.now();
      while (true) {
        if (response.error || response.mutation?.success === false) {
          if (response.error?.code === "job_not_found") context.lifecycle("disconnected", "Bridge lost domain-local job ownership.", false);
          return { state: "failed", error: { code: response.error?.code ?? response.mutation?.error?.code ?? "command_rejected", message: response.error?.message ?? response.mutation?.error?.message ?? "Bridge rejected command." }, result: response };
        }
        if (response.job_id !== context.jobId) throw new Error("Bridge did not confirm job ownership");
        if (!ownershipConfirmed) {
          context.lifecycle("connected", `Bridge owns job ${context.jobId}`, true);
          ownershipConfirmed = true;
        }
        if (phase !== response.phase) { phase = response.phase; context.report({ phase: response.phase }); }
        if (response.state === "succeeded") return { state: "succeeded", result: response.result };
        if (response.state === "cancelled") return { state: "cancelled", result: response.result };
        if (response.state === "failed") return { state: "failed", error: { code: "command_failed", message: "See terminal gate/command result." }, result: response.result };
        // A poll only observes work the bridge keeps running. A main thread busy
        // past the bridge's dispatch timeout (an import, synchronous project code)
        // answers HTTP 500 or not at all, so an unanswered poll is retried with
        // backoff. A cancel is not resent: it may still be queued, and the status
        // polls after it show whether it was acknowledged.
        const answeredAt = Date.now();
        let failures = 0;
        while (true) {
          if (Date.now() - startedAt >= deadlineMs) {
            // The bridge never reported a terminal state. Ask it to stop when the
            // contract allows, then release this job's execution slot as orphaned:
            // the remote outcome is unknown, so a blind retry is not safe.
            if (cancellable && !cancelSent) {
              cancelSent = true;
              try { await live.projectCommandJob({ action: "cancel", job_id: context.jobId }); } catch { /* best effort */ }
            }
            context.lifecycle("disconnected", `Bridge job reported no terminal state within ${deadlineMs} ms; outcome unknown.`, false);
            return { state: "failed", error: { code: "job_deadline_exceeded", message: `No terminal state within ${deadlineMs} ms.` }, result: response };
          }
          if (failures && Date.now() - answeredAt >= unreachableMs) {
            context.lifecycle("disconnected", `Bridge answered no job poll for ${unreachableMs} ms; outcome unknown.`, false);
            return { state: "failed", error: { code: "job_status_unavailable", message: `No job status within ${unreachableMs} ms.` }, result: response };
          }
          // The backoff yields to a cancel request: an abort while sleeping
          // ends the sleep so the cancel goes out now, not after the backoff.
          // Once the cancel has been sent the signal stays aborted, so the
          // remaining status polls sleep without it.
          try {
            await delay(pollIntervalMs * 2 ** Math.min(failures, 3), undefined,
              cancelSent ? undefined : { signal: context.signal });
          } catch { /* aborted: forward the cancel below */ }
          const action = context.signal.aborted && !cancelSent ? "cancel" : "status";
          if (action === "cancel") cancelSent = true;
          try {
            response = await live.projectCommandJob({ action, job_id: context.jobId });
            break;
          } catch (error) {
            // A 4xx is the bridge refusing the transport itself; retrying cannot help.
            if (error instanceof ProjectJobHttpError && error.status < 500) throw error;
            if (failures++ === 0) {
              // Ownership was confirmed by the last answer; the next answer reconfirms it.
              ownershipConfirmed = false;
              context.lifecycle("disconnected", `Job poll failed (${String(error).slice(0, 200)}); retrying while the bridge owns job ${context.jobId}.`, true);
            }
          }
        }
      }
    },
  };
}

// ---------------------------------------------------------------------------
// Play-mode operations — one generic adapter for every tool whose work runs on
// the bridge's shared play-mode scheduler (a player-loop tick that owns timing,
// cancellation and reload / exit-play cleanup). The tool call starts the
// operation with the job id as its operation_id and returns at once; status and
// cancel go through POST /playmode-operations. Records are domain-local: a
// reload drops them (after releasing held input), which surfaces here as
// `operation_not_found` and orphans the job — never a restart.
// ---------------------------------------------------------------------------

/** Tools served by the generic `playmode_operation` jobs adapter. */
export const PLAYMODE_OPERATION_TOOLS: ReadonlyMap<string, Record<string, any>> = new Map([
  [inputsimGesture.name, inputsimGesture.inputSchema as Record<string, any>],
  [playtestWait.name, playtestWait.inputSchema as Record<string, any>],
  [captureSeries.name, captureSeries.inputSchema as Record<string, any>],
]);

/** Encoding cost assumed per captured frame (PNG encode of a native frame) for duration estimates. */
export const PLAYMODE_ENCODE_ESTIMATE_MS = 250;

const numbers = (v: unknown): number[] => Array.isArray(v) ? v.map(Number).filter(Number.isFinite) : [];

/**
 * Worst-case duration (ms) of a play-mode operation from its arguments: the
 * gesture's last keyframe or capture, a wait's timeout, a series' start_on
 * wait plus its schedule (frames at a conservative 30 fps) plus encoding.
 * Decides inline-vs-job and sizes the job deadline.
 */
export function playmodeEstimateMs(tool: string, args: Record<string, unknown>): number {
  if (tool === inputsimGesture.name) {
    const fingers = Array.isArray(args.fingers) ? args.fingers as Array<{ path?: Array<{ at_ms?: unknown }> }> : [];
    const keyframes = fingers.flatMap(f => Array.isArray(f?.path) ? f.path.map(k => Number(k?.at_ms) || 0) : []);
    const captures = numbers(args.capture_at_ms);
    return Math.max(0, ...keyframes, ...captures) + captures.length * PLAYMODE_ENCODE_ESTIMATE_MS;
  }
  if (tool === playtestWait.name) return typeof args.timeout_ms === "number" ? args.timeout_ms : PLAYTEST_WAIT_DEFAULT_TIMEOUT_MS;
  if (tool === captureSeries.name) {
    const atMs = numbers(args.at_ms), atFrames = numbers(args.at_frames);
    const every = Number(args.every_ms), duration = Number(args.duration_ms);
    let last = 0, count = 0;
    if (atMs.length) { last = Math.max(...atMs); count = atMs.length; }
    else if (atFrames.length) { last = Math.max(...atFrames) * 1000 / 30; count = atFrames.length; }
    else if (every > 0 && duration >= 0) { last = duration; count = Math.floor(duration / every) + 1; }
    const trigger = args.start_on === undefined ? 0
      : typeof args.start_timeout_ms === "number" ? args.start_timeout_ms : CAPTURE_SERIES_DEFAULT_START_TIMEOUT_MS;
    return trigger + last + count * PLAYMODE_ENCODE_ESTIMATE_MS;
  }
  return 0;
}

/** Wall-clock tail after an operation's schedule (encoding its captures), for the remaining-time estimate. */
export function playmodeTailMs(tool: string, args: Record<string, unknown>): number {
  if (tool === inputsimGesture.name) return numbers(args.capture_at_ms).length * PLAYMODE_ENCODE_ESTIMATE_MS;
  if (tool === captureSeries.name) {
    const atMs = numbers(args.at_ms), atFrames = numbers(args.at_frames);
    const every = Number(args.every_ms), duration = Number(args.duration_ms);
    const count = atMs.length || atFrames.length || (every > 0 && duration >= 0 ? Math.floor(duration / every) + 1 : 0);
    return count * PLAYMODE_ENCODE_ESTIMATE_MS;
  }
  return 0;
}

/** Observed span (wall ms) of progress before its rate is trusted for a decision. */
export const PLAYMODE_RATE_MIN_SPAN_MS = 1_000;

/** What the observed progress of a running play-mode operation says about its end. */
export interface PlaymodeEstimate {
  /** The operation's clock (`gameplay` / `realtime`). */
  clock: string | null;
  phase: string | null;
  /** progress.elapsedMs / expectedMs of the last snapshot (operation-clock ms). */
  elapsedMs: number;
  expectedMs: number;
  /** Operation-clock ms per wall-clock ms since progress began (null until measurable). */
  rate: number | null;
  /** Wall-clock ms until the operation should end, encoding included (null while unknown). */
  etaMs: number | null;
  /** Wall-clock ms the bridge has seen without progress (it ends the operation as stalled at its window). */
  stalledMs: number;
  /** Wall-clock ms over which the rate was measured. */
  spanMs: number;
}

/**
 * Remaining-time estimate from successive status snapshots: the rate of the
 * operation's own clock (gameplay ms at the game's Time.timeScale, a series'
 * schedule offset) against this server's wall clock, from the first snapshot
 * that showed progress to the latest. Slow progress gives a long estimate,
 * never a stall: the bridge alone ends an operation that stopped moving.
 */
export class PlaymodeProgress {
  /** The last snapshot before progress began (elapsed 0), else the first one seen. */
  private ref: { atMs: number; elapsedMs: number } | null = null;
  private moving = false;
  private latest: PlaymodeEstimate | null = null;
  constructor(private readonly tailMs = 0, private readonly now: () => number = Date.now) {}

  observe(snapshot: Record<string, any>): PlaymodeEstimate {
    const progress = (snapshot.progress ?? {}) as Record<string, unknown>;
    const atMs = this.now();
    const num = (v: unknown) => { const n = Number(v); return Number.isFinite(n) ? n : 0; };
    const elapsed = num(progress.elapsedMs), expected = num(progress.expectedMs);
    if (!this.moving) {
      // While nothing moved (a start_on wait) the reference follows the latest sample.
      if (elapsed > 0) this.moving = true;
      if (elapsed === 0 || this.ref === null) this.ref = { atMs, elapsedMs: elapsed };
    }
    const spanMs = this.moving && this.ref ? atMs - this.ref.atMs : 0;
    const moved = this.moving && this.ref ? elapsed - this.ref.elapsedMs : 0;
    const rate = spanMs > 0 && moved > 0 ? moved / spanMs : null;
    const remaining = Math.max(0, expected - elapsed);
    const etaMs = PLAYMODE_TERMINAL.has(String(snapshot.state)) ? 0
      : remaining === 0 ? this.tailMs : rate === null ? null : Math.round(remaining / rate + this.tailMs);
    this.latest = {
      clock: typeof progress.clock === "string" ? progress.clock : null,
      phase: typeof snapshot.phase === "string" ? snapshot.phase : null,
      elapsedMs: elapsed, expectedMs: expected, rate: rate === null ? null : Math.round(rate * 1e4) / 1e4,
      etaMs, stalledMs: num(progress.stalledMs), spanMs,
    };
    return this.latest;
  }

  get last(): PlaymodeEstimate | null { return this.latest; }
}

/** Time without progress (bridge `stalledMs`) after which an operation counts as standing still, not slow. */
export const PLAYMODE_STILL_MS = 2_000;

/**
 * Inline poll decision: keep waiting, or hand the running operation to a job.
 * Hand over once the inline limit is reached, or as soon as a trusted rate
 * says a MOVING operation will not end inside it — never cancel it. One that
 * stands still (a paused Editor, Time.timeScale 0) is not handed over early:
 * it either moves again or the bridge ends it as stalled within its window.
 */
export function playmodeShouldHandOver(estimate: PlaymodeEstimate, sinceStartMs: number, limitMs: number): boolean {
  if (sinceStartMs >= limitMs) return true;
  return estimate.etaMs !== null && estimate.rate !== null && estimate.spanMs >= PLAYMODE_RATE_MIN_SPAN_MS
    && estimate.stalledMs < PLAYMODE_STILL_MS && sinceStartMs + estimate.etaMs > limitMs;
}

/** Stable failure code of a terminal play-mode snapshot (the operation's own code when it set one). */
export function playmodeFailureCode(snapshot: Record<string, any>, fallback = "operation_failed"): string {
  if (snapshot.state === "failed") return typeof snapshot.result?.error_code === "string" ? snapshot.result.error_code : fallback;
  return String(snapshot.reason ?? snapshot.state ?? fallback);
}

/**
 * The error of a terminal play-mode snapshot that did not succeed: its code,
 * the operation's own message, and for a stall `reason` (editor_paused /
 * time_scale_zero / no_frames / no_progress). A series cut short by leaving
 * play mode says how many frames it kept.
 */
export function playmodeFailure(snapshot: Record<string, any>, fallback = "operation_failed", noun = "play-mode operation"): { code: string; message: string; reason?: string } {
  const code = playmodeFailureCode(snapshot, fallback);
  const stall = typeof snapshot.stall?.reason === "string" ? { reason: snapshot.stall.reason as string } : {};
  if (typeof snapshot.result?.error === "string") return { code, message: snapshot.result.error, ...stall };
  if (snapshot.reason === "play_mode_exited") {
    const series = snapshot.result?.series as Record<string, unknown> | undefined;
    const kept = series && typeof series.captured === "number" && typeof series.requested === "number"
      ? `; ${series.captured} of ${series.requested} frames were captured and are kept (the rest are listed captured: false)` : "";
    return { code, message: `Play mode was exited before the ${noun} finished${kept}.`, ...stall };
  }
  return { code, message: `The ${noun} ended ${String(snapshot.reason ?? snapshot.state ?? "without completing")}.`, ...stall };
}
export const PLAYMODE_JOB_DEFAULT_DEADLINE_MS = 10 * 60_000;
export const PLAYMODE_JOB_DEFAULT_UNREACHABLE_MS = 2 * 60_000;
export interface PlaymodePollOptions { deadlineMs?: number; pollIntervalMs?: number; unreachableMs?: number }

const PLAYMODE_TERMINAL = new Set(["succeeded", "failed", "cancelled", "orphaned"]);

/** Parsed JSON body of a tool result ({} when it is not JSON). */
export function toolResultBody(result: CallToolResult): Record<string, any> {
  const text = result.content.find(item => item.type === "text");
  if (text?.type !== "text") return {};
  try { return JSON.parse(text.text) as Record<string, any>; } catch { return {}; }
}

/**
 * The tool's own answer, whichever shape carried it. A gate envelope yields
 * `mutation.output`; when the call was refused it also yields the refusal as
 * `error` (`mutation.error`: code, message and the tool's detail members) plus
 * the envelope's `agentNextSteps`. A direct body is returned as-is, so its
 * top-level `error` reads the same way. Every route that needs to know what a
 * tool answered or why it refused goes through here.
 */
export function toolOutput(result: CallToolResult): Record<string, any> {
  const body = toolResultBody(result);
  const mutation = body.mutation;
  if (!mutation || typeof mutation !== "object") return body;
  const output = mutation.output && typeof mutation.output === "object" ? mutation.output as Record<string, any> : null;
  if (mutation.error) {
    const steps = Array.isArray(body.agentNextSteps) && body.agentNextSteps.length ? { agentNextSteps: body.agentNextSteps } : {};
    return { ...(output && !Array.isArray(output) ? output : {}), error: mutation.error, ...steps };
  }
  return output ?? body;
}

export type PlaymodePollOutcome =
  | { kind: "terminal"; snapshot: Record<string, any>; cancelSent: boolean }
  | { kind: "lost"; detail: Record<string, any> }
  | { kind: "deadline"; snapshot: Record<string, any> | null; cancelSent: boolean }
  | { kind: "detached"; snapshot: Record<string, any> }
  | { kind: "unreachable"; error: string };

/**
 * Poll a scheduler operation to a terminal state. An abort of `signal` sends one
 * cancel and keeps polling for its acknowledgement; at the deadline a cancel is
 * sent once (the bridge then releases held input) before giving up. The
 * deadline counts from the last progress (`progress.elapsedMs` moved), so a
 * slow operation is never cut off; the bridge ends one that stopped moving. A
 * cancel counts as sent only once the bridge answered it — a failed one is
 * retried with backoff. An operation the deadline cancel ended is a `deadline`
 * outcome, never a caller-initiated `cancelled`. `detach` returning true for a
 * running snapshot stops polling WITHOUT a cancel (`detached`): the operation
 * keeps running for whoever observes it next.
 */
export async function pollPlaymodeOperation(live: LiveClient, operationId: string, options: PlaymodePollOptions & {
  signal?: AbortSignal;
  onSnapshot?: (snapshot: Record<string, any>) => void;
  onTransport?: (connected: boolean, detail: string) => void;
  detach?: (snapshot: Record<string, any>) => boolean;
} = {}): Promise<PlaymodePollOutcome> {
  const deadlineMs = options.deadlineMs ?? PLAYMODE_JOB_DEFAULT_DEADLINE_MS;
  const pollIntervalMs = options.pollIntervalMs ?? 250;
  const unreachableMs = options.unreachableMs ?? PLAYMODE_JOB_DEFAULT_UNREACHABLE_MS;
  let progressAt = Date.now();
  let lastElapsed: number | null = null;
  let lastAnswer = Date.now();
  let failures = 0;
  let cancelSent = false;
  let deadlineCancel = false;
  let last: Record<string, any> | null = null;
  while (true) {
    const overdue = Date.now() - progressAt >= deadlineMs;
    const wantCancel = !cancelSent && (overdue || options.signal?.aborted === true);
    if (!wantCancel || failures > 0) {
      try {
        await delay(pollIntervalMs * 2 ** Math.min(failures, 3), undefined,
          wantCancel || cancelSent || !options.signal ? undefined : { signal: options.signal });
      } catch { /* aborted: send the cancel now */ }
    }
    const action = !cancelSent && (overdue || options.signal?.aborted === true) ? "cancel" : "status";
    let answer: Record<string, any>;
    try {
      answer = await live.playmodeOperation({ action, operation_id: operationId });
      if (action === "cancel") {
        cancelSent = true;
        deadlineCancel = options.signal?.aborted !== true;
      }
      if (failures) options.onTransport?.(true, `Bridge answered again for operation ${operationId}.`);
      failures = 0;
      lastAnswer = Date.now();
    } catch (error) {
      if (error instanceof ProjectJobHttpError && error.status < 500) return { kind: "unreachable", error: String(error) };
      if (failures++ === 0) options.onTransport?.(false, `Operation poll failed (${String(error).slice(0, 200)}); retrying.`);
      if (Date.now() - lastAnswer >= unreachableMs) return { kind: "unreachable", error: `No answer for ${unreachableMs} ms.` };
      continue;
    }
    if (answer.error?.code === "operation_not_found") return { kind: "lost", detail: answer };
    if (answer.error) return { kind: "unreachable", error: `${answer.error.code}: ${answer.error.message}` };
    last = answer;
    const elapsed = Number(answer.progress?.elapsedMs);
    if (Number.isFinite(elapsed)) {
      if (lastElapsed !== null && elapsed > lastElapsed && !cancelSent) progressAt = Date.now();
      lastElapsed = elapsed;
    }
    options.onSnapshot?.(answer);
    if (deadlineCancel && answer.state === "cancelled" && answer.reason === "cancelled") return { kind: "deadline", snapshot: answer, cancelSent };
    if (PLAYMODE_TERMINAL.has(String(answer.state))) return { kind: "terminal", snapshot: answer, cancelSent };
    if (!cancelSent && options.detach?.(answer)) return { kind: "detached", snapshot: answer };
    // A cancel sent at the deadline gets a short grace window for its acknowledgement.
    if (overdue && cancelSent && Date.now() - progressAt >= deadlineMs + 5_000) return { kind: "deadline", snapshot: last, cancelSent };
  }
}

/**
 * Job progress of a running play-mode operation: the scheduler phase, the
 * fraction of its expected duration, and the numbers behind them — clock,
 * elapsed / expected (operation-clock ms), rate, the remaining wall-clock
 * estimate, time without progress and, for a series, captured of requested.
 */
export function playmodeJobProgress(snapshot: Record<string, any>, estimate: PlaymodeEstimate): { phase: string; fraction?: number; detail: Record<string, unknown> } {
  // A running series reports its counts in progress; a terminal one in its result.
  const series = (snapshot.progress?.series ?? snapshot.result?.series) as Record<string, unknown> | undefined;
  const fraction = estimate.expectedMs > 0 ? Math.min(1, Math.max(0, estimate.elapsedMs / estimate.expectedMs)) : undefined;
  return {
    phase: String(snapshot.phase ?? "running"),
    ...(fraction === undefined ? {} : { fraction }),
    detail: {
      clock: estimate.clock,
      elapsed_ms: estimate.elapsedMs,
      expected_ms: estimate.expectedMs,
      rate: estimate.rate,
      eta_ms: estimate.etaMs,
      stalled_ms: estimate.stalledMs,
      ...(series ? { captured: series.captured, requested: series.requested } : {}),
    },
  };
}

/** Flatten a terminal scheduler snapshot into a job/tool result. */
export function playmodeResult(snapshot: Record<string, any>): Record<string, unknown> {
  const { result, ...meta } = snapshot;
  return { operation: meta, ...(result && typeof result === "object" ? result : {}) };
}

export function playmodeOperation(tool: string, schema: Record<string, any>, client: ClientFor, poll: PlaymodePollOptions = {}): JobOperation {
  return {
    mutating: false, cancellable: true,
    validate(args) {
      if (args.operation_id !== undefined) throw new JobManagerError("invalid_arguments", "Jobs own operation_id; use idempotency_key to retry.");
      invalid(validateSchema(args, schema));
    },
    async run(args, context): Promise<JobOutcome> {
      const live = client(context.owner);
      if (context.continuation) {
        // The call that started the operation (under this id) handed it over:
        // observe it, never start it again — a record lost to a reload is lost.
        context.lifecycle("connected", `Continuing play-mode operation ${context.jobId}, started by the tool call`, true);
      } else {
        context.report({ phase: "starting" });
        // Start is idempotent per operation id: a retried POST returns the same record.
        const started = toolOutput(await live.route(tool, { ...args, operation_id: context.jobId }));
        const operation = started.operation as Record<string, any> | undefined;
        if (started.error || !operation)
          return { state: "failed", error: { code: started.error?.code ?? "operation_rejected", message: started.error?.message ?? "Bridge did not start the play-mode operation." }, result: started };
        if (operation.operation_id !== context.jobId) throw new Error("Bridge did not confirm play-mode operation ownership.");
        context.lifecycle("connected", `Bridge play-mode scheduler owns operation ${context.jobId}`, true);
      }
      // The deadline counts from the last progress and outlives the operation's
      // own worst case (a 10-minute wait); a stopped one is ended by the bridge.
      const deadlineMs = poll.deadlineMs ?? Math.max(PLAYMODE_JOB_DEFAULT_DEADLINE_MS, playmodeEstimateMs(tool, args) + 2 * 60_000);
      const progress = new PlaymodeProgress(playmodeTailMs(tool, args));
      const outcome = await pollPlaymodeOperation(live, context.jobId, {
        ...poll, deadlineMs, signal: context.signal,
        onSnapshot: snapshot => {
          context.report(playmodeJobProgress(snapshot, progress.observe(snapshot)));
        },
        onTransport: (connected, detail) => context.lifecycle(connected ? "connected" : "disconnected", detail, true),
      });
      switch (outcome.kind) {
        case "lost":
          context.lifecycle("disconnected", "Bridge lost the play-mode operation (domain reload or Editor restart); its held input was released by that cleanup.", false);
          return { state: "failed", error: { code: "operation_lost", message: "The bridge no longer knows this operation." }, result: outcome.detail };
        case "unreachable":
          context.lifecycle("disconnected", `Play-mode operation status unavailable: ${outcome.error}`, false);
          return { state: "failed", error: { code: "job_status_unavailable", message: outcome.error } };
        case "deadline":
          context.lifecycle("disconnected", "Play-mode operation reported no terminal state before the deadline; cancel sent.", false);
          return { state: "failed", error: { code: "job_deadline_exceeded", message: "No terminal state before the deadline." }, ...(outcome.snapshot ? { result: outcome.snapshot } : {}) };
      }
      const snapshot = outcome.snapshot;
      const result = playmodeResult(snapshot);
      if (snapshot.state === "succeeded") return { state: "succeeded", result };
      if (snapshot.state === "cancelled" && context.signal.aborted) return { state: "cancelled", result };
      if (snapshot.state === "orphaned") {
        context.lifecycle("disconnected", `Bridge ended the operation without completing it (${snapshot.reason}).`, false);
        return { state: "failed", error: { code: "operation_lost", message: String(snapshot.reason) }, result };
      }
      // cancelled without our request = play mode exited; failed = operation error
      // (a wait that timed out, a game exception, a stall: the operation's own error_code).
      const failure = playmodeFailure(snapshot, "operation_failed", PLAYMODE_ROUTES[tool]?.noun);
      // A stall's reason token is in result.operation.stall.reason; its message names it too.
      return { state: "failed", error: { code: failure.code, message: failure.message }, result };
    },
  };
}

// ---------------------------------------------------------------------------
// Editor wait — `editor_status {"wait_until":…}` past its inline window. The
// wait polls from the server (instance lock, /ping, /compile-state), so it
// survives the domain reloads it waits for. An observer: it changes nothing,
// holds no project slot (a wait never queues behind a test run, nor blocks
// one) and is cancellable at any time.
// ---------------------------------------------------------------------------

export function editorWaitOperation(depsFor: (owner: JobOwner) => WaitDeps): JobOperation {
  return {
    mutating: false, cancellable: true, observer: true,
    validate(args) {
      invalid(validateSchema(args, editorStatus.inputSchema as Record<string, any>));
      if (args.wait_until === undefined) throw new JobManagerError("invalid_arguments", "An editor_status job is a wait: pass wait_until.");
    },
    async run(args, context): Promise<JobOutcome> {
      const request: WaitRequest = {
        until: args.wait_until as WaitUntil,
        ...(typeof args.after_generation === "number" ? { afterGeneration: args.after_generation } : {}),
        timeoutMs: typeof args.timeout_ms === "number" ? args.timeout_ms : WAIT_DEFAULT_TIMEOUT_MS,
      };
      context.lifecycle("connected", "Waiting from the server: instance lock, /ping and /compile-state.", true);
      let phase = "waiting";
      context.report({ phase });
      const outcome = await waitForEditor(request, {
        ...depsFor(context.owner),
        signal: context.signal,
        onProgress: next => { if (next !== phase) { phase = next; context.report({ phase }); } },
      });
      const { body, isError } = describeWait(request, outcome);
      if (outcome.reason === "aborted") return { state: "cancelled", result: body };
      if (isError) return { state: "failed", error: body.error as { code: string; message: string }, result: body };
      return { state: "succeeded", result: body };
    },
  };
}
