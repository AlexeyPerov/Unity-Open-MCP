import { test } from "node:test";
import assert from "node:assert/strict";
import { setImmediate as tick } from "node:timers/promises";
import { ProjectJobHttpError, type LiveClient } from "../live-client.js";
import { JobManager } from "./job-manager.js";
import { projectCommandOperation, testRunOperation } from "./adapters.js";
const owner = { project: "/project", agent: "test" };
const catalog = { command: { available: true, async: true, cancellable: true, schemaVersion: "v1", inputSchema: { type: "object" } } };

test("project job retries start once, cancellation retains terminal gate and bounded phase events", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  let starts = 0, cancels = 0;
  const terminal = { gate: { checkpointId: "cp", delta: { newErrors: 0 } } };
  const live = { projectCommands: async () => catalog, projectCommandJob: async (args: any) => {
    if (args.action === "start") starts++;
    if (args.action === "cancel") cancels++;
    return { job_id: args.job_id, phase: "waiting for safe boundary", state: cancels ? "cancelled" : "running", result: cancels ? terminal : null };
  } } as unknown as LiveClient;
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => live));
  const args = { args: {}, gate: "enforce", paths_hint: ["Assets/Fixture"] };
  const job = manager.start(owner, "project.test.async", args, "same");
  await tick();
  assert.equal(manager.start(owner, "project.test.async", args, "same").job_id, job.job_id);
  assert.equal(manager.cancel(owner, job.job_id).state, "cancel_requested");
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "cancelled"); assert.deepEqual(done.result, terminal);
  assert.equal(starts, 1); assert.equal(cancels, 1);
  assert.equal(done.progress?.fraction, undefined);
  assert.ok(done.events.length <= 32);
});

test("project job losing bridge record is orphaned and never starts again", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  let starts = 0;
  const live = { projectCommands: async () => catalog, projectCommandJob: async (args: any) => {
    if (args.action === "start") { starts++; return { job_id: args.job_id, state: "running", phase: "preparing" }; }
    return { error: { code: "job_not_found", message: "Reloaded" } };
  } } as unknown as LiveClient;
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => live));
  const job = manager.start(owner, "project.test.async", {}, "same");
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "orphaned");
  assert.equal(manager.start(owner, "project.test.async", {}, "same").job_id, job.job_id);
  assert.equal(starts, 1);
});

test("project job that never reaches a terminal state is cancelled once and orphaned at the deadline", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  let cancels = 0;
  const live = { projectCommands: async () => catalog, projectCommandJob: async (args: any) => {
    if (args.action === "cancel") cancels++;
    return { job_id: args.job_id, state: "running", phase: "still preparing" };
  } } as unknown as LiveClient;
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => live, { deadlineMs: 40, pollIntervalMs: 5 }));
  const job = manager.start(owner, "project.test.async", {}, "same");
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "orphaned");
  assert.equal(done.lifecycle.state, "disconnected");
  assert.match(done.lifecycle.evidence ?? "", /no terminal state/);
  assert.equal(cancels, 1);
  // The execution slot is released: a new job for the same project can start.
  const next = manager.start(owner, "project.test.async", { args: { other: true } }, "next");
  await manager.wait(owner, next.job_id, 2000);
  assert.notEqual(manager.status(owner, next.job_id).state, "queued");
});

test("project job keeps polling through failed status polls and retains the bridge's terminal result", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  let starts = 0, polls = 0;
  const terminal = { gate: { checkpointId: "cp", delta: { newErrors: 0 } } };
  const live = { projectCommands: async () => catalog, projectCommandJob: async (args: any) => {
    if (args.action === "start") { starts++; return { job_id: args.job_id, state: "running", phase: "executing" }; }
    polls++;
    // A main thread blocked past the bridge's dispatch timeout answers 500, then no connection at all.
    if (polls === 1) throw new ProjectJobHttpError(500);
    if (polls === 2) throw new TypeError("fetch failed");
    return polls === 3 ? { job_id: args.job_id, state: "running", phase: "settling" } : { job_id: args.job_id, state: "succeeded", phase: "succeeded", result: terminal };
  } } as unknown as LiveClient;
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => live, { pollIntervalMs: 5 }));
  const job = manager.start(owner, "project.test.async", {}, "busy-main-thread");
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "succeeded"); assert.deepEqual(done.result, terminal);
  assert.equal(starts, 1); assert.equal(polls, 4);
  const lifecycle = done.events.filter(e => e.kind === "lifecycle").map(e => e.detail.split(":")[0]);
  assert.deepEqual(lifecycle, ["connected", "disconnected", "connected"]);
  assert.equal(done.lifecycle.state, "connected");
});

test("cancel lost to a busy bridge is not resent; later status polls observe its acknowledgement", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  let cancels = 0, statusPolls = 0;
  const live = { projectCommands: async () => catalog, projectCommandJob: async (args: any) => {
    if (args.action === "cancel") { cancels++; throw new ProjectJobHttpError(500); }
    if (args.action === "status") statusPolls++;
    return { job_id: args.job_id, state: statusPolls ? "cancelled" : "running", phase: "waiting for safe boundary", result: statusPolls ? {} : null };
  } } as unknown as LiveClient;
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => live, { pollIntervalMs: 5 }));
  const job = manager.start(owner, "project.test.async", {}, "cancel-busy");
  await tick();
  assert.equal(manager.cancel(owner, job.job_id).state, "cancel_requested");
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "cancelled");
  assert.equal(cancels, 1); assert.equal(statusPolls, 1);
});

test("project job is orphaned only after the bridge stays unreachable, and at once on an HTTP 4xx", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  let polls = 0;
  const unreachable = { projectCommands: async () => catalog, projectCommandJob: async (args: any) => {
    if (args.action === "start") return { job_id: args.job_id, state: "running", phase: "executing" };
    polls++; throw new ProjectJobHttpError(503);
  } } as unknown as LiveClient;
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => unreachable, { pollIntervalMs: 5, unreachableMs: 60 }));
  const job = manager.start(owner, "project.test.async", {}, "unreachable");
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "orphaned"); assert.ok(polls > 1);
  assert.match(done.lifecycle.evidence ?? "", /no job poll for 60 ms/);
  await tick();
  assert.ok(manager.status(owner, job.job_id).events.some(e => e.kind === "adapter_outcome" && e.detail.startsWith("job_status_unavailable")));

  let refused = 0;
  const unauthorized = { projectCommands: async () => catalog, projectCommandJob: async (args: any) => {
    if (args.action === "start") return { job_id: args.job_id, state: "running", phase: "executing" };
    refused++; throw new ProjectJobHttpError(401);
  } } as unknown as LiveClient;
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => unauthorized, { pollIntervalMs: 5 }), true);
  const next = manager.start(owner, "project.test.async", {}, "unauthorized");
  const lost = await manager.wait(owner, next.job_id, 2000);
  assert.equal(lost.state, "orphaned"); assert.equal(lost.error?.code, "job_outcome_unknown");
  assert.equal(refused, 1);
});

test("bridge preflight rejection is a known failure with its gate envelope", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  const result = { mutation: { success: false, error: { code: "invalid_paths", message: "scope" } }, gate: { skipped: true } };
  const live = { projectCommands: async () => catalog, projectCommandJob: async () => result } as unknown as LiveClient;
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => live));
  const job = manager.start(owner, "project.test.async", {}, "same");
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "failed"); assert.equal(done.error?.code, "invalid_paths"); assert.deepEqual(done.result, result);
  assert.equal(done.lifecycle.state, "not_started");
});

test("test adapter preserves legacy result, owns run id, requires deduplication and refuses cancellation", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  let starts = 0;
  const live = { runTestsJob: async (args: any, started: () => void) => {
    started();
    starts++; assert.ok(args.run_id); assert.equal(args.timeout_ms, 600000);
    return { content: [{ type: "text", text: JSON.stringify({ status: "completed", runId: args.run_id, summary: { failed: 0, total: 1 } }) }] };
  } } as unknown as LiveClient;
  manager.register("tests", testRunOperation(() => live));
  assert.throws(() => manager.start(owner, "tests", {}, undefined), { code: "idempotency_key_required" });
  assert.throws(() => manager.start(owner, "tests", { run_id: "custom" }, "bad"), { code: "invalid_arguments" });
  const job = manager.start(owner, "tests", {}, "same");
  assert.throws(() => manager.cancel(owner, job.job_id), { code: "not_cancellable" });
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "succeeded"); assert.equal(starts, 1);
  assert.equal(manager.start(owner, "tests", {}, "same").job_id, job.job_id);
});

for (const terminal of [
  { status: "completed", summary: { failed: 1, total: 2 } },
  { status: "aborted", summary: { failed: 0, total: 2 } },
]) test(`test adapter retains ${terminal.status}/${terminal.summary.failed} failure without inventing a gate`, async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  const live = { runTestsJob: async (args: any, started: () => void) => {
    started(); return { content: [{ type: "text", text: JSON.stringify({ ...terminal, runId: args.run_id }) }] };
  } } as unknown as LiveClient;
  manager.register("tests", testRunOperation(() => live));
  const job = manager.start(owner, "tests", {}, "failure");
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "failed");
  assert.equal((done.result as any).status, terminal.status);
  assert.equal((done.result as any).gate, undefined);
});

test("project partial-output failure retains terminal validation across observations and retries", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  let starts = 0;
  const result = { mutation: { success: false, partialCommit: true, error: { code: "execution_error" } },
    gate: { checkpointId: "one-checkpoint", validation: { complete: true } } };
  const live = { projectCommands: async () => catalog, projectCommandJob: async (args: any) => {
    if (args.action === "start") starts++;
    return { job_id: args.job_id, state: "failed", result };
  } } as unknown as LiveClient;
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => live));
  const job = manager.start(owner, "project.test.async", {}, "partial");
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "failed"); assert.deepEqual(done.result, result);
  assert.deepEqual(manager.status(owner, job.job_id), done);
  assert.deepEqual(manager.start(owner, "project.test.async", {}, "partial"), done);
  assert.equal(starts, 1);
});

for (const changed of [
  { available: false }, { async: false }, { cancellable: false },
  { inputSchema: { type: "object", required: ["required"] } },
]) test(`project job rejects changed declaration before transport: ${JSON.stringify(changed)}`, async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  const live = { projectCommands: async () => ({ command: { ...catalog.command, ...changed } }),
    projectCommandJob: async () => assert.fail("Rejected declaration must not execute") } as unknown as LiveClient;
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => live));
  const job = manager.start(owner, "project.test.async", {}, "invalid");
  assert.equal((await manager.wait(owner, job.job_id, 2000)).state, "failed");
});

test("project job forwards a cancel while sleeping between polls, not after the backoff", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  let cancels = 0;
  const live = { projectCommands: async () => catalog, projectCommandJob: async (args: any) => {
    if (args.action === "cancel") { cancels++; return { job_id: args.job_id, state: "cancelled", phase: "cancelled", result: { gate: null } }; }
    return { job_id: args.job_id, state: "running", phase: "preparing" };
  } } as unknown as LiveClient;
  // A 1.2 s poll interval: without an abortable sleep the cancel would only
  // go out after the first backoff, well past the bounded wait below.
  manager.register("project.test.async", projectCommandOperation("project.test.async", true, () => live, { pollIntervalMs: 1200 }));
  const job = manager.start(owner, "project.test.async", {}, "same");
  await tick();
  assert.equal(manager.cancel(owner, job.job_id).state, "cancel_requested");
  const done = await manager.wait(owner, job.job_id, 600);
  assert.equal(done.state, "cancelled");
  assert.equal(cancels, 1);
});
