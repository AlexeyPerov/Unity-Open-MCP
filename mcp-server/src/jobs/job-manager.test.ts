import { test } from "node:test";
import assert from "node:assert/strict";
import { setImmediate as tick } from "node:timers/promises";
import { JobManager, type JobContext, type JobOutcome, type JobOperation } from "./job-manager.js";
const owner = { project: "/project", agent: "a" };
function fixture(cancellable = true) {
  const pending: Array<{ context: JobContext; finish: (outcome: JobOutcome) => void }> = [];
  const operation: JobOperation = { mutating: true, cancellable, validate: () => {},
    run: (_args, context) => new Promise(resolve => pending.push({ context, finish: resolve })) };
  return { operation, pending };
}
function manager(t: { after(fn: () => void): void }, options = {}) {
  const m = new JobManager(options); t.after(() => m.close()); return m;
}

test("start returns queued before execution; concurrent duplicate starts run once and result is retained", async t => {
  const m = manager(t), f = fixture(); m.register("operation", f.operation);
  assert.throws(() => m.start(owner, "operation", {}), { code: "idempotency_key_required" });
  const job = m.start(owner, "operation", { b: 1, a: 2 }, "key");
  assert.equal(job.state, "queued"); assert.equal(f.pending.length, 0);
  const duplicates = await Promise.all(Array.from({ length: 20 }, async () => m.start(owner, "operation", { a: 2, b: 1 }, "key")));
  assert.ok(duplicates.every(j => j.job_id === job.job_id));
  assert.throws(() => m.start(owner, "operation", { a: 3 }, "key"), { code: "idempotency_conflict" });
  await tick(); assert.equal(f.pending.length, 1);
  f.pending[0].context.report({ phase: "work", fraction: 0.5 });
  const timeout = await m.wait(owner, job.job_id, 1);
  assert.equal(timeout.state, "running"); assert.equal(timeout.progress?.fraction, 0.5);
  const waiter = m.wait(owner, job.job_id, 1000);
  f.pending[0].finish({ state: "succeeded", result: { answer: 42 } });
  const completed = await waiter;
  assert.equal(completed.state, "succeeded"); assert.deepEqual(completed.result, { answer: 42 });
  assert.notEqual(completed.started_at, null); assert.notEqual(completed.finished_at, null);
  f.pending[0].context.report({ phase: "late" });
  f.pending[0].context.lifecycle("disconnected", "late", false);
  assert.deepEqual(m.status(owner, job.job_id), completed);
  assert.equal(m.start(owner, "operation", { a: 2, b: 1 }, "key").job_id, job.job_id);
});

test("cancel is cooperative, queued cancellation prevents dispatch, and unsupported cancellation never changes state", async t => {
  const m = manager(t), f = fixture(), unsupported = fixture(false);
  m.register("cooperative", f.operation); m.register("unsupported", unsupported.operation);
  const running = m.start(owner, "cooperative", {}, "r");
  const queued = m.start(owner, "cooperative", {}, "q");
  const no = m.start(owner, "unsupported", {}, "n");
  assert.throws(() => m.cancel(owner, no.job_id), { code: "not_cancellable" });
  assert.equal(m.status(owner, no.job_id).state, "queued");
  assert.equal(m.cancel(owner, queued.job_id).state, "cancelled");
  await tick(); assert.equal(f.pending.length, 1);
  assert.equal(m.cancel(owner, running.job_id).state, "cancel_requested");
  assert.ok(f.pending[0].context.signal.aborted);
  assert.equal(m.cancel(owner, running.job_id).state, "cancel_requested");
  f.pending[0].finish({ state: "cancelled" }); await tick();
  assert.equal(m.status(owner, running.job_id).state, "cancelled");
  assert.equal(unsupported.pending.length, 1);
  assert.throws(() => m.cancel(owner, no.job_id), { code: "not_cancellable" });
  unsupported.pending[0].finish({ state: "succeeded", result: null }); await tick();
  assert.equal(m.status(owner, no.job_id).state, "succeeded");
});

test("cancel race may truthfully succeed; explicit failures stay failed", async t => {
  const m = manager(t), f = fixture(); m.register("operation", f.operation);
  const j = m.start(owner, "operation", {}, "one"); await tick(); m.cancel(owner, j.job_id);
  f.pending[0].finish({ state: "succeeded", result: "already completed" }); await tick();
  assert.equal(m.status(owner, j.job_id).state, "succeeded");
  const k = m.start(owner, "operation", {}, "two"); await tick();
  f.pending[1].finish({ state: "failed", error: { code: "build_failed", message: "Compiler rejected input" } }); await tick();
  assert.equal(m.cancel(owner, k.job_id).state, "failed");
});

test("reload requires ownership evidence, unknown ownership is terminal and never redispatched", async t => {
  const m = manager(t), f = fixture(); m.register("operation", f.operation);
  const j = m.start(owner, "operation", {}, "one"); await tick();
  const ctx = f.pending[0].context;
  ctx.lifecycle("reloading", "native operation token 123 remains owned", true);
  assert.equal(m.status(owner, j.job_id).state, "running");
  ctx.lifecycle("connected", "native token 123 matched", true);
  f.pending[0].finish({ state: "succeeded", result: "reload result" }); await tick();
  assert.equal(m.status(owner, j.job_id).state, "succeeded");
  const lost = m.start(owner, "operation", {}, "lost"); await tick();
  f.pending[1].context.lifecycle("disconnected", "socket closed; no operation token", false);
  assert.equal(m.status(owner, lost.job_id).state, "orphaned");
  assert.equal(m.start(owner, "operation", {}, "lost").job_id, lost.job_id);
  f.pending[1].finish({ state: "succeeded", result: "late" }); await tick();
  assert.equal(m.status(owner, lost.job_id).state, "orphaned"); assert.equal(f.pending.length, 2);
});

test("transport rejection is orphaned, never a fabricated failure", async t => {
  const m = manager(t);
  m.register("broken", { mutating: false, cancellable: false, validate() {}, async run() { throw new Error("socket reset"); } });
  const job = m.start(owner, "broken", {}); await tick();
  assert.equal(m.status(owner, job.job_id).state, "orphaned");
});

test("global/project/agent concurrency and ownership isolation", async t => {
  const m = manager(t, { maxConcurrent: 2, perProject: 2, perAgent: 1 }), f = fixture(); m.register("operation", f.operation);
  const a = m.start(owner, "operation", {}, "same");
  m.start(owner, "operation", {}, "queued");
  const bOwner = { ...owner, agent: "b" };
  const b = m.start(bOwner, "operation", {}, "same");
  const cOwner = { ...owner, project: "/other" };
  const c = m.start(cOwner, "operation", {}, "same");
  await tick(); assert.equal(f.pending.length, 2);
  assert.equal(m.status(owner, a.job_id).state, "running"); assert.equal(m.status(bOwner, b.job_id).state, "running");
  assert.equal(m.status(cOwner, c.job_id).state, "queued");
  for (const wrong of [bOwner, cOwner]) {
    assert.throws(() => m.status(wrong, a.job_id), { code: "job_not_found" });
    assert.throws(() => m.cancel(wrong, a.job_id), { code: "job_not_found" });
    await assert.rejects(m.wait(wrong, a.job_id, 0), { code: "job_not_found" });
    assert.ok(m.list(wrong).jobs.every(j => j.job_id !== a.job_id));
  }
  f.pending[1].finish({ state: "succeeded", result: null }); await tick();
  assert.equal(m.status(cOwner, c.job_id).state, "running");
});

test("project concurrency serializes different agents and orphaned unresolved execution keeps its slot", async t => {
  const m = manager(t), f = fixture(); m.register("operation", f.operation);
  m.start(owner, "operation", {}, "one");
  const other = { ...owner, agent: "b" }, second = m.start(other, "operation", {}, "two");
  await tick(); assert.equal(f.pending.length, 1);
  f.pending[0].context.lifecycle("disconnected", "unknown ownership", false); await tick();
  assert.equal(m.status(other, second.job_id).state, "queued");
  f.pending[0].finish({ state: "succeeded", result: null }); await tick(); assert.equal(f.pending.length, 2);
});

test("bounded events, immutable snapshots, payload limits and observer limits", async t => {
  const m = manager(t, { maxEvents: 3, maxPayloadBytes: 256, maxWaiters: 1 }), f = fixture(); m.register("operation", f.operation);
  assert.throws(() => m.start(owner, "operation", { big: "x".repeat(300) }, "big"), { code: "job_payload_too_large" });
  const job = m.start(owner, "operation", {}, "one"); await tick();
  for (let i = 0; i < 10; i++) f.pending[0].context.report({ phase: `${i}` });
  const snapshot = m.status(owner, job.job_id); assert.equal(snapshot.events.length, 3);
  snapshot.owner.agent = "hacked"; snapshot.events.length = 0;
  assert.equal(m.status(owner, job.job_id).events.length, 3);
  const wait = m.wait(owner, job.job_id, 1000);
  await assert.rejects(m.wait(owner, job.job_id, 1), { code: "job_wait_capacity" });
  await assert.rejects(m.wait(owner, job.job_id, 30001), { code: "invalid_arguments" });
  m.close(); assert.equal((await wait).state, "orphaned");
});

test("retention never evicts active or unexpired keys; expiry removes result and key together", async t => {
  let now = 0;
  const m = new JobManager({ maxJobs: 2, retentionMs: 10 }, () => now); t.after(() => m.close());
  const f = fixture(); m.register("operation", f.operation);
  const a = m.start(owner, "operation", {}, "a"); await tick();
  const b = m.start(owner, "operation", {}, "b");
  assert.throws(() => m.start(owner, "operation", {}, "c"), { code: "job_capacity" });
  now = 20; m.cleanup(); assert.equal(m.status(owner, a.job_id).state, "running");
  f.pending[0].finish({ state: "succeeded", result: 1 }); await tick();
  now = 29; assert.equal(m.start(owner, "operation", {}, "a").job_id, a.job_id);
  now = 30; m.cleanup(); assert.throws(() => m.status(owner, a.job_id), { code: "job_not_found" });
  assert.equal(m.status(owner, b.job_id).state, "running");
  assert.notEqual(m.start(owner, "operation", {}, "a").job_id, a.job_id);
});

test("paging is deterministic, bound to owner/filter/session and excludes later starts", t => {
  const m = manager(t), f = fixture(); m.register("operation", f.operation);
  const ids = ["a", "b", "c"].map(key => m.start(owner, "operation", {}, key).job_id);
  const first = m.list(owner, {}, undefined, 1); assert.deepEqual(first.jobs.map(j => j.job_id), ids.slice(0, 1));
  m.start(owner, "operation", {}, "later");
  const next = m.list(owner, {}, first.next_cursor!, 10); assert.deepEqual(next.jobs.map(j => j.job_id), ids.slice(1));
  assert.equal(next.next_cursor, null);
  assert.throws(() => m.list({ ...owner, agent: "b" }, {}, first.next_cursor!), { code: "invalid_cursor" });
  assert.throws(() => m.list(owner, { state: "running" }, first.next_cursor!), { code: "invalid_cursor" });
  const restarted = manager(t);
  assert.throws(() => restarted.list(owner, {}, first.next_cursor!), { code: "invalid_cursor" });
  assert.throws(() => restarted.status(owner, ids[0]), { code: "job_not_found" });
});

test("invalid preflight does not reserve a key; oversized terminal data cannot masquerade as success", async t => {
  const m = manager(t, { maxPayloadBytes: 128 });
  m.register("bounded", { mutating: true, cancellable: false,
    validate(args) { if (args.invalid) throw new Error("invalid adapter args"); },
    async run() { return { state: "succeeded", result: "x".repeat(129) }; } });
  assert.throws(() => m.start(owner, "bounded", { invalid: true }, "key"), /invalid adapter args/);
  assert.equal(m.list(owner).jobs.length, 0);
  const job = m.start(owner, "bounded", {}, "key"); await tick();
  assert.equal(m.status(owner, job.job_id).state, "orphaned");
  assert.equal(m.status(owner, job.job_id).result, undefined);
});
