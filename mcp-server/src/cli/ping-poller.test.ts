// Tests for the compile-aware bridge readiness poller (src/cli/ping-poller.ts).
//
// The poller is the heart of `wait-for-ready` and `ping`: it must return ready
// only when /ping says connected AND not compiling, keep waiting through a
// 503/compiling state, fail fast on a dead-bridge signature, and respect the
// deadline. All of those are testable without a network by injecting the
// single-poll stub and a fake clock.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { projectHash } from "../instance-discovery.js";

import {
  pollUntilReady,
  extractPingBody,
  type SinglePollResult,
  type PollOptions,
} from "./ping-poller.js";
import type { LiveClient } from "../live-client.js";

// A controllable fake clock. The poller calls now() for the deadline check and
// sleep() between polls; we drive both so a 120s timeout test runs instantly.
function makeFakeClock(startMs: number) {
  let t = startMs;
  const sleeps: number[] = [];
  return {
    now: () => t,
    sleep: (ms: number) => {
      sleeps.push(ms);
      t += ms;
      return Promise.resolve();
    },
    elapsed: () => t - startMs,
    sleeps,
  };
}

function fakeLive(): LiveClient {
  // The poller only uses the project-path argument; the LiveClient itself is
  // passed to singlePoll as an opaque token. We return a minimal stub.
  return {} as LiveClient;
}

// A scripted sequence of poll outcomes. When exhausted, repeats the last one.
function scriptPoll(results: SinglePollResult[]) {
  let i = 0;
  return async (): Promise<SinglePollResult> => {
    const r = results[Math.min(i, results.length - 1)];
    i++;
    return r;
  };
}

function makeOpts(clock: ReturnType<typeof makeFakeClock>): PollOptions {
  return {
    timeoutMs: 10_000,
    intervalMs: 1_000,
    now: clock.now,
    sleep: clock.sleep,
  };
}

const READY_BODY = { connected: true, compiling: false, isPlaying: false };
const COMPILING_BODY = { connected: true, compiling: true, isPlaying: false };
const OFFLINE_RESULT: SinglePollResult = { status: "offline", body: null };

// ---------------------------------------------------------------------------
// ready cases
// ---------------------------------------------------------------------------

test("pollUntilReady: returns ready immediately when first ping is ready", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    fakeLive(),
    undefined,
    scriptPoll([{ status: "ready", body: READY_BODY }]),
    makeOpts(clock),
  );
  assert.equal(outcome.ready, true);
  assert.equal(outcome.status, "ready");
  assert.equal(outcome.elapsedMs, 0);
  // No sleeps when ready on the first try.
  assert.deepEqual(clock.sleeps, []);
});

test("pollUntilReady: waits through compiling then becomes ready", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    fakeLive(),
    undefined,
    scriptPoll([
      { status: "compiling", body: COMPILING_BODY },
      { status: "compiling", body: COMPILING_BODY },
      { status: "ready", body: READY_BODY },
    ]),
    makeOpts(clock),
  );
  assert.equal(outcome.ready, true);
  assert.equal(outcome.status, "ready");
  // Two compile polls → two interval sleeps (1000ms each).
  assert.equal(outcome.elapsedMs, 2_000);
  assert.deepEqual(clock.sleeps, [1_000, 1_000]);
  assert.deepEqual(outcome.lastPing, READY_BODY);
});

test("pollUntilReady: treats connected:false as offline (not ready)", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    fakeLive(),
    undefined,
    scriptPoll([{ status: "offline", body: { connected: false } }]),
    { ...makeOpts(clock), timeoutMs: 500 },
  );
  assert.equal(outcome.ready, false);
  assert.equal(outcome.status, "timeout");
});

// ---------------------------------------------------------------------------
// timeout cases
// ---------------------------------------------------------------------------

test("pollUntilReady: times out when never ready", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    fakeLive(),
    undefined,
    scriptPoll([OFFLINE_RESULT]),
    { ...makeOpts(clock), timeoutMs: 3_000, intervalMs: 1_000 },
  );
  assert.equal(outcome.ready, false);
  assert.equal(outcome.status, "timeout");
  assert.match(outcome.reason, /never became reachable/i);
});

test("pollUntilReady: timeout reason mentions compiling when that was the stall", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    fakeLive(),
    undefined,
    scriptPoll([{ status: "compiling", body: COMPILING_BODY }]),
    { ...makeOpts(clock), timeoutMs: 3_000, intervalMs: 1_000 },
  );
  assert.equal(outcome.status, "timeout");
  assert.match(outcome.reason, /still compiling/i);
});

test("pollUntilReady: deadline check happens before the first poll when already past", async () => {
  // Edge case: if now() is already at/over the deadline, no poll runs.
  const clock = makeFakeClock(5_000);
  const outcome = await pollUntilReady(
    fakeLive(),
    undefined,
    scriptPoll([{ status: "ready", body: READY_BODY }]),
    { ...makeOpts(clock), timeoutMs: 0 },
  );
  assert.equal(outcome.ready, false);
  assert.equal(outcome.status, "timeout");
});

// ---------------------------------------------------------------------------
// dead-bridge fail-fast
// ---------------------------------------------------------------------------

test("pollUntilReady: fail-fast on dead_bridge when projectPath resolves a stale lock", async () => {
  // Plant a dead-bridge lock for this test's project path: live PID (the test
  // runner) but a heartbeat far in the past. classifyInstance reads the
  // heartbeat relative to Date.now(); an ISO string from 2000 is well past the
  // stale threshold.
  const projectPath = "/fake/project/for/dead-bridge-test";
  const hash = projectHash(projectPath);
  const dir = join(homedir(), ".unity-open-mcp", "instances");
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
  const lockPath = join(dir, `${hash}.json`);
  const existedBefore = existsSync(lockPath);
  writeFileSync(lockPath, JSON.stringify({
    pid: process.pid,
    port: 29999,
    projectPath,
    projectHash: hash,
    startedAt: "2000-01-01T00:00:00.000Z",
    updatedAt: "2000-01-01T00:00:00.000Z",
    heartbeatAt: "2000-01-01T00:00:00.000Z",
    state: "idle",
    isPlaying: false,
    isCompiling: false,
    bridgeVersion: "0.1.0",
    unityVersion: "6000.0.0f1",
  }));

  try {
    const clock = makeFakeClock(0);
    const outcome = await pollUntilReady(
      fakeLive(),
      projectPath,
      scriptPoll([OFFLINE_RESULT]),
      makeOpts(clock),
    );
    assert.equal(outcome.ready, false);
    assert.equal(outcome.status, "dead_bridge");
    assert.match(outcome.reason, /failed to recompile/i);
  } finally {
    if (!existedBefore) {
      try { rmSync(lockPath, { force: true }); } catch { /* best effort */ }
    }
  }
});

test("pollUntilReady: skips dead-bridge check when projectPath is undefined", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    fakeLive(),
    undefined, // no project path → no lock read
    scriptPoll([OFFLINE_RESULT]),
    { ...makeOpts(clock), timeoutMs: 500 },
  );
  assert.equal(outcome.status, "timeout");
});

// ---------------------------------------------------------------------------
// extractPingBody
// ---------------------------------------------------------------------------

test("extractPingBody: parses a text content ping body", () => {
  const body = extractPingBody({
    content: [{ type: "text", text: '{"connected":true,"compiling":false}' }],
  });
  assert.deepEqual(body, { connected: true, compiling: false });
});

test("extractPingBody: returns null for missing/invalid text", () => {
  assert.equal(extractPingBody({ content: [] }), null);
  assert.equal(
    extractPingBody({ content: [{ type: "image", text: "x" }] }),
    null,
  );
  assert.equal(
    extractPingBody({ content: [{ type: "text", text: "not-json" }] }),
    null,
  );
});

// ---------------------------------------------------------------------------
// sanity: projectHash + createHash agree (guards the test helper itself)
// ---------------------------------------------------------------------------

test("projectHash matches createHash(normalizePath) for the dead-bridge sample", () => {
  // If this ever drifts, the dead-bridge test above plants a lock at the wrong
  // path and the fail-fast assertion silently no-ops.
  const projectPath = "/fake/project/for/dead-bridge-test";
  const norm = projectPath.replace(/\\/g, "/").replace(/\/+$/, "") || "/";
  const expected = createHash("sha256").update(norm, "utf8").digest("hex");
  assert.equal(projectHash(projectPath), expected);
});
