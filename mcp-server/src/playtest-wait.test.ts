// playtest_wait / capture_series / gesture capture_at_ms: schema contract,
// group placement, the worst-case estimate that decides inline vs job, the
// inline route (terminal result, the operation's own failure code), the job
// route for long runs, the hand-over of a slow operation to a job that observes
// it (never a cancel), the bridge's stall end, the jobs adapter's failure code
// and the live client's unclamped tool-owned timeout_ms.
import { test } from "node:test";
import assert from "node:assert/strict";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { ToolRouter } from "./tool-router.js";
import { ToolSessionState } from "./tool-session-state.js";
import { ALL_TOOLS, PLAYTEST_TOOLS } from "./tools/index.js";
import type { LiveClient } from "./live-client.js";
import { TOOL_OWNED_TIMEOUT_TOOLS } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import type { BridgeEventStream } from "./event-stream.js";
import { groupFor } from "./capabilities/tool-groups.js";
import { recommendGroups } from "./capabilities/intent-groups.js";
import { JobManager } from "./jobs/job-manager.js";
import { playmodeEstimateMs, playmodeFailureCode, playmodeOperation, playmodeShouldHandOver, PlaymodeProgress, PLAYMODE_OPERATION_TOOLS } from "./jobs/adapters.js";
import { CONDITION_LEAVES, OBJECT_STATES } from "./tools/playtest-conditions.js";
import { PLAYTEST_WAIT_INLINE_LIMIT_MS } from "./tools/playtest-wait.js";

const WAIT = "unity_open_mcp_playtest_wait";
const SERIES = "unity_senses_capture_series";
const GESTURE = "unity_open_mcp_inputsim_gesture";
const tool = (name: string) => { const t = ALL_TOOLS.find(x => x.name === name); assert.ok(t, name); return t!; };
const props = (name: string) => tool(name).inputSchema.properties as Record<string, any>;
const body = (result: CallToolResult) => JSON.parse((result.content[0] as { text: string }).text);
const text = (value: unknown): CallToolResult => ({ content: [{ type: "text", text: JSON.stringify(value) }] });
const owner = { project: "/project", agent: "test" };

// ---------------------------------------------------------------------------
// Schema contract
// ---------------------------------------------------------------------------

test("wait and capture_series join the playtest group; the gesture stays in input-simulation", () => {
  assert.ok(PLAYTEST_TOOLS.some(t => t.name === WAIT));
  assert.ok(PLAYTEST_TOOLS.some(t => t.name === SERIES));
  assert.equal(groupFor(WAIT), "playtest");
  assert.equal(groupFor(SERIES), "playtest");
  assert.equal(groupFor(GESTURE), "input-simulation");
  for (const intent of ["wait until the wave is over", "capture series of the popup tween"]) {
    const rec = recommendGroups({ intent });
    assert.ok(rec.groups.some((g: { id: string }) => g.id === "playtest"), intent);
  }
});

test("wait schema: condition vocabulary, bounds and description promises", () => {
  const p = props(WAIT);
  assert.deepEqual(tool(WAIT).inputSchema.required, ["condition"]);
  for (const leaf of CONDITION_LEAVES) assert.ok(p.condition.properties[leaf], leaf);
  for (const key of ["all", "any"]) {
    assert.equal(p.condition.properties[key].type, "array");
    assert.equal(p.condition.properties[key].maxItems, 16);
    const nested = p.condition.properties[key].items;
    assert.equal(nested.properties.all.type, "array", "a child may be all / any");
    assert.equal(nested.properties.any.items.properties.all, undefined, "one nested level only");
  }
  assert.deepEqual(p.condition.properties.object.properties.state.enum, [...OBJECT_STATES]);
  assert.deepEqual(p.condition.properties.project_command.required, ["id"]);
  assert.equal(p.timeout_ms.maximum, 600_000);
  assert.equal(p.poll_every_frames.maximum, 600);
  assert.equal(p.time_scale.maximum, 100);
  assert.deepEqual(p.fail_on.items.enum, ["exception", "error"]);
  const description = tool(WAIT).description!;
  for (const phrase of ["instead of polling in a loop", "timed_out", "restored on every exit", "exception_thrown",
    "no arbitrary C#", "job_id", "Not allowed inside batch_execute"])
    assert.ok(description.includes(phrase), phrase);
});

test("capture_series schema: three schedule forms, bounded, start_on shares the condition shapes", () => {
  const p = props(SERIES);
  assert.equal(p.at_ms.maxItems, 120);
  assert.equal(p.at_frames.maxItems, 120);
  assert.equal(p.every_ms.minimum, 10);
  assert.deepEqual(Object.keys(p.start_on.properties).sort(), Object.keys(props(WAIT).condition.properties).sort());
  assert.equal(p.start_timeout_ms.default, undefined, "a materialized default must not imply start_on");
  assert.equal(p.region.minItems, 4);
  assert.deepEqual(p.metric.enum, ["luminance", "diff"]);
  assert.equal(p.contact_sheet.default, false);
  assert.ok(SERIES.startsWith("unity_senses_"), "live-only senses prefix");
  const description = tool(SERIES).description!;
  for (const phrase of ["inside the player loop", "contact_sheet", "unchanged", "capture_at_ms", "render_target_unavailable"])
    assert.ok(description.includes(phrase), phrase);
});

test("gesture schema gains capture_at_ms (bounded) next to its keyframes", () => {
  const p = props(GESTURE);
  assert.equal(p.capture_at_ms.type, "array");
  assert.equal(p.capture_at_ms.maxItems, 30);
  assert.equal(p.capture_at_ms.items.maximum, 300_000);
  assert.match(tool(GESTURE).description!, /capture_at_ms/);
});

// ---------------------------------------------------------------------------
// Estimates and failure codes
// ---------------------------------------------------------------------------

test("worst-case estimates: wait timeout, series trigger + schedule + encoding, gesture captures", () => {
  assert.equal(playmodeEstimateMs(WAIT, { condition: { frames: 1 } }), 30_000);
  assert.equal(playmodeEstimateMs(WAIT, { condition: { frames: 1 }, timeout_ms: 90_000 }), 90_000);
  assert.equal(playmodeEstimateMs(SERIES, { at_ms: [0, 100, 300] }), 300 + 3 * 250);
  assert.equal(playmodeEstimateMs(SERIES, { every_ms: 500, duration_ms: 30_000 }), 30_000 + 61 * 250);
  assert.equal(playmodeEstimateMs(SERIES, { at_frames: [0, 30] }), 1000 + 2 * 250);
  assert.equal(playmodeEstimateMs(SERIES, { at_ms: [0], start_on: { ms: 1 } }), 30_000 + 250);
  assert.equal(playmodeEstimateMs(SERIES, { at_ms: [0], start_on: { ms: 1 }, start_timeout_ms: 5_000 }), 5_000 + 250);
  const fingers = [{ path: [{ at_ms: 0 }, { at_ms: 50 }] }];
  assert.equal(playmodeEstimateMs(GESTURE, { fingers }), 50);
  assert.equal(playmodeEstimateMs(GESTURE, { fingers, capture_at_ms: [0, 400] }), 400 + 2 * 250);
});

test("a failed play-mode operation reports its own error_code, else the fallback; ended ones their reason", () => {
  assert.equal(playmodeFailureCode({ state: "failed", result: { error_code: "timed_out" } }), "timed_out");
  assert.equal(playmodeFailureCode({ state: "failed", result: {} }, "wait_failed"), "wait_failed");
  assert.equal(playmodeFailureCode({ state: "cancelled", reason: "play_mode_exited" }), "play_mode_exited");
});

test("the wait's timeout_ms is its own parameter: never clamped as a request budget", () => {
  assert.ok(TOOL_OWNED_TIMEOUT_TOOLS.has(WAIT));
  assert.ok(!TOOL_OWNED_TIMEOUT_TOOLS.has("unity_open_mcp_execute_csharp"));
});

// ---------------------------------------------------------------------------
// Inline route
// ---------------------------------------------------------------------------

/** Fake bridge: the tool call starts an operation; /playmode-operations answers from `states`. */
function fakeLive(states: Array<Record<string, unknown>>, startError?: Record<string, unknown>) {
  const calls = { starts: [] as Array<{ tool: string; args: Record<string, unknown> }>, status: 0, cancel: 0 };
  let index = 0;
  const live = {
    route: async (name: string, args: Record<string, unknown>) => {
      calls.starts.push({ tool: name, args });
      // A refused start is a failed gate envelope with the code in mutation.error.
      if (startError) return { ...text({ mutation: { success: false, output: null, error: startError.error }, agentNextSteps: [] }), isError: true };
      return text({ mutation: { success: true, output: { status: "ok", operation: { operation_id: args.operation_id, kind: name, state: "running" } }, error: null } });
    },
    playmodeOperation: async (args: { action: string; operation_id: string }) => {
      if (args.action === "cancel") { calls.cancel++; return { operation_id: args.operation_id, state: "cancelled", reason: "cancelled", result: { outcome: "cancelled" } }; }
      calls.status++;
      return { operation_id: args.operation_id, ...states[Math.min(index++, states.length - 1)] };
    },
  } as unknown as LiveClient;
  return { live, calls };
}

function router(live: LiveClient) {
  return new ToolRouter(live, {} as BatchSpawn, "/project", {} as BridgeEventStream, new ToolSessionState());
}

test("inline wait: polls to the terminal result and forwards timeout_ms unchanged", async t => {
  const fired = { condition: "log", frame: 300 };
  const { live, calls } = fakeLive([
    { state: "running", phase: "waiting" },
    { state: "succeeded", reason: "completed", result: { outcome: "fired", fired } },
  ]);
  const r = router(live); t.after(() => r.jobs.close());
  const result = await r.route(WAIT, { condition: { log: { pattern: "Timer done" } }, timeout_ms: 15_000 });
  const out = body(result);
  assert.notEqual(result.isError, true);
  assert.equal(out.status, "ok");
  assert.deepEqual(out.fired, fired);
  assert.equal(out.operation.state, "succeeded");
  assert.equal(calls.starts.length, 1);
  assert.equal(calls.starts[0].args.timeout_ms, 15_000);
  assert.match(String(calls.starts[0].args.operation_id), /^[0-9a-f-]{36}$/);
});

test("inline wait: a timeout is an error with code timed_out, the last observed state and next steps", async t => {
  const condition = { path: "condition", type: "ui", met: false, last: { count: 0 } };
  const { live } = fakeLive([{ state: "failed", reason: "failed",
    result: { outcome: "timed_out", error_code: "timed_out", error: "The condition did not hold within 1500 ms", condition } }]);
  const r = router(live); t.after(() => r.jobs.close());
  const result = await r.route(WAIT, { condition: { ui: { text_contains: "Victory" } }, timeout_ms: 1_500 });
  const out = body(result);
  assert.equal(result.isError, true);
  assert.equal(out.error.code, "timed_out");
  assert.match(out.error.message, /did not hold/);
  assert.deepEqual(out.condition, condition);
  assert.match(out.agentNextSteps[0], /do not re-poll in a loop/);
});

test("inline wait: an exception stop keeps its code and stack", async t => {
  const exception = { type: "Exception", message: "InvalidOperationException: boom", stack: "Game.Buy ()" };
  const { live } = fakeLive([{ state: "failed", reason: "failed", result: { outcome: "exception", error_code: "exception_thrown", error: "The game threw", exception } }]);
  const r = router(live); t.after(() => r.jobs.close());
  const out = body(await r.route(WAIT, { condition: { ms: 20_000 }, fail_on: ["exception"], timeout_ms: 25_000 }));
  assert.equal(out.error.code, "exception_thrown");
  assert.deepEqual(out.exception, exception);
});

test("inline wait: a bridge refusal passes through unchanged", async t => {
  const { live, calls } = fakeLive([], { error: { code: "play_mode_required", message: "play" } });
  const r = router(live); t.after(() => r.jobs.close());
  const out = body(await r.route(WAIT, { condition: { frames: 1 } }));
  assert.equal(out.mutation.success, false);
  assert.equal(out.mutation.error.code, "play_mode_required");
  assert.equal(calls.status, 0);
});

test("a wait longer than the inline budget starts a job instead", async t => {
  const { live, calls } = fakeLive([{ state: "running", phase: "waiting" }]);
  const r = router(live); t.after(() => r.jobs.close());
  const out = body(await r.route(WAIT, { condition: { ms: 120_000 }, timeout_ms: PLAYTEST_WAIT_INLINE_LIMIT_MS + 1 }));
  assert.equal(typeof out.job_id, "string");
  assert.equal(out.operation, WAIT);
  assert.match(out.note, /runs as a job/);
  assert.match(out.agentNextSteps[0], /unity_open_mcp_jobs/);
  await new Promise(resolve => setTimeout(resolve, 50));
  assert.equal(calls.starts[0]?.args.operation_id, out.job_id, "the job id is the bridge operation id");
  assert.equal(calls.starts[0]?.args.timeout_ms, PLAYTEST_WAIT_INLINE_LIMIT_MS + 1);
  await r.routeJobs({ action: "cancel", job_id: out.job_id });
  const done = body(await r.routeJobs({ action: "wait", job_id: out.job_id, timeout_ms: 5000 }));
  assert.equal(done.state, "cancelled");
  assert.equal(calls.cancel, 1);
});

test("a long capture series runs as a job; a short one inline", async t => {
  const { live } = fakeLive([{ state: "succeeded", reason: "completed", result: { outcome: "completed", series: { captured: 4 } } }]);
  const r = router(live); t.after(() => r.jobs.close());
  const inline = body(await r.route(SERIES, { at_ms: [0, 100, 200, 300], contact_sheet: true }));
  assert.equal(inline.status, "ok");
  assert.equal(inline.series.captured, 4);
  const job = body(await r.route(SERIES, { every_ms: 500, duration_ms: 30_000 }));
  assert.equal(typeof job.job_id, "string");
});

// ---------------------------------------------------------------------------
// Jobs adapter
// ---------------------------------------------------------------------------

test("jobs adapter serves wait and capture_series and validates their schemas", () => {
  for (const name of [WAIT, SERIES]) {
    const schema = PLAYMODE_OPERATION_TOOLS.get(name);
    assert.ok(schema, name);
    const op = playmodeOperation(name, schema!, () => { throw new Error("unused"); });
    assert.equal(op.cancellable, true);
    assert.throws(() => op.validate({ operation_id: "abc" }), /own operation_id/);
  }
  const wait = playmodeOperation(WAIT, PLAYMODE_OPERATION_TOOLS.get(WAIT)!, () => { throw new Error("unused"); });
  assert.throws(() => wait.validate({}), /condition is required/);
  assert.throws(() => wait.validate({ condition: { frames: 1 }, timeout_ms: 600_001 }), /maximum/);
  wait.validate({ condition: { any: [{ ms: 5 }, { log: { pattern: "x" } }] }, timeout_ms: 600_000 });
});

test("a wait job that timed out fails with timed_out and keeps the last observed state", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  const condition = { type: "object", met: false, last: { found: true } };
  const { live } = fakeLive([{ state: "failed", reason: "failed", result: { outcome: "timed_out", error_code: "timed_out", error: "did not hold", condition } }]);
  manager.register(WAIT, playmodeOperation(WAIT, PLAYMODE_OPERATION_TOOLS.get(WAIT)!, () => live, { pollIntervalMs: 1 }));
  const done = await manager.wait(owner, manager.start(owner, WAIT, { condition: { object: { path: "Boss", state: "destroyed" } }, timeout_ms: 90_000 }).job_id, 2000);
  assert.equal(done.state, "failed");
  assert.equal(done.error?.code, "timed_out");
  assert.deepEqual((done.result as Record<string, any>).condition, condition);
});

test("a wait job cancelled mid-wait is cancelled (the bridge restores the time scale)", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  const { live, calls } = fakeLive([{ state: "running", phase: "waiting" }]);
  manager.register(WAIT, playmodeOperation(WAIT, PLAYMODE_OPERATION_TOOLS.get(WAIT)!, () => live, { pollIntervalMs: 5 }));
  const job = manager.start(owner, WAIT, { condition: { ms: 120_000 }, time_scale: 2, timeout_ms: 120_000 });
  await new Promise(resolve => setTimeout(resolve, 20));
  manager.cancel(owner, job.job_id);
  const done = await manager.wait(owner, job.job_id, 2000);
  assert.equal(done.state, "cancelled");
  assert.equal(calls.cancel, 1);
});

test("a series job lost to a domain reload is orphaned, never restarted", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  const { live, calls } = fakeLive([{ error: { code: "operation_not_found", message: "reloaded" } }]);
  manager.register(SERIES, playmodeOperation(SERIES, PLAYMODE_OPERATION_TOOLS.get(SERIES)!, () => live, { pollIntervalMs: 1 }));
  const done = await manager.wait(owner, manager.start(owner, SERIES, { every_ms: 500, duration_ms: 60_000 }).job_id, 2000);
  assert.equal(done.state, "orphaned");
  assert.equal(calls.starts.length, 1);
});

// ---------------------------------------------------------------------------
// Slow is not stalled: the remaining-time estimate, the hand-over to a job that
// observes the running operation, and the bridge's stall end
// ---------------------------------------------------------------------------

test("progress estimate: the operation clock's rate against the wall clock, measured from when it began moving", () => {
  let now = 0;
  const progress = new PlaymodeProgress(250, () => now);
  const snap = (elapsedMs: number, phase = "capturing") => ({ phase, progress: { clock: "gameplay", elapsedMs, expectedMs: 8000, stalledMs: 0 } });
  // A start_on wait: nothing moves, so there is no rate yet.
  assert.equal(progress.observe(snap(0, "waiting for start_on")).etaMs, null);
  now = 5_000;
  assert.equal(progress.observe(snap(0, "waiting for start_on")).rate, null);
  now = 6_000;
  const first = progress.observe(snap(250));
  assert.equal(first.rate, 0.25, "measured from the last still sample");
  now = 8_000;
  const e = progress.observe(snap(750));
  assert.equal(e.rate, 0.25);
  assert.equal(e.spanMs, 3_000);
  assert.equal(e.etaMs, (8000 - 750) / 0.25 + 250);
  // Encoding: the schedule is done, only the tail remains.
  assert.equal(progress.observe(snap(8000, "encoding")).etaMs, 250);
});

test("hand-over decision: a slow operation goes to a job early, a fast one stays inline, the limit always hands over", () => {
  const est = (rate: number | null, etaMs: number | null, spanMs: number) =>
    ({ clock: "gameplay", phase: "capturing", elapsedMs: 100, expectedMs: 8000, rate, etaMs, stalledMs: 0, spanMs });
  assert.equal(playmodeShouldHandOver(est(0.25, 31_600, 1_200), 1_500, 50_000), false, "fits inside the limit");
  assert.equal(playmodeShouldHandOver(est(0.1, 79_000, 1_200), 1_500, 50_000), true, "slow: decided early");
  assert.equal(playmodeShouldHandOver(est(0.1, 79_000, 400), 600, 50_000), false, "the rate is not trusted yet");
  assert.equal(playmodeShouldHandOver(est(null, null, 0), 10_000, 50_000), false, "no progress: the bridge decides");
  assert.equal(playmodeShouldHandOver(est(null, null, 0), 50_000, 50_000), true, "the inline limit always hands over");
  assert.equal(playmodeShouldHandOver(est(0.1, 79_000, 5_000), 5_000, Infinity), false, "the CLI waits inline throughout");
  const still = { ...est(0.05, 160_000, 12_000), stalledMs: 9_000 };
  assert.equal(playmodeShouldHandOver(still, 12_000, 50_000), false, "standing still (paused): the bridge's stall window decides, not a decaying rate");
  assert.equal(playmodeShouldHandOver(still, 50_000, 50_000), true);
});

/** Fake bridge whose running series advances at `rate` × wall time until `finish()` makes it terminal. */
function slowSeries(rate: number, expectedMs: number) {
  const calls = { starts: [] as Array<Record<string, unknown>>, status: 0, cancel: 0 };
  const startedAt = Date.now();
  let terminal: Record<string, unknown> | null = null;
  const live = {
    route: async (name: string, args: Record<string, unknown>) => {
      calls.starts.push(args);
      return text({ status: "ok", operation: { operation_id: args.operation_id, kind: name, state: "running" } });
    },
    playmodeOperation: async (args: { action: string; operation_id: string }) => {
      if (args.action === "cancel") { calls.cancel++; return { operation_id: args.operation_id, state: "cancelled", reason: "cancelled" }; }
      calls.status++;
      if (terminal) return { operation_id: args.operation_id, ...terminal };
      const elapsedMs = Math.min(expectedMs, (Date.now() - startedAt) * rate);
      return { operation_id: args.operation_id, state: "running", phase: "capturing",
        progress: { clock: "gameplay", elapsedMs, expectedMs, stalledMs: 0, ticks: calls.status,
          series: { schedule: "at_ms", time: "gameplay", requested: 1, captured: 0, frames: [] } } };
    },
  } as unknown as LiveClient;
  return { live, calls, finish: (state: Record<string, unknown>) => { terminal = state; } };
}

test("a series slower than its schedule continues as a job under its operation id: never cancelled, never restarted", async t => {
  const fake = slowSeries(0.01, 8000);
  const r = router(fake.live); t.after(() => r.jobs.close());
  const began = Date.now();
  const result = await r.route(SERIES, { at_ms: [8000], time: "gameplay" });
  const out = body(result);
  assert.notEqual(result.isError, true);
  assert.ok(Date.now() - began < 5_000, "decided early from the observed rate, not at the inline limit");
  assert.equal(out.status, "running");
  assert.equal(out.job_id, out.operationId, "the job continues the operation under its id");
  assert.equal(out.job.operation, SERIES);
  assert.equal(out.series.requested, 1);
  assert.equal(out.progress.clock, "gameplay");
  assert.ok(out.progress.eta_ms > 50_000, "the answer says when to expect the result");
  assert.match(out.note, /continues as job/);
  assert.match(out.agentNextSteps[0], /Do not start the capture series again/);
  assert.equal(fake.calls.cancel, 0, "a progressing series is never cancelled");
  // The job observes the running operation; finishing it completes the job with the series payload.
  fake.finish({ state: "succeeded", reason: "completed", phase: "succeeded",
    result: { outcome: "completed", series: { requested: 1, captured: 1, time: "gameplay", frames: [{ index: 0, file: "/tmp/f.png" }] } } });
  const done = body(await r.routeJobs({ action: "wait", job_id: out.job_id, timeout_ms: 5000 }));
  assert.equal(done.state, "succeeded");
  assert.equal(done.result.series.captured, 1);
  assert.equal(done.result.series.time, "gameplay");
  assert.equal(fake.calls.starts.length, 1, "the job started nothing");
  assert.equal(fake.calls.cancel, 0);
});

test("a stalled series fails with capture_series_stalled, why it stood still, and the frames it kept", async t => {
  const stalled = { state: "failed", reason: "stalled", stall: { reason: "time_scale_zero", stalledMs: 15000 },
    result: { outcome: "stalled", error_code: "capture_series_stalled", error: "Time.timeScale is 0: gameplay time did not advance for 15000 ms while frames ran.",
      series: { requested: 2, captured: 1, frames: [{ index: 0, file: "/f0.png" }, { index: 1, captured: false, dropped: "stalled" }] } } };
  const { live, calls } = fakeLive([{ state: "running", phase: "capturing" }, stalled]);
  const r = router(live); t.after(() => r.jobs.close());
  const result = await r.route(SERIES, { at_ms: [0, 1000] });
  const out = body(result);
  assert.equal(result.isError, true);
  assert.equal(out.error.code, "capture_series_stalled");
  assert.equal(out.error.reason, "time_scale_zero");
  assert.match(out.error.message, /Time\.timeScale is 0/);
  assert.equal(out.series.captured, 1);
  assert.match(out.agentNextSteps[0], /Time\.timeScale is 0/);
  assert.equal(calls.cancel, 0, "the bridge ended it; the route never cancels");
});

test("a series cut short by leaving play mode keeps its frames with play_mode_exited", async t => {
  const { live } = fakeLive([{ state: "cancelled", reason: "play_mode_exited",
    result: { outcome: "play_mode_exited", series: { requested: 4, captured: 2, frames: [] } } }]);
  const r = router(live); t.after(() => r.jobs.close());
  const out = body(await r.route(SERIES, { at_ms: [0, 100, 200, 300] }));
  assert.equal(out.error.code, "play_mode_exited");
  assert.match(out.error.message, /2 of 4 frames were captured and are kept/);
  assert.equal(out.series.captured, 2);
  assert.match(out.agentNextSteps[0], /Play mode ended mid-series/);
});

test("a stalled series job fails with the bridge's code; its result names the stall", async t => {
  const manager = new JobManager(); t.after(() => manager.close());
  const { live } = fakeLive([{ state: "failed", reason: "stalled", stall: { reason: "editor_paused", stalledMs: 15000 },
    result: { outcome: "stalled", error_code: "capture_series_stalled", error: "The Editor is paused: no play-mode frame ran for 15000 ms." } }]);
  manager.register(SERIES, playmodeOperation(SERIES, PLAYMODE_OPERATION_TOOLS.get(SERIES)!, () => live, { pollIntervalMs: 1 }));
  const done = await manager.wait(owner, manager.start(owner, SERIES, { at_ms: [60_000] }).job_id, 2000);
  assert.equal(done.state, "failed");
  assert.equal(done.error?.code, "capture_series_stalled");
  assert.match(done.error!.message, /Editor is paused/);
  assert.equal((done.result as Record<string, any>).operation.stall.reason, "editor_paused");
});

test("the CLI (no host timeout) runs a long play-mode operation inline instead of as a job", async t => {
  const { live, calls } = fakeLive([{ state: "succeeded", reason: "completed", result: { outcome: "completed", series: { captured: 61 } } }]);
  const r = router(live); t.after(() => r.jobs.close());
  r.waitInlineLimitMs = Number.POSITIVE_INFINITY;
  const out = body(await r.route(SERIES, { every_ms: 500, duration_ms: 30_000 }));
  assert.equal(out.status, "ok");
  assert.equal(out.series.captured, 61);
  assert.equal(calls.starts.length, 1);
});
