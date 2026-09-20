import { randomUUID } from "node:crypto";

export const JOB_STATES = ["queued", "running", "succeeded", "failed", "cancel_requested", "cancelled", "orphaned"] as const;
export type JobState = typeof JOB_STATES[number];
export interface JobOwner { project: string; agent: string; port?: number }
export interface JobError { code: string; message: string }
export type JobOutcome = { state: "succeeded"; result: unknown } | { state: "failed"; error: JobError; result?: unknown } | { state: "cancelled"; result?: unknown };
export interface JobContext {
  jobId: string;
  owner: JobOwner;
  signal: AbortSignal;
  report(progress: { phase: string; fraction?: number }): void;
  /** Call on disconnect/reload. Only operation-specific ownership evidence permits continuation. */
  lifecycle(state: "connected" | "reloading" | "disconnected", evidence: string, ownershipProven: boolean): void;
}
export interface JobOperation {
  mutating: boolean;
  cancellable: boolean;
  /** Pure preflight. Execution must preserve the owning operation's scope/gate/validation contract. */
  validate(args: Record<string, unknown>): void;
  /** Must yield promptly; use callbacks/async transport, never a blocking main-thread operation. */
  run(args: Record<string, unknown>, context: JobContext): Promise<JobOutcome>;
}
export interface JobSnapshot {
  job_id: string;
  operation: string;
  owner: JobOwner;
  state: JobState;
  created_at: number;
  updated_at: number;
  started_at: number | null;
  finished_at: number | null;
  progress: { phase: string; fraction?: number } | null;
  cancellable: boolean;
  lifecycle: { state: "not_started" | "connected" | "reloading" | "disconnected"; evidence: string | null };
  events: Array<{ at: number; kind: string; detail: string }>;
  result?: unknown;
  error?: JobError;
}
interface Entry {
  view: JobSnapshot;
  sequence: number;
  key?: string;
  fingerprint: string;
  args: Record<string, unknown>;
  operation: JobOperation;
  controller: AbortController;
  executing: boolean;
  waiters: Set<() => void>;
}
export class JobManagerError extends Error {
  readonly code: string;
  constructor(code: string, message: string) { super(message); this.code = code; }
}
const terminal = (state: JobState) => ["succeeded", "failed", "cancelled", "orphaned"].includes(state);
const sameProject = (a: JobOwner, b: JobOwner) => a.project === b.project && a.port === b.port;
const sameOwner = (a: JobOwner, b: JobOwner) => sameProject(a, b) && a.agent === b.agent;
function canonical(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
  if (value && typeof value === "object") return `{${Object.keys(value).sort().map(k => `${JSON.stringify(k)}:${canonical((value as Record<string, unknown>)[k])}`).join(",")}}`;
  return JSON.stringify(value);
}
export interface JobManagerOptions {
  maxJobs: number; retentionMs: number; maxConcurrent: number; perProject: number; perAgent: number;
  maxEvents: number; maxPayloadBytes: number; maxWaiters: number;
}
const defaults: JobManagerOptions = {
  maxJobs: 256, retentionMs: 30 * 60_000, maxConcurrent: 4, perProject: 1, perAgent: 1,
  maxEvents: 32, maxPayloadBytes: 256 * 1024, maxWaiters: 128,
};

/** Session-only owner. No replay, disk persistence, or automatic transport retries. */
export class JobManager {
  private readonly operations = new Map<string, JobOperation>();
  private readonly entries = new Map<string, Entry>();
  private readonly keys = new Map<string, string>();
  private sequence = 0;
  private readonly session = randomUUID();
  private readonly options: JobManagerOptions;
  private readonly cleanupTimer: ReturnType<typeof setInterval>;
  private closed = false;
  private readonly now: () => number;
  constructor(options: Partial<JobManagerOptions> = {}, now = Date.now) {
    this.now = now;
    this.options = { ...defaults, ...options };
    for (const value of Object.values(this.options)) if (!Number.isSafeInteger(value) || value < 1) throw new Error("Job limits must be positive integers");
    this.cleanupTimer = setInterval(() => this.cleanup(), Math.min(this.options.retentionMs, 60_000));
    this.cleanupTimer.unref();
  }
  hasOperation(name: string): boolean { return this.operations.has(name); }
  register(name: string, operation: JobOperation): void {
    if (this.operations.has(name)) throw new Error(`Duplicate job operation: ${name}`);
    this.operations.set(name, operation);
  }
  private copy<T>(value: T): T {
    const json = JSON.stringify(value);
    if (json === undefined || Buffer.byteLength(json) > this.options.maxPayloadBytes)
      throw new JobManagerError("job_payload_too_large", "Job payload exceeds the configured JSON byte limit.");
    return JSON.parse(json) as T;
  }
  private event(entry: Entry, kind: string, detail: string): void {
    entry.view.updated_at = this.now();
    entry.view.events.push({ at: entry.view.updated_at, kind, detail: detail.slice(0, 1024) });
    entry.view.events = entry.view.events.slice(-this.options.maxEvents);
  }
  private transition(entry: Entry, state: JobState): void {
    if (terminal(entry.view.state)) return;
    entry.view.state = state;
    this.event(entry, "state", state);
    if (terminal(state)) {
      entry.view.finished_at = this.now();
      entry.args = {};
      for (const wake of entry.waiters) wake();
      entry.waiters.clear();
    }
  }
  private snapshot(entry: Entry): JobSnapshot { return structuredClone(entry.view); }
  private find(owner: JobOwner, id: string): Entry {
    this.cleanup();
    const entry = this.entries.get(id);
    if (!entry || !sameOwner(owner, entry.view.owner)) throw new JobManagerError("job_not_found", "Job is unknown, expired, or belongs to another project/agent.");
    return entry;
  }
  start(owner: JobOwner, name: string, args: Record<string, unknown>, key?: string): JobSnapshot {
    if (this.closed) throw new JobManagerError("jobs_closed", "Job manager is closed.");
    if (!owner.project || !owner.agent) throw new JobManagerError("invalid_job_owner", "Project and agent identity are required.");
    this.cleanup();
    const safeArgs = this.copy(args);
    const fingerprint = canonical({ name, args: safeArgs });
    const scopedKey = key === undefined ? undefined : JSON.stringify([owner.project, owner.port ?? null, owner.agent, key]);
    const existing = scopedKey === undefined ? undefined : this.keys.get(scopedKey);
    if (existing) {
      const entry = this.entries.get(existing)!;
      if (entry.fingerprint !== fingerprint) throw new JobManagerError("idempotency_conflict", "Key already identifies a different operation or arguments.");
      return this.snapshot(entry);
    }
    const operation = this.operations.get(name);
    if (!operation) throw new JobManagerError("job_operation_unsupported", "Operation has no registered asynchronous job adapter.");
    if ((operation.mutating || key !== undefined) && (!key?.trim() || key.length > 256))
      throw new JobManagerError("idempotency_key_required", "Provide a nonempty idempotency_key of at most 256 characters.");
    operation.validate(safeArgs);
    // Never evict unexpired idempotency records to make room for new starts.
    if (this.entries.size >= this.options.maxJobs) throw new JobManagerError("job_capacity", "Job retention capacity reached; retry after terminal records expire.");
    const at = this.now();
    const entry: Entry = {
      view: { job_id: randomUUID(), operation: name, owner: { ...owner }, state: "queued", created_at: at, updated_at: at,
        started_at: null, finished_at: null, progress: null, cancellable: operation.cancellable,
        lifecycle: { state: "not_started", evidence: null }, events: [] },
      sequence: ++this.sequence, key: scopedKey, fingerprint, args: safeArgs, operation,
      controller: new AbortController(), executing: false, waiters: new Set(),
    };
    this.entries.set(entry.view.job_id, entry);
    if (scopedKey !== undefined) this.keys.set(scopedKey, entry.view.job_id);
    this.event(entry, "state", "queued");
    // Scheduling is independent of the lifetime of start/wait MCP calls.
    setImmediate(() => this.pump());
    return this.snapshot(entry);
  }
  status(owner: JobOwner, id: string): JobSnapshot { return this.snapshot(this.find(owner, id)); }
  async wait(owner: JobOwner, id: string, timeoutMs = 10_000): Promise<JobSnapshot> {
    if (!Number.isInteger(timeoutMs) || timeoutMs < 0 || timeoutMs > 30_000) throw new JobManagerError("invalid_arguments", "wait timeout_ms must be between 0 and 30000.");
    const entry = this.find(owner, id);
    if (terminal(entry.view.state) || timeoutMs === 0) return this.snapshot(entry);
    const waiters = [...this.entries.values()].reduce((sum, e) => sum + e.waiters.size, 0);
    if (waiters >= this.options.maxWaiters) throw new JobManagerError("job_wait_capacity", "Too many concurrent observers.");
    await new Promise<void>(resolve => {
      const wake = () => { clearTimeout(timer); entry.waiters.delete(wake); resolve(); };
      const timer = setTimeout(wake, timeoutMs);
      entry.waiters.add(wake);
    });
    return this.snapshot(entry);
  }
  cancel(owner: JobOwner, id: string): JobSnapshot {
    const entry = this.find(owner, id);
    if (!entry.operation.cancellable) throw new JobManagerError("not_cancellable", "Operation has no cooperative cancellation contract.");
    if (terminal(entry.view.state) || entry.view.state === "cancel_requested") return this.snapshot(entry);
    if (entry.view.state === "queued") this.transition(entry, "cancelled");
    else {
      this.transition(entry, "cancel_requested");
      entry.controller.abort();
    }
    return this.snapshot(entry);
  }
  list(owner: JobOwner, filters: { state?: JobState; operation?: string } = {}, cursor?: string, limit = 20): { jobs: JobSnapshot[]; next_cursor: string | null } {
    this.cleanup();
    if (!Number.isInteger(limit) || limit < 1 || limit > 100) throw new JobManagerError("invalid_arguments", "limit must be 1..100.");
    const binding = canonical({ owner, filters, session: this.session });
    let after = 0, ceiling = this.sequence;
    if (cursor) {
      try {
        const parsed = JSON.parse(Buffer.from(cursor, "base64url").toString());
        if (parsed.binding !== binding || !Number.isSafeInteger(parsed.after) || !Number.isSafeInteger(parsed.ceiling) || parsed.after < 0 || parsed.ceiling < parsed.after || parsed.ceiling > this.sequence) throw new Error();
        after = parsed.after; ceiling = parsed.ceiling;
      } catch { throw new JobManagerError("invalid_cursor", "Cursor does not match this owner/filter or server session."); }
    }
    const matches = [...this.entries.values()].filter(e => sameOwner(owner, e.view.owner) && e.sequence > after && e.sequence <= ceiling
      && (!filters.state || e.view.state === filters.state) && (!filters.operation || e.view.operation === filters.operation));
    const page = matches.slice(0, limit);
    return { jobs: page.map(e => this.snapshot(e)), next_cursor: matches.length > limit
      ? Buffer.from(JSON.stringify({ binding, after: page.at(-1)!.sequence, ceiling })).toString("base64url") : null };
  }
  cleanup(): void {
    for (const [id, entry] of this.entries) {
      if (!entry.executing && entry.view.finished_at !== null && this.now() - entry.view.finished_at >= this.options.retentionMs) {
        this.entries.delete(id);
        if (entry.key !== undefined) this.keys.delete(entry.key);
      }
    }
  }
  close(): void {
    this.closed = true;
    clearInterval(this.cleanupTimer);
    for (const entry of this.entries.values()) if (!terminal(entry.view.state)) {
      entry.view.error = { code: "job_owner_lost", message: "Server job manager closed; execution outcome is unknown." };
      this.transition(entry, "orphaned");
    }
  }
  private pump(): void {
    if (this.closed) return;
    for (const entry of this.entries.values()) {
      if (entry.view.state !== "queued") continue;
      const active = [...this.entries.values()].filter(e => e.executing);
      if (active.length >= this.options.maxConcurrent) break;
      if (active.filter(e => sameProject(e.view.owner, entry.view.owner)).length >= this.options.perProject ||
          active.filter(e => sameOwner(e.view.owner, entry.view.owner)).length >= this.options.perAgent) continue;
      entry.executing = true;
      entry.view.started_at = this.now();
      this.transition(entry, "running");
      void this.execute(entry);
    }
  }
  private async execute(entry: Entry): Promise<void> {
    const context: JobContext = {
      jobId: entry.view.job_id, owner: { ...entry.view.owner }, signal: entry.controller.signal,
      report: progress => {
        if (terminal(entry.view.state)) return;
        if (typeof progress.phase !== "string" || (progress.fraction !== undefined && (!Number.isFinite(progress.fraction) || progress.fraction < 0 || progress.fraction > 1)))
          throw new JobManagerError("invalid_progress", "Progress requires a phase and optional fraction in [0,1].");
        entry.view.progress = { phase: progress.phase.slice(0, 1024),
          ...(progress.fraction === undefined ? {} : { fraction: progress.fraction }) };
        this.event(entry, "progress", progress.phase);
      },
      lifecycle: (state, evidence, ownershipProven) => {
        if (terminal(entry.view.state)) return;
        entry.view.lifecycle = { state, evidence: evidence.slice(0, 1024) };
        this.event(entry, "lifecycle", `${state}: ${evidence}`);
        if (!ownershipProven || !evidence.trim()) {
          entry.view.error = { code: "job_owner_lost", message: "Operation ownership cannot be proven; do not blindly retry." };
          this.transition(entry, "orphaned");
        }
      },
    };
    try {
      const outcome = await entry.operation.run(entry.args, context);
      if (terminal(entry.view.state)) return;
      if ("result" in outcome) entry.view.result = this.copy(outcome.result);
      if (outcome.state === "failed") entry.view.error = this.copy(outcome.error);
      else if (outcome.state !== "succeeded" && (outcome.state !== "cancelled" || !entry.operation.cancellable || !entry.controller.signal.aborted))
        throw new Error("Invalid terminal outcome or cancellation without acknowledgement");
      this.transition(entry, outcome.state);
    } catch (error) {
      if (!terminal(entry.view.state)) {
        // A thrown transport/runtime error does not prove that remote work failed.
        entry.view.error = { code: "job_outcome_unknown", message: String(error).slice(0, 1024) };
        this.transition(entry, "orphaned");
      }
    } finally {
      entry.executing = false;
      this.pump();
    }
  }
}
