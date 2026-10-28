// Playtest session recorder (unity_open_mcp_playtest_session).
//
// The MCP server sees every tool call, so the session log lives here; the
// bridge only writes frames to its capture folder. While an agent owner has an
// open session, each of its input / wait / capture / project-command calls
// (and play-mode / Game view changes) is appended to the session log when the
// call ends: tool, redacted arguments, status, duration, the frames it wrote,
// and the console errors / exceptions logged during it. Errors come from the
// bridge event stream (timestamped, with stacks) correlated by time window;
// the call's own `logs[]` and a play-mode operation's `errors` fill in what
// the stream missed (it reconnects after a domain reload). An error logged
// between calls belongs to the call before it (`after`) — a tap's consequence
// shows up a few frames later.
//
// Ownership: the owner is the request identity jobs use (project + agent id +
// per-request port override). One open session per owner; other owners' calls
// are never recorded into it. The log is appended per line as events happen,
// so it survives a domain reload and an MCP server restart; a restarted server
// adopts a session whose owning process is gone (`resume` / `end` with
// `session`), and the report lists the call that was in flight when the old
// process stopped.
import { randomBytes } from "node:crypto";
import { copyFileSync, linkSync, statSync, writeFileSync } from "node:fs";
import { basename, isAbsolute, join } from "node:path";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { BridgeEvent } from "../event-stream.js";
import { toolOutput, toolResultBody } from "../jobs/adapters.js";
import { ALL_TOOLS } from "../tools/index.js";
import { isPidAlive } from "../instance-discovery.js";
import { Redactor } from "./redact.js";
import {
  FRAMES_DIR, REPORT_DIR, SESSION_FORMAT, SESSION_ID_PATTERN, SessionStore, readProjectIdentity, sessionId,
  type SessionMeta, type SessionOwner,
} from "./session-store.js";
import { writeReport, type ReportSummary } from "./report.js";

export const PLAYTEST_SESSION_TOOL = "unity_open_mcp_playtest_session";

export const RECORDING_LIMITS = {
  /** Recorded calls per session; later calls are counted, not logged. */
  maxCalls: 500,
  maxMarks: 200,
  /** Frames linked / copied into the report folder per session. */
  maxFrames: 300,
  maxFrameBytes: 512 * 1024 * 1024,
  /** Thumbnails written by `end` (downscaled; the full frames stay files). */
  maxThumbnails: 60,
  maxErrorsPerCall: 20,
  maxEditorEvents: 200,
  maxArgsChars: 2_000,
  maxPendingErrors: 500,
} as const;

export type CallKind = "input" | "wait" | "capture" | "command" | "control";

const INPUT_TOOLS = new Set([
  "unity_open_mcp_inputsim_touch", "unity_open_mcp_inputsim_pointer", "unity_open_mcp_inputsim_pointer3d",
  "unity_open_mcp_inputsim_key", "unity_open_mcp_inputsim_gesture", "unity_open_mcp_inputsim_step",
]);
const CAPTURE_TOOLS = new Set([
  "unity_senses_screenshot", "unity_senses_screenshot_window", "unity_senses_screenshot_camera",
  "unity_senses_capture_inline", "unity_senses_capture_series", "unity_senses_visual_compare",
]);
const WAIT_TOOL = "unity_open_mcp_playtest_wait";
const JOBS_TOOL = "unity_open_mcp_jobs";
const COMMANDS_TOOL = "unity_open_mcp_project_commands";

function kindOfTool(tool: string): CallKind | null {
  if (INPUT_TOOLS.has(tool)) return "input";
  if (CAPTURE_TOOLS.has(tool)) return "capture";
  if (tool === WAIT_TOOL) return "wait";
  if (tool.startsWith("project.")) return "command";
  return null;
}

/** Which recorded category a call falls in (null = not recorded). */
export function classifyCall(tool: string, args: Record<string, unknown>): { kind: CallKind; label: string; args: unknown } | null {
  const direct = kindOfTool(tool);
  if (direct) return { kind: direct, label: tool, args };
  if (tool === COMMANDS_TOOL && args.action === "invoke")
    return { kind: "command", label: `${COMMANDS_TOOL} ${String(args.command_id ?? "")}`.trim(), args };
  if (tool === JOBS_TOOL && args.action === "start" && typeof args.tool_or_command === "string") {
    const inner = kindOfTool(args.tool_or_command);
    if (inner) return { kind: inner, label: `${JOBS_TOOL} start ${args.tool_or_command}`, args };
  }
  if (tool === "unity_open_mcp_editor_set_state") return { kind: "control", label: tool, args };
  if (tool === "unity_open_mcp_game_view" && (args.action === "set_resolution" || args.action === "set_device"))
    return { kind: "control", label: tool, args };
  return null;
}

export function ownerKey(owner: SessionOwner): string {
  return `${owner.project}\u0000${owner.port ?? ""}\u0000${owner.agent}`;
}

interface PendingError {
  ts: number;
  type: string;
  message: string;
  stack?: string;
  source: "stream" | "response";
}

/** A finished call's time window and the error messages already recorded for it. */
interface CallWindow { seq: number; start: number; end: number; messages: Set<string> }

interface OpenSession {
  meta: SessionMeta;
  dir: string;
  nextSeq: number;
  nextStep: number;
  calls: number;
  droppedCalls: number;
  marks: number;
  frames: number;
  frameBytes: number;
  framesSkipped: number;
  editorEvents: number;
  lastEditorState: string | null;
  streamReady: number;
  windows: CallWindow[];
  inflight: Map<number, number>;
  pending: PendingError[];
  jobs: Map<string, { seq: number; kind: CallKind; tool: string }>;
  limitNoted: Set<string>;
  sinceMark: { calls: number; errors: number; captures: number; failed: number };
}

export interface CallHandle { session: OpenSession; seq: number; tool: string; kind: CallKind; start: number }

/** Calls a tool through the normal route on behalf of the session owner. */
export type ToolCaller = (tool: string, args: Record<string, unknown>) => Promise<CallToolResult>;

export class PlaytestError extends Error {
  constructor(readonly code: string, message: string, readonly detail: Record<string, unknown> = {}) { super(message); }
}

export interface RecorderOptions {
  root: string;
  projectPath: string;
  knownSecrets?: Iterable<string | undefined | null>;
  now?: () => number;
  pid?: number;
  /** Process-liveness probe for adoption (tests inject). */
  isAlive?: (pid: number) => boolean;
  /** Whether the bridge event stream is connected (errors without it come from responses only). */
  streamConnected?: () => boolean;
}

const iso = (ms: number) => new Date(ms).toISOString();
const ERROR_TYPES = new Set(["error", "exception", "assert"]);
const TERMINAL_JOB_STATES = new Set(["succeeded", "failed", "cancelled", "orphaned"]);
/** Errors are told apart by their first message line; the stream says Exception where logs[] says error. */
const messageKey = (message: string) => message.split("\n")[0].trim().slice(0, 200);
const clip = (value: string, max: number) => (value.length > max ? `${value.slice(0, max)}…` : value);

function boundedJson(value: unknown, max: number): unknown {
  const text = JSON.stringify(value);
  if (text === undefined || text.length <= max) return value;
  return { truncated: true, json: clip(text, max) };
}

const SUMMARY_KEYS = [
  "action", "status", "outcome", "fired", "elapsed_ms", "realtime_ms", "backend", "target", "resolved", "targetHit", "topHit",
  "blockedBy", "framesAdvanced", "released", "isPlaying", "isPaused", "mode", "size", "device", "settled", "capturedAt",
  "time_scale", "frames", "exception",
];

export class PlaytestRecorder {
  readonly store: SessionStore;
  readonly redactor: Redactor;
  private readonly projectPath: string;
  private readonly now: () => number;
  private readonly pid: number;
  private readonly isAlive: (pid: number) => boolean;
  private readonly streamConnected: () => boolean;
  private readonly open = new Map<string, OpenSession>();
  private identity: { company: string; product: string } | null = null;

  constructor(options: RecorderOptions) {
    this.store = new SessionStore(options.root);
    this.projectPath = options.projectPath;
    this.redactor = new Redactor(options.knownSecrets ?? []);
    this.now = options.now ?? Date.now;
    this.pid = options.pid ?? process.pid;
    this.isAlive = options.isAlive ?? isPidAlive;
    this.streamConnected = options.streamConnected ?? (() => true);
  }

  /** Cheap hot-path check: is any session open in this process? */
  get openCount(): number { return this.open.size; }

  isRecording(owner: SessionOwner): boolean {
    return this.open.size > 0 && this.open.has(ownerKey(owner));
  }

  // ---------------------------------------------------------------------
  // Hook: one recorded call
  // ---------------------------------------------------------------------

  begin(owner: SessionOwner, tool: string, args: Record<string, unknown>): CallHandle | null {
    const session = this.open.get(ownerKey(owner));
    if (!session) return null;
    const classified = classifyCall(tool, args);
    if (!classified) return null;
    this.settle(session);
    if (session.calls >= RECORDING_LIMITS.maxCalls) {
      session.droppedCalls++;
      this.noteLimit(session, "calls", `More than ${RECORDING_LIMITS.maxCalls} calls; later calls are counted but not logged.`);
      return null;
    }
    const seq = session.nextSeq++;
    session.calls++;
    const start = this.now();
    session.inflight.set(seq, start);
    // The arguments go with the start line, so a call lost in flight still shows what it was.
    this.append(session, {
      k: "call_started", seq, tool: classified.label, kind: classified.kind, at: iso(start),
      args: boundedJson(this.redactor.value(asSent(classified.label, args)), RECORDING_LIMITS.maxArgsChars),
    });
    return { session, seq, tool: classified.label, kind: classified.kind, start };
  }

  finish(handle: CallHandle, _args: Record<string, unknown>, result: CallToolResult | null, thrown?: unknown): void {
    const { session, seq } = handle;
    session.inflight.delete(seq);
    if (!this.isOpen(session)) return;
    const end = this.now();
    const body = result ? toolResultBody(result) : {};
    const output = result ? toolOutput(result) : {};
    const error = (output.error ?? body.error) as { code?: string; message?: string } | undefined;
    const jobId = typeof body.job_id === "string" ? body.job_id : undefined;
    const status = thrown ? "error" : result?.isError || error ? "error" : jobId && !TERMINAL_JOB_STATES.has(String(body.state)) ? "job_started" : "ok";
    const errors = this.collectErrors(session, handle.start, end, output, body);
    const captures = result ? this.collectCaptures(session, seq, output, result) : [];
    this.pushWindow(session, { seq, start: handle.start, end, messages: new Set(errors.map(e => messageKey(String(e.message ?? "")))) });
    if (jobId && status === "job_started") session.jobs.set(jobId, { seq, kind: handle.kind, tool: handle.tool });
    session.sinceMark.calls++;
    session.sinceMark.errors += errors.length;
    session.sinceMark.captures += captures.length;
    if (status === "error") session.sinceMark.failed++;
    const entry: Record<string, unknown> = {
      k: "call", seq, tool: handle.tool, kind: handle.kind,
      at: iso(handle.start), ended_at: iso(end), duration_ms: end - handle.start, status,
    };
    if (thrown) entry.error = { code: "internal_error", message: this.redactor.text(clip(String(thrown instanceof Error ? thrown.message : thrown), 500)) };
    else if (error) entry.error = this.redactor.value({ code: error.code ?? "error", message: clip(String(error.message ?? ""), 500) });
    if (jobId) entry.job_id = jobId;
    if (!this.streamConnected()) entry.stream = false;
    const summary = this.summarize(handle.kind, output);
    if (summary) entry.summary = summary;
    if (captures.length) entry.captures = captures;
    if (errors.length) entry.errors = errors;
    this.append(session, entry);
    this.settle(session);
  }

  /** `jobs wait` / `jobs status` reached a terminal state for a job a recorded call started. */
  observeJob(owner: SessionOwner, args: Record<string, unknown>, result: CallToolResult): void {
    const session = this.open.get(ownerKey(owner));
    if (!session || (args.action !== "wait" && args.action !== "status")) return;
    const body = toolResultBody(result);
    const jobId = typeof body.job_id === "string" ? body.job_id : undefined;
    const started = jobId ? session.jobs.get(jobId) : undefined;
    if (!jobId || !started || !TERMINAL_JOB_STATES.has(String(body.state))) return;
    session.jobs.delete(jobId);
    if (session.calls >= RECORDING_LIMITS.maxCalls) { session.droppedCalls++; return; }
    const seq = session.nextSeq++;
    session.calls++;
    const start = typeof body.started_at === "number" ? body.started_at : this.now();
    const end = typeof body.finished_at === "number" ? body.finished_at : this.now();
    const jobResult = (body.result && typeof body.result === "object" ? body.result : {}) as Record<string, any>;
    const errors = this.collectErrors(session, start, end, jobResult, {});
    const captures = this.collectCaptures(session, seq, jobResult, null);
    this.pushWindow(session, { seq, start, end, messages: new Set(errors.map(e => messageKey(String(e.message ?? "")))) });
    session.sinceMark.calls++;
    session.sinceMark.errors += errors.length;
    session.sinceMark.captures += captures.length;
    const status = body.state === "succeeded" ? "ok" : "error";
    if (status === "error") session.sinceMark.failed++;
    const entry: Record<string, unknown> = {
      k: "call", seq, tool: `${started.tool} (job result)`, kind: started.kind, of: started.seq, job_id: jobId,
      at: iso(start), ended_at: iso(end), duration_ms: Math.max(0, end - start), status, job_state: body.state,
    };
    if (body.error) entry.error = this.redactor.value(body.error);
    const summary = this.summarize(started.kind, jobResult);
    if (summary) entry.summary = summary;
    if (captures.length) entry.captures = captures;
    if (errors.length) entry.errors = errors;
    this.append(session, entry);
    this.settle(session);
  }

  /** Bridge event stream listener (every event, every open session). */
  onEvent(event: BridgeEvent): void {
    if (this.open.size === 0) return;
    for (const session of this.open.values()) {
      if (event.type === "log" && ERROR_TYPES.has(String(event.logType ?? "").toLowerCase())) {
        if (session.pending.length >= RECORDING_LIMITS.maxPendingErrors) continue;
        const ts = Date.parse(event.ts);
        session.pending.push({
          ts: Number.isFinite(ts) ? ts : this.now(), type: String(event.logType),
          message: event.message ?? "", ...(event.stack ? { stack: event.stack } : {}), source: "stream",
        });
      } else if (event.type === "editor_state" && event.state && event.state !== session.lastEditorState) {
        session.lastEditorState = event.state;
        if (session.editorEvents++ < RECORDING_LIMITS.maxEditorEvents)
          this.append(session, { k: "editor", at: event.ts, state: event.state, playing: event.isPlaying === true });
      } else if (event.type === "ready") {
        // The first `ready` is the subscription the session started; a later one
        // is a reconnect — the bridge was away (a domain reload, an Editor restart).
        if (session.streamReady++ > 0 && session.editorEvents++ < RECORDING_LIMITS.maxEditorEvents)
          this.append(session, { k: "editor", at: event.ts, state: "stream_reconnected", playing: null });
      } else if (event.type === "missed") {
        this.append(session, { k: "stream_gap", at: event.ts, detail: event.message ?? "" });
      }
      this.settle(session);
    }
  }

  // ---------------------------------------------------------------------
  // Actions
  // ---------------------------------------------------------------------

  async start(owner: SessionOwner, args: Record<string, unknown>, call: ToolCaller): Promise<Record<string, unknown>> {
    const existing = this.open.get(ownerKey(owner)) ?? this.diskSessionOf(owner);
    if (existing) {
      const meta = "meta" in existing ? existing.meta : existing;
      throw new PlaytestError("session_already_open",
        `This agent already has an open playtest session (${meta.id}); end it before starting another.`,
        { session: meta.id, name: meta.name, started_at: meta.started_at });
    }
    if (args.snapshot === true && typeof args.use_snapshot === "string")
      throw new PlaytestError("invalid_arguments", "Pass either snapshot:true (take one now) or use_snapshot (an existing one), not both.");
    const identity = await this.resolveIdentity(call);
    const name = typeof args.name === "string" && args.name.trim() ? args.name.trim() : "playtest";
    const startedAt = this.now();
    const id = sessionId(name, new Date(startedAt), randomBytes(2).toString("hex"));
    let snapshot: SessionMeta["snapshot"] = null;
    if (args.snapshot === true) {
      const snapshotName = `playtest-${id}`.slice(0, 64);
      const result = await call("unity_open_mcp_player_state", { action: "snapshot", name: snapshotName });
      const out = toolOutput(result);
      if (result.isError || out.error) {
        const failure = (out.error ?? toolResultBody(result).error ?? {}) as { code?: string; message?: string };
        throw new PlaytestError("snapshot_failed",
          `The player-state snapshot failed (${failure.code ?? "error"}): ${failure.message ?? "no detail"}. No session was started.`,
          { snapshot_error: failure });
      }
      snapshot = { name: snapshotName, taken: true };
    } else if (typeof args.use_snapshot === "string") {
      snapshot = { name: args.use_snapshot, taken: false };
    }
    const device = await this.describeDevice(args, call);
    const meta: SessionMeta = {
      format: SESSION_FORMAT, id, name: this.redactor.text(name), project: this.projectPath,
      company: identity.company, product: identity.product, owner: { ...owner }, server_pid: this.pid,
      started_at: iso(startedAt), state: "open", device, snapshot,
      limits: { ...RECORDING_LIMITS },
    };
    const dir = this.store.create(meta);
    const session = this.openSession(meta, dir);
    this.append(session, { k: "session_started", at: meta.started_at, name: meta.name, device, snapshot });
    return {
      status: "ok", action: "start", session: id, name: meta.name, folder: dir, device, snapshot,
      recording: ["input (inputsim_*)", "wait (playtest_wait)", "capture (screenshots, capture_inline, capture_series, visual_compare)",
        "command (project_commands invoke, project-command and play-mode jobs)", "control (editor_set_state, game_view set_*)"],
      limits: RECORDING_LIMITS,
      agentNextSteps: [
        "Your input / wait / capture / project-command calls are now recorded with their console errors and frames.",
        "After each player-visible step call playtest_session {\"action\":\"mark\",\"label\":\"…\",\"verdict\":\"ok|bug|suspicious\",\"note\":\"…\"}.",
        "Finish with playtest_session {\"action\":\"end\"} and hand the report path to the human.",
      ],
    };
  }

  mark(owner: SessionOwner, args: Record<string, unknown>, sessionArg?: string): Record<string, unknown> {
    const session = this.requireOpen(owner, sessionArg);
    this.settle(session);
    if (session.marks >= RECORDING_LIMITS.maxMarks)
      throw new PlaytestError("limit_reached", `A session holds at most ${RECORDING_LIMITS.maxMarks} marks; end it and start another.`);
    const verdict = typeof args.verdict === "string" ? args.verdict : "ok";
    const step = session.nextStep++;
    session.marks++;
    const since = { ...session.sinceMark };
    session.sinceMark = { calls: 0, errors: 0, captures: 0, failed: 0 };
    this.append(session, {
      k: "mark", step, at: iso(this.now()), label: this.redactor.text(String(args.label ?? "")).slice(0, 120), verdict,
      ...(typeof args.note === "string" && args.note ? { note: this.redactor.text(clip(args.note, 2_000)) } : {}),
    });
    return {
      status: "ok", action: "mark", session: session.meta.id, step, label: args.label, verdict,
      since_last_mark: since,
      ...(since.errors > 0 ? { note: `${since.errors} console error(s) / exception(s) were logged during this step — treat them as findings (they are in the report with the call that caused them).` } : {}),
    };
  }

  status(owner: SessionOwner): Record<string, unknown> {
    const session = this.open.get(ownerKey(owner));
    if (session) this.settle(session);
    const orphaned = this.orphanedSessions(owner);
    return {
      status: "ok", action: "status",
      session: session ? this.describe(session) : null,
      ...(orphaned.length ? {
        orphaned,
        agentNextSteps: ["An open session whose MCP server process is gone can be continued with {\"action\":\"resume\",\"session\":\"<id>\"} or closed with {\"action\":\"end\",\"session\":\"<id>\"}."],
      } : {}),
    };
  }

  resume(owner: SessionOwner, sessionArg: string | undefined): Record<string, unknown> {
    if (!sessionArg) throw new PlaytestError("invalid_arguments", "resume requires session (the id start returned).");
    const own = this.open.get(ownerKey(owner));
    if (own && own.meta.id === sessionArg) return { status: "ok", action: "resume", already_open: true, session: this.describe(own) };
    if (own) throw new PlaytestError("session_already_open", `This agent already has an open playtest session (${own.meta.id}).`, { session: own.meta.id });
    const session = this.adopt(owner, sessionArg);
    const lost = this.lostInFlight(session);
    return {
      status: "ok", action: "resume", session: this.describe(session),
      ...(lost.length ? { lost_in_flight: lost } : {}),
    };
  }

  async end(owner: SessionOwner, args: Record<string, unknown>, call: ToolCaller): Promise<Record<string, unknown>> {
    const sessionArg = typeof args.session === "string" ? args.session : undefined;
    let session = this.open.get(ownerKey(owner));
    if (session && sessionArg && session.meta.id !== sessionArg)
      throw new PlaytestError("session_already_open", `This agent's open session is ${session.meta.id}, not ${sessionArg}; end that one first.`, { session: session.meta.id });
    if (!session) {
      if (!sessionArg) {
        const orphaned = this.orphanedSessions(owner);
        throw new PlaytestError("no_open_session", "This agent has no open playtest session.",
          orphaned.length ? { orphaned, hint: "After an MCP server restart pass session: <id> to end (or resume) the session the old process left open." } : {});
      }
      session = this.adopt(owner, sessionArg);
    }
    // Let in-flight stream events (SSE latency) arrive before the last attribution.
    await new Promise(resolve => setTimeout(resolve, 300));
    this.settle(session, true);
    let stateDiff: { ok: boolean; output?: unknown; error?: unknown } | null = null;
    if (session.meta.snapshot && args.diff !== false) {
      try {
        const result = await call("unity_open_mcp_player_state", { action: "diff", name: session.meta.snapshot.name, limit: 200 });
        const out = toolOutput(result);
        stateDiff = result.isError || out.error
          ? { ok: false, error: out.error ?? toolResultBody(result).error ?? { code: "diff_failed" } }
          : { ok: true, output: out };
      } catch (error) {
        stateDiff = { ok: false, error: { code: "diff_failed", message: error instanceof Error ? error.message : String(error) } };
      }
    }
    const endedAt = this.now();
    if (typeof args.note === "string" && args.note)
      this.append(session, { k: "end_note", at: iso(endedAt), note: this.redactor.text(clip(args.note, 2_000)) });
    this.append(session, { k: "session_ended", at: iso(endedAt), dropped_calls: session.droppedCalls, frames_skipped: session.framesSkipped });
    const meta: SessionMeta = { ...session.meta, state: "ended", ended_at: iso(endedAt) };
    const summary: ReportSummary = writeReport({
      store: this.store, dir: session.dir, meta, entries: this.store.readLog(session.dir),
      stateDiff: stateDiff ? this.redactor.value(stateDiff) : null, redactor: this.redactor,
      maxThumbnails: RECORDING_LIMITS.maxThumbnails, now: this.now,
    });
    meta.report = summary.report;
    this.store.writeMeta(meta);
    this.open.delete(ownerKey(session.meta.owner));
    const nextSteps = [`Hand the report to the human: ${summary.report}`];
    if (summary.verdicts.bug > 0 || summary.errors.unique > 0)
      nextSteps.push("Summarize the bug / suspicious steps and the first occurrence of each error from summary.findings and summary.top_errors.");
    if (summary.hooks.begin && !summary.hooks.end)
      nextSteps.push(`The project's isolation hook ${summary.hooks.begin} ran without its end hook — invoke the matching playtest end command.`);
    if (session.meta.snapshot)
      nextSteps.push(`Restore the player state after stopping play mode: player_state {"action":"restore","name":"${session.meta.snapshot.name}"} (dry run), then with "confirm":true.`);
    return { status: "ok", action: "end", session: meta.id, report: summary.report, folder: session.dir, summary, agentNextSteps: nextSteps };
  }

  list(owner: SessionOwner, limit: number): Record<string, unknown> {
    const identity = this.identity ?? readProjectIdentity(this.projectPath);
    if (!identity) return { status: "ok", action: "list", sessions: [] };
    const all = this.store.list(identity.company, identity.product).filter(s => s.meta.project === this.projectPath);
    return {
      status: "ok", action: "list", company: identity.company, product: identity.product,
      root: this.store.productDir(identity.company, identity.product), total: all.length,
      sessions: all.slice(0, limit).map(({ dir, meta }) => ({
        session: meta.id, name: meta.name, state: this.stateOf(meta, owner), started_at: meta.started_at,
        ...(meta.ended_at ? { ended_at: meta.ended_at } : {}), ...(meta.report ? { report: meta.report } : {}), folder: dir,
      })),
    };
  }

  // ---------------------------------------------------------------------
  // Internals
  // ---------------------------------------------------------------------

  private openSession(meta: SessionMeta, dir: string): OpenSession {
    const session: OpenSession = {
      meta, dir, nextSeq: 1, nextStep: 1, calls: 0, droppedCalls: 0, marks: 0, frames: 0, frameBytes: 0, framesSkipped: 0,
      // An already-connected stream will not send another `ready` until it reconnects.
      editorEvents: 0, lastEditorState: null, streamReady: this.streamConnected() ? 1 : 0, windows: [], inflight: new Map(), pending: [], jobs: new Map(),
      limitNoted: new Set(), sinceMark: { calls: 0, errors: 0, captures: 0, failed: 0 },
    };
    this.open.set(ownerKey(meta.owner), session);
    return session;
  }

  private isOpen(session: OpenSession): boolean {
    return this.open.get(ownerKey(session.meta.owner)) === session;
  }

  private append(session: OpenSession, entry: Record<string, unknown>): void {
    try { this.store.append(session.meta, entry); }
    catch (error) { console.error("[unity-open-mcp] playtest session log write failed:", error); }
  }

  private noteLimit(session: OpenSession, what: string, message: string): void {
    if (session.limitNoted.has(what)) return;
    session.limitNoted.add(what);
    this.append(session, { k: "limit", at: iso(this.now()), what, message });
  }

  private requireOpen(owner: SessionOwner, sessionArg?: string): OpenSession {
    const session = this.open.get(ownerKey(owner));
    if (session && (!sessionArg || session.meta.id === sessionArg)) return session;
    if (!session && sessionArg) return this.adopt(owner, sessionArg);
    if (session) throw new PlaytestError("session_already_open", `This agent's open session is ${session.meta.id}, not ${sessionArg}.`, { session: session.meta.id });
    const orphaned = this.orphanedSessions(owner);
    throw new PlaytestError("no_open_session", "This agent has no open playtest session; start one first.", orphaned.length ? { orphaned } : {});
  }

  /** Take over an open session whose owner is this caller or whose server process is gone. */
  private adopt(owner: SessionOwner, id: string): OpenSession {
    if (!SESSION_ID_PATTERN.test(id)) throw new PlaytestError("invalid_arguments", `'${id}' is not a session id.`);
    const found = this.findSession(id);
    if (!found) throw new PlaytestError("session_not_found", `No playtest session ${id} for this project.`);
    const { dir, meta } = found;
    if (meta.state === "ended")
      throw new PlaytestError("session_ended", `Session ${id} has already ended.`, { report: meta.report ?? null });
    const same = ownerKey(meta.owner) === ownerKey(owner);
    for (const other of this.open.values())
      if (other.meta.id === id && !same) throw new PlaytestError("session_owned_by_other_agent", `Session ${id} belongs to another agent of this MCP server.`);
    // Not open in this process (checked above), so a session naming this pid
    // was left by an earlier process that had the same pid.
    if (!same && meta.server_pid !== this.pid && this.isAlive(meta.server_pid))
      throw new PlaytestError("session_owned_by_other_agent",
        `Session ${id} is open in another running MCP server process (pid ${meta.server_pid}); only its owner can mark or end it.`);
    const adopted: SessionMeta = {
      ...meta, owner: { ...owner }, server_pid: this.pid,
      adoptions: [...(meta.adoptions ?? []), { at: iso(this.now()), from: meta.owner, from_pid: meta.server_pid }],
    };
    this.store.writeMeta(adopted);
    const session = this.openSession(adopted, dir);
    // Continue numbering after what the previous process logged.
    for (const entry of this.store.readLog(dir)) {
      if (typeof entry.seq === "number") session.nextSeq = Math.max(session.nextSeq, entry.seq + 1);
      if (entry.k === "call") {
        session.calls++;
        const start = Date.parse(entry.at), end = Date.parse(entry.ended_at);
        if (Number.isFinite(start) && Number.isFinite(end)) session.windows.push({ seq: entry.seq, start, end, messages: new Set() });
        session.frames += Array.isArray(entry.captures) ? entry.captures.filter((c: any) => c.file).length : 0;
      }
      if (entry.k === "mark") { session.marks++; session.nextStep = Math.max(session.nextStep, entry.step + 1); }
    }
    session.windows = session.windows.slice(-256);
    this.append(session, { k: "adopted", at: iso(this.now()), from: meta.owner, from_pid: meta.server_pid, lost_in_flight: this.lostInFlight(session) });
    return session;
  }

  private lostInFlight(session: OpenSession): Array<{ seq: number; tool: string; at: string }> {
    const started = new Map<number, { seq: number; tool: string; at: string }>();
    for (const entry of this.store.readLog(session.dir)) {
      if (entry.k === "call_started") started.set(entry.seq, { seq: entry.seq, tool: entry.tool, at: entry.at });
      if (entry.k === "call") started.delete(entry.seq);
    }
    for (const seq of session.inflight.keys()) started.delete(seq);
    return [...started.values()];
  }

  private findSession(id: string): { dir: string; meta: SessionMeta } | null {
    const identity = this.identity ?? readProjectIdentity(this.projectPath);
    if (!identity) return null;
    const dir = this.store.dir({ company: identity.company, product: identity.product, id });
    const meta = this.store.readMeta(dir);
    return meta && meta.project === this.projectPath ? { dir, meta } : null;
  }

  /** An open on-disk session owned by exactly this owner (a stable explicit agent id after a restart). */
  private diskSessionOf(owner: SessionOwner): SessionMeta | null {
    const identity = this.identity ?? readProjectIdentity(this.projectPath);
    if (!identity) return null;
    const key = ownerKey(owner);
    return this.store.list(identity.company, identity.product)
      .find(s => s.meta.state === "open" && s.meta.project === this.projectPath && ownerKey(s.meta.owner) === key)?.meta ?? null;
  }

  private stateOf(meta: SessionMeta, owner: SessionOwner): string {
    if (meta.state === "ended") return "ended";
    if ([...this.open.values()].some(s => s.meta.id === meta.id)) return ownerKey(meta.owner) === ownerKey(owner) ? "open" : "open_other_agent";
    if (meta.server_pid !== this.pid && this.isAlive(meta.server_pid)) return "open_other_process";
    return "orphaned";
  }

  private orphanedSessions(owner: SessionOwner): Array<Record<string, unknown>> {
    const identity = this.identity ?? readProjectIdentity(this.projectPath);
    if (!identity) return [];
    return this.store.list(identity.company, identity.product)
      .filter(s => s.meta.project === this.projectPath && this.stateOf(s.meta, owner) === "orphaned")
      .slice(0, 10)
      .map(s => ({ session: s.meta.id, name: s.meta.name, started_at: s.meta.started_at, owner_pid: s.meta.server_pid }));
  }

  private describe(session: OpenSession): Record<string, unknown> {
    return {
      session: session.meta.id, name: session.meta.name, started_at: session.meta.started_at, folder: session.dir,
      calls: session.calls, marks: session.marks, frames: session.frames, pending_errors: session.pending.length,
      since_last_mark: session.sinceMark, ...(session.droppedCalls ? { dropped_calls: session.droppedCalls } : {}),
      snapshot: session.meta.snapshot, device: session.meta.device,
    };
  }

  /** Resolve company / product once per process (live Player Settings, else ProjectSettings.asset). */
  async prepare(call: ToolCaller): Promise<void> {
    if (this.identity) return;
    try { await this.resolveIdentity(call); } catch { /* actions report identity_unavailable themselves */ }
  }

  private async resolveIdentity(call: ToolCaller): Promise<{ company: string; product: string }> {
    if (this.identity) return this.identity;
    try {
      const result = await call("unity_open_mcp_player_state", { action: "list" });
      const out = toolOutput(result);
      if (!result.isError && typeof out.company === "string" && typeof out.product === "string") {
        this.identity = { company: out.company, product: out.product };
        return this.identity;
      }
    } catch { /* fall back to ProjectSettings */ }
    const fromDisk = readProjectIdentity(this.projectPath);
    if (!fromDisk) throw new PlaytestError("identity_unavailable",
      "Could not read the project's company / product (no live Editor and no readable ProjectSettings/ProjectSettings.asset).");
    this.identity = fromDisk;
    return fromDisk;
  }

  private async describeDevice(args: Record<string, unknown>, call: ToolCaller): Promise<Record<string, unknown>> {
    const device: Record<string, unknown> = {};
    if (typeof args.device === "string" && args.device.trim()) device.summary = this.redactor.text(args.device.trim()).slice(0, 200);
    try {
      const result = await call("unity_open_mcp_game_view", { action: "get" });
      const out = toolOutput(result);
      if (!result.isError && !out.error) {
        const screen = (out.screen ?? {}) as Record<string, any>;
        device.view = {
          mode: out.mode, playing: out.playing,
          ...(out.size ? { size: (out.size as Record<string, unknown>).label } : {}),
          ...(out.device ? { device: (out.device as Record<string, unknown>).id } : {}),
          ...(screen.width ? { screen: [screen.width, screen.height], orientation: screen.orientation, safe_area: screen.safe_area } : {}),
        };
      } else device.view_error = (out.error as { code?: string } | undefined)?.code ?? "unavailable";
    } catch { device.view_error = "unavailable"; }
    return device;
  }

  private pushWindow(session: OpenSession, window: CallWindow): void {
    session.windows.push(window);
    if (session.windows.length > 256) session.windows.shift();
  }

  /**
   * Which recorded call an error at `ts` belongs to: the call whose window
   * contains it; else one within 50 ms of it (clock rounding, the stream's
   * latency); else the last call that ended before it ("after" — a tap's
   * consequence logged frames later).
   */
  private attribute(session: OpenSession, ts: number): { window: CallWindow; relation: "during" | "after" } | null {
    let near: CallWindow | undefined, nearGap = Infinity, before: CallWindow | undefined;
    for (const w of session.windows) {
      if (ts >= w.start && ts <= w.end) return { window: w, relation: "during" };
      const gap = ts < w.start ? w.start - ts : ts - w.end;
      if (gap <= 50 && gap < nearGap) { near = w; nearGap = gap; }
      if (w.end < ts && (!before || w.end > before.end)) before = w;
    }
    if (near) return { window: near, relation: "during" };
    return before ? { window: before, relation: "after" } : null;
  }

  /**
   * Attribute pending stream errors that can no longer belong to an in-flight
   * call (see attribute). An error the call's own response already reported
   * is still logged (it carries the stack) but not counted twice.
   */
  private settle(session: OpenSession, all = false): void {
    if (session.pending.length === 0) return;
    const earliestInflight = session.inflight.size && !all ? Math.min(...session.inflight.values()) : Infinity;
    const ready = session.pending.filter(e => e.ts < earliestInflight);
    if (ready.length === 0) return;
    session.pending = session.pending.filter(e => !ready.includes(e));
    const items = ready.map(e => {
      const target = this.attribute(session, e.ts);
      const key = messageKey(e.message);
      if (target && !(target.relation === "during" && target.window.messages.has(key))) session.sinceMark.errors++;
      if (target?.relation === "during") target.window.messages.add(key);
      return this.redactor.value({
        at: iso(e.ts), type: e.type, message: clip(e.message, 2_000), ...(e.stack ? { stack: clip(e.stack, 4_000) } : {}),
        seq: target?.window.seq ?? null, relation: target?.relation ?? "before_first_call", source: e.source,
      });
    });
    this.append(session, { k: "errors", items });
  }

  /** Errors logged inside the call's window: stream events first, then the response's own evidence. */
  private collectErrors(session: OpenSession, start: number, end: number, output: Record<string, any>, body: Record<string, any>): Array<Record<string, unknown>> {
    const out: Array<Record<string, unknown>> = [];
    const seen = new Set<string>();
    // From the call's start (an earlier error belongs to the call before it) to
    // shortly after its end; later stream arrivals are attributed by settle().
    const inWindow = session.pending.filter(e => e.ts >= start && e.ts <= end + 50);
    session.pending = session.pending.filter(e => !inWindow.includes(e));
    for (const e of inWindow) {
      seen.add(messageKey(e.message));
      out.push({ at: iso(e.ts), type: e.type, message: clip(e.message, 2_000), ...(e.stack ? { stack: clip(e.stack, 4_000) } : {}), source: "stream" });
    }
    const add = (type: string, message: string, stack?: string) => {
      const key = messageKey(message);
      if (seen.has(key)) return;
      seen.add(key);
      out.push({ type, message: clip(message, 2_000), ...(stack ? { stack: clip(stack, 4_000) } : {}), source: "response" });
    };
    const logs = Array.isArray(body.logs) ? body.logs : [];
    for (const log of logs)
      if (log && typeof log === "object" && log.severity === "error" && typeof log.message === "string") add("error", log.message);
    // A play-mode operation's own console capture (wait / series / gesture); an
    // argument-validation failure's `errors` are schema messages, not console.
    const operationErrors = output.operation !== undefined || output.series !== undefined || "errors_dropped" in output;
    if (operationErrors && Array.isArray(output.errors)) for (const e of output.errors) if (typeof e === "string") add("error", e);
    const exception = output.exception as Record<string, unknown> | undefined;
    if (exception && typeof exception.message === "string")
      add(String(exception.type ?? "exception").toLowerCase(), exception.message, typeof exception.stack === "string" ? exception.stack : undefined);
    return this.redactor.value(out.slice(0, RECORDING_LIMITS.maxErrorsPerCall));
  }

  /** Frames a call wrote (file paths anywhere in its output, inline images) → linked into the report folder. */
  private collectCaptures(session: OpenSession, seq: number, output: Record<string, any>, result: CallToolResult | null): Array<Record<string, unknown>> {
    const found: Array<{ source?: string; data?: string; label?: string; frame?: number; width?: number; height?: number }> = [];
    const walk = (value: unknown, path: string, depth: number) => {
      if (depth > 8 || !value || typeof value !== "object") return;
      if (Array.isArray(value)) { value.forEach((item, i) => walk(item, `${path}[${i}]`, depth + 1)); return; }
      const obj = value as Record<string, unknown>;
      for (const key of ["filePath", "file"]) {
        const file = obj[key];
        if (typeof file === "string" && /\.png$/i.test(file) && isAbsolute(file)) {
          found.push({
            source: file, label: /contact_sheet/.test(path) ? "contact sheet" : undefined,
            frame: typeof obj.frame === "number" ? obj.frame : undefined,
            width: typeof obj.width === "number" ? obj.width : undefined, height: typeof obj.height === "number" ? obj.height : undefined,
          });
        }
      }
      for (const [key, child] of Object.entries(obj)) if (typeof child === "object") walk(child, `${path}.${key}`, depth + 1);
    };
    walk(output, "", 0);
    for (const block of result?.content ?? [])
      if (block.type === "image" && typeof block.data === "string") found.push({ data: block.data, label: "inline" });
    const out: Array<Record<string, unknown>> = [];
    found.forEach((capture, index) => {
      const entry: Record<string, unknown> = {
        ...(capture.label ? { label: capture.label } : {}), ...(capture.frame !== undefined ? { frame: capture.frame } : {}),
        ...(capture.width ? { width: capture.width, height: capture.height } : {}),
        ...(capture.source ? { source: capture.source } : {}),
      };
      if (session.frames >= RECORDING_LIMITS.maxFrames || session.frameBytes >= RECORDING_LIMITS.maxFrameBytes) {
        session.framesSkipped++;
        this.noteLimit(session, "frames", `More than ${RECORDING_LIMITS.maxFrames} frames (or ${RECORDING_LIMITS.maxFrameBytes / 1024 / 1024} MB); later frames are referenced by their source path only.`);
        out.push({ ...entry, skipped: "limit" });
        return;
      }
      const name = `s${String(seq).padStart(3, "0")}-${String(index).padStart(2, "0")}-${capture.source ? basename(capture.source) : "inline.png"}`;
      const target = join(session.dir, REPORT_DIR, FRAMES_DIR, name);
      try {
        if (capture.data) writeFileSync(target, Buffer.from(capture.data, "base64"));
        else {
          try { linkSync(capture.source!, target); } catch { copyFileSync(capture.source!, target); }
        }
        const bytes = statSync(target).size;
        session.frames++;
        session.frameBytes += bytes;
        out.push({ ...entry, file: `${FRAMES_DIR}/${name}`, bytes });
      } catch (error) {
        out.push({ ...entry, missing: error instanceof Error ? error.message.slice(0, 200) : "copy failed" });
      }
    });
    return out;
  }

  private summarize(kind: CallKind, output: Record<string, any>): Record<string, unknown> | null {
    const summary: Record<string, unknown> = {};
    for (const key of SUMMARY_KEYS) {
      if (output[key] === undefined || output[key] === null) continue;
      if (key === "frames" && kind !== "wait") continue;
      summary[key] = boundedJson(output[key], 400);
    }
    if (kind === "command" && output.result !== undefined) summary.result = boundedJson(output.result, 1_000);
    if (output.series && typeof output.series === "object") {
      const series = output.series as Record<string, unknown>;
      summary.series = { captured: series.captured, requested: series.requested, schedule: series.schedule };
    }
    return Object.keys(summary).length ? this.redactor.value(summary) : null;
  }
}

const TOOL_BY_NAME = new Map(ALL_TOOLS.map(t => [t.name, t]));

/**
 * The arguments as the agent sent them: the server fills schema defaults in
 * before routing, so a top-level value equal to its schema default is dropped
 * (as are routing keys) — the report shows intent, not boilerplate.
 */
function asSent(label: string, args: Record<string, unknown>): Record<string, unknown> {
  const { _meta: _ignored, operation_id: _op, ...rest } = args;
  const properties = (TOOL_BY_NAME.get(label.split(" ")[0])?.inputSchema.properties ?? {}) as Record<string, { default?: unknown }>;
  for (const [key, value] of Object.entries(rest))
    if (properties[key]?.default !== undefined && JSON.stringify(properties[key].default) === JSON.stringify(value)) delete rest[key];
  return rest;
}
