// playtest_wait / capture_series / gesture capture_at_ms: schema contract,
// group placement, the worst-case estimate that decides inline vs job, the
// inline route (terminal result, the operation's own failure code, stalled
// cancel), the job route for long runs, the jobs adapter's failure code and
// the live client's unclamped tool-owned timeout_ms.
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
import { playmodeEstimateMs, playmodeFailureCode, playmodeOperation, PLAYMODE_OPERATION_TOOLS } from "./jobs/adapters.js";
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
    assert.equal(p.condition.properties[key].items.properties.all, undefined, "one level deep");
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
      if (startError) return { ...text(startError), isError: true };
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
  assert.equal(out.error.code, "play_mode_required");
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
