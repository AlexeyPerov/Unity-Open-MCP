import { test } from "node:test";
import assert from "node:assert/strict";
import { setImmediate as tick } from "node:timers/promises";
import type { LiveClient } from "../live-client.js";
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
