// The editor-state reader and the one wait primitive (editor_status
// wait_until, the jobs wait, the router's mid-reload policy and the CLI
// wait-for-ready all run waitForEditor).

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  viewEditor,
  waitForEditor,
  describeWait,
  waitCall,
  reloadingRefusal,
  lostResponseResult,
  PENDING_COMPILE_GRACE_MS,
  AFTER_GENERATION_GRACE_MS,
  MODE_GRACE_MS,
  type CompileReading,
  type EditorSignals,
  type WaitDeps,
} from "./editor-state.js";
import type { InstanceLock, LockCompile } from "./instance-discovery.js";
import type { BridgeLossEvidence } from "./bridge-loss.js";
import type { PingProbe } from "./live-client.js";

const NOW = Date.parse("2026-11-14T12:00:00.000Z");

function lock(state: string, extra: Partial<InstanceLock> = {}, compile: Partial<LockCompile> | null = {}): InstanceLock {
  return {
    pid: process.pid, port: 27999, projectPath: "/p", projectHash: "h", startedAt: "", updatedAt: "",
    heartbeatAt: new Date(NOW).toISOString(), state: state as InstanceLock["state"],
    isPlaying: state === "playing", isCompiling: state === "compiling", bridgeVersion: "1.6.0", unityVersion: "6000.3",
    compile: compile === null ? null : { generation: 7, running: false, completed: true, failed: false, errorCount: 0, pending: false, requestedAt: null, ...compile },
    ...extra,
  };
}
const ping = (body: Record<string, unknown> | null, httpStatus: number | null = body ? 200 : null): PingProbe =>
  ({ reachable: httpStatus !== null, httpStatus, body: body as PingProbe["body"] });
const settledPing = (extra: Record<string, unknown> = {}) =>
  ping({ connected: true, compiling: false, isPlaying: false, transition: null, ...extra });
function signals(l: InstanceLock | null, p: PingProbe, classification: EditorSignals["classification"] = l ? "healthy" : "gone",
  bridgeLoss?: BridgeLossEvidence | null): EditorSignals {
  return { at: NOW, lock: l, classification, ping: p, ...(bridgeLoss !== undefined ? { bridgeLoss } : {}) };
}
const SAFE_MODE: BridgeLossEvidence = { kind: "safe_mode", detail: "Editor.log shows Unity in Safe Mode.", logPath: "/l/Editor.log" };
const staleLock = (state: string) => lock(state, { heartbeatAt: new Date(NOW - 45_000).toISOString() });

// ---------------------------------------------------------------------------
// viewEditor
// ---------------------------------------------------------------------------

test("viewEditor: lock transition states win over a settled-looking /ping", () => {
  for (const state of ["compiling", "reloading", "entering_playmode", "exiting_playmode"]) {
    const v = viewEditor(signals(lock(state), settledPing()));
    assert.equal(v.phase, "transition", state);
    assert.equal(v.transition, state);
  }
});

test("viewEditor: /ping transition and 503 count as transitions; unreachable listener with a fresh lock reads as reloading only when the lock says so", () => {
  assert.equal(viewEditor(signals(lock("idle"), settledPing({ transition: "entering_playmode" }))).transition, "entering_playmode");
  const initializing = viewEditor(signals(lock("idle"), ping({ connected: false, compiling: true }, 503)));
  assert.equal(initializing.phase, "transition");
  assert.equal(viewEditor(signals(lock("reloading"), ping(null), "reloading")).phase, "transition");
  assert.equal(viewEditor(signals(lock("idle"), ping(null))).phase, "unreachable");
  assert.equal(viewEditor(signals(null, ping(null))).phase, "gone");
  assert.equal(viewEditor(signals(lock("idle"), settledPing())).phase, "settled");
});

test("viewEditor: a stale heartbeat is a dead bridge only with Editor.log evidence", () => {
  // Evidence (Safe Mode, a reload that finished without the bridge) + no listener: dead.
  const dead = viewEditor(signals(staleLock("reloading"), ping(null), "dead_bridge", SAFE_MODE));
  assert.equal(dead.phase, "dead_bridge");
  assert.deepEqual(dead.bridgeLoss, SAFE_MODE);
  assert.equal(dead.stalledMs, 45_000);
  // No evidence: a reload that runs long is a transition, never a dead bridge.
  const longReload = viewEditor(signals(staleLock("reloading"), ping(null), "dead_bridge", null));
  assert.equal(longReload.phase, "transition");
  assert.equal(longReload.transition, "reloading");
  assert.equal(longReload.bridgeLoss, undefined);
  assert.equal(viewEditor(signals(staleLock("entering_playmode"), ping(null), "dead_bridge", null)).transition, "entering_playmode");
  // Outside a transition with no evidence: an unreachable listener (retry), not a compile failure.
  assert.equal(viewEditor(signals(staleLock("idle"), ping(null), "dead_bridge", null)).phase, "unreachable");
  // /ping answers: a blocked main thread.
  assert.equal(viewEditor(signals(staleLock("idle"), settledPing(), "dead_bridge")).phase, "wedged");
  // A test run's reload.
  assert.equal(viewEditor(signals(staleLock("idle"), ping(null), "dead_bridge", null), true).phase, "transition");
});

// ---------------------------------------------------------------------------
// waitForEditor
// ---------------------------------------------------------------------------

/** A scripted Editor: each poll consumes one frame (the last one repeats). */
function scripted(frames: Array<{ s: EditorSignals; c?: CompileReading | null }>) {
  let t = NOW;
  let i = 0;
  const deps: WaitDeps & { polls: () => number } = {
    signals: async () => frames[Math.min(i++, frames.length - 1)].s,
    compileState: async () => {
      const frame = frames[Math.min(i - 1, frames.length - 1)];
      return frame.c === undefined ? { status: "no_errors_found", generation: 7, errorCount: 0, sourceMatches: true, pending: false } : frame.c;
    },
    now: () => t,
    sleep: async (ms: number) => { t += ms; },
    polls: () => i,
  };
  return { deps, elapsed: () => t - NOW };
}

test("wait: returns once settled for two consecutive polls, with the compile reading", async () => {
  const { deps } = scripted([{ s: signals(lock("idle"), settledPing()) }]);
  const out = await waitForEditor({ until: "idle", timeoutMs: 10_000 }, deps);
  assert.equal(out.reached, true);
  assert.equal(deps.polls(), 2);
  assert.equal(out.compile?.status, "no_errors_found");
  assert.equal(out.compileRan, null);
});

test("wait: holds through compiling → reloading (listener down) → initializing and only then returns", async () => {
  const frames = [
    { s: signals(lock("compiling"), settledPing({ compiling: true })) },
    { s: signals(lock("reloading"), ping(null), "reloading") },
    { s: signals(lock("reloading"), ping(null), "reloading") },
    { s: signals(lock("idle"), ping({ connected: false, compiling: true }, 503)) },
    { s: signals(lock("idle", {}, { generation: 8 }), settledPing()), c: { status: "no_errors_found", generation: 8, errorCount: 0, sourceMatches: true, pending: false } },
  ];
  const { deps } = scripted(frames);
  const out = await waitForEditor({ until: "idle", afterGeneration: 7, timeoutMs: 30_000 }, deps);
  assert.equal(out.reached, true);
  assert.deepEqual(out.transitionsSeen.sort(), ["compiling", "reloading"]);
  assert.equal(out.compileRan, true);
  assert.equal(out.compile?.generation, 8);
});

test("wait: the gap before a requested compile starts is not idle (the handshake)", async () => {
  // A refresh requested a compile: isCompiling already reads false, /ping is
  // settled, but the lock and /compile-state say a compile is pending.
  const requestedAt = new Date(NOW).toISOString();
  const gap = { s: signals(lock("idle", {}, { pending: true, requestedAt }), settledPing()), c: { status: "assembly_stale", generation: 7, errorCount: 0, sourceMatches: false, pending: true } };
  const frames = [gap, gap, gap, gap, gap, gap,
    { s: signals(lock("compiling", {}, { generation: 8, running: true }), settledPing({ compiling: true })) },
    { s: signals(lock("idle", {}, { generation: 8 }), settledPing()), c: { status: "no_errors_found", generation: 8, errorCount: 0, sourceMatches: true, pending: false } },
  ];
  const { deps } = scripted(frames);
  const out = await waitForEditor({ until: "idle", timeoutMs: 60_000 }, deps);
  assert.equal(out.reached, true);
  assert.equal(out.compile?.generation, 8, "returned only after the requested compile ran");
  assert.ok(deps.polls() >= 8);
});

test("wait: a pending compile that never starts is given up after its grace window, and the answer says so", async () => {
  const requestedAt = new Date(NOW).toISOString();
  const gap = { s: signals(lock("idle", {}, { pending: true, requestedAt }), settledPing()), c: { status: "no_errors_found", generation: 7, errorCount: 0, sourceMatches: true, pending: true } };
  const { deps, elapsed } = scripted([gap]);
  const out = await waitForEditor({ until: "idle", timeoutMs: 120_000, pollMs: 1_000 }, deps);
  assert.ok(deps.polls() > 2);
  assert.equal(out.reached, true);
  assert.equal(out.compileDueNotStarted, true);
  assert.ok(elapsed() >= PENDING_COMPILE_GRACE_MS, `${elapsed()}`);
  assert.match(String(describeWait({ until: "idle", timeoutMs: 120_000 }, out).body.agentNextSteps), /did not start/);
});

test("wait: after_generation holds for a late compile, then reports that none ran", async () => {
  const { deps, elapsed } = scripted([{ s: signals(lock("idle"), settledPing()) }]);
  const out = await waitForEditor({ until: "idle", afterGeneration: 7, timeoutMs: 60_000, pollMs: 1_000 }, deps);
  assert.equal(out.reached, true);
  assert.equal(out.compileRan, false);
  assert.ok(elapsed() >= AFTER_GENERATION_GRACE_MS);
  const { body, isError } = describeWait({ until: "idle", afterGeneration: 7, timeoutMs: 60_000 }, out);
  assert.equal(isError, false);
  assert.equal((body.compile as Record<string, unknown>).ranAfterGeneration, false);
});

test("wait: a failed compile is reported with its error count (not an error of the wait)", async () => {
  const { deps } = scripted([{ s: signals(lock("idle", {}, { generation: 8, failed: true, errorCount: 2 }), settledPing()), c: { status: "compile_failed", generation: 8, errorCount: 2, sourceMatches: true, pending: false } }]);
  const out = await waitForEditor({ until: "idle", afterGeneration: 7, timeoutMs: 10_000 }, deps);
  const { body, isError } = describeWait({ until: "idle", afterGeneration: 7, timeoutMs: 10_000 }, out);
  assert.equal(isError, false);
  assert.deepEqual(body.compile, { generation: 8, status: "compile_failed", errorCount: 2, ranAfterGeneration: true });
  assert.match(String((body.agentNextSteps as string[])[0]), /read_compile_errors/);
});

test("wait: playing / edit — a mode that does not come ends wait_not_reached after the mode grace", async () => {
  const { deps, elapsed } = scripted([{ s: signals(lock("idle"), settledPing()) }]);
  const out = await waitForEditor({ until: "playing", timeoutMs: 60_000 }, deps);
  assert.equal(out.reason, "not_reached");
  assert.ok(elapsed() >= MODE_GRACE_MS && elapsed() < MODE_GRACE_MS + 1_000);
  const { body, isError } = describeWait({ until: "playing", timeoutMs: 60_000 }, out);
  assert.equal(isError, true);
  assert.equal((body.error as { code: string }).code, "wait_not_reached");
});

test("wait: entering play mode through its domain reload reaches playing", async () => {
  const frames = [
    { s: signals(lock("idle"), settledPing({ transition: "entering_playmode" })) },
    { s: signals(lock("entering_playmode"), ping(null)) },
    { s: signals(lock("reloading"), ping(null), "reloading") },
    { s: signals(lock("playing"), settledPing({ isPlaying: true })), c: { status: "indeterminate", generation: 7, errorCount: 0, pending: false } },
  ];
  const { deps } = scripted(frames);
  const out = await waitForEditor({ until: "playing", timeoutMs: 30_000 }, deps);
  assert.equal(out.reached, true);
  // A play-mode wait with no compile in sight does not hold on a cold compile reading.
  assert.ok(deps.polls() <= 6, `${deps.polls()}`);
  assert.deepEqual(describeWait({ until: "playing", timeoutMs: 30_000 }, out).body.status, "playing");
});

test("wait: dead bridge ends at once; a missing Editor after its grace; the budget ends in wait_timeout", async () => {
  const dead = scripted([{ s: signals(staleLock("reloading"), ping(null), "dead_bridge", SAFE_MODE) }]);
  const deadOut = await waitForEditor({ until: "idle", timeoutMs: 30_000 }, dead.deps);
  assert.equal(deadOut.reason, "dead_bridge");
  const deadBody = describeWait({ until: "idle", timeoutMs: 30_000 }, deadOut).body;
  assert.equal((deadBody.error as { code: string }).code, "bridge_compile_failed");
  assert.deepEqual(deadBody.evidence, SAFE_MODE);
  // A stopped heartbeat without evidence is waited through like any reload.
  const longReload = scripted([
    { s: signals(staleLock("reloading"), ping(null), "dead_bridge", null) },
    { s: signals(staleLock("reloading"), ping(null), "dead_bridge", null) },
    { s: signals(lock("idle"), settledPing()) },
  ]);
  assert.equal((await waitForEditor({ until: "idle", timeoutMs: 30_000 }, longReload.deps)).reason, "reached");
  const stuck = scripted([{ s: signals(staleLock("reloading"), ping(null), "dead_bridge", null) }]);
  const stuckOut = await waitForEditor({ until: "idle", timeoutMs: 2_000 }, stuck.deps);
  assert.equal(stuckOut.reason, "timeout");
  const stuckBody = describeWait({ until: "idle", timeoutMs: 2_000 }, stuckOut).body;
  assert.equal((stuckBody.error as { code: string }).code, "wait_timeout");
  assert.match((stuckBody.error as { message: string }).message, /not a dead bridge/);
  const gone = scripted([{ s: signals(null, ping(null)) }]);
  assert.equal((await waitForEditor({ until: "idle", timeoutMs: 30_000 }, gone.deps)).reason, "gone");
  const busy = scripted([{ s: signals(lock("compiling"), settledPing({ compiling: true })) }]);
  const out = await waitForEditor({ until: "idle", timeoutMs: 2_000 }, busy.deps);
  assert.equal(out.reason, "timeout");
  const { body, isError } = describeWait({ until: "idle", timeoutMs: 2_000 }, out);
  assert.equal(isError, true);
  assert.equal((body.error as { code: string }).code, "wait_timeout");
  assert.equal(body.holdingFor, "compiling");
});

test("wait: an aborted signal cancels the wait", async () => {
  const controller = new AbortController();
  const { deps } = scripted([{ s: signals(lock("compiling"), settledPing({ compiling: true })) }]);
  controller.abort();
  const out = await waitForEditor({ until: "idle", timeoutMs: 30_000 }, { ...deps, signal: controller.signal });
  assert.equal(out.reason, "aborted");
});

// ---------------------------------------------------------------------------
// hints and refusals
// ---------------------------------------------------------------------------

test("waitCall names the editor_status wait (mirrors the bridge's BridgeJson.WaitCall)", () => {
  assert.equal(waitCall("idle"), 'unity_open_mcp_editor_status {"wait_until":"idle"}');
  assert.equal(waitCall("idle", 12), 'unity_open_mcp_editor_status {"wait_until":"idle","after_generation":12}');
  assert.equal(waitCall("playing", null), 'unity_open_mcp_editor_status {"wait_until":"playing"}');
});

test("reloadingRefusal: a stable editor_reloading with retryAfterMs, the transition and the wait call", () => {
  const view = viewEditor(signals(lock("reloading"), ping(null), "reloading"));
  const result = reloadingRefusal("unity_open_mcp_gameobject_create", view, 30_000);
  const body = JSON.parse((result.content[0] as { text: string }).text);
  assert.equal(result.isError, true);
  assert.equal(body.error.code, "editor_reloading");
  assert.equal(body.dispatched, false);
  assert.equal(body.transition, "reloading");
  assert.equal(typeof body.retryAfterMs, "number");
  assert.match(body.agentNextSteps[0], /"wait_until":"idle"/);
});

test("lostResponseResult: a lost response is retryable, dispatched:null — editor_reloading mid-reload, response_lost otherwise", () => {
  const parse = (r: ReturnType<typeof lostResponseResult>) => JSON.parse((r.content[0] as { text: string }).text);
  // Cut by a reload that runs long (heartbeat stopped, no evidence).
  const reloading = parse(lostResponseResult("unity_open_mcp_find_members",
    viewEditor(signals(staleLock("reloading"), ping(null), "dead_bridge", null)), "connection_closed"));
  assert.equal(reloading.error.code, "editor_reloading");
  assert.equal(reloading.dispatched, null);
  assert.equal(reloading.cause, "connection_closed");
  assert.equal(reloading.heartbeatStalledMs, 45_000);
  assert.equal(typeof reloading.retryAfterMs, "number");
  assert.match(reloading.agentNextSteps[0], /"wait_until":"idle"/);
  // No answer in time from a settled Editor.
  const lost = parse(lostResponseResult("unity_open_mcp_find_members", viewEditor(signals(lock("idle"), settledPing())), "timeout"));
  assert.equal(lost.error.code, "response_lost");
  assert.equal(lost.dispatched, null);
  assert.equal(lost.cause, "timeout");
  assert.notEqual(lost.error.code, "bridge_compile_failed");
  // A blocked main thread is named as such.
  const wedged = parse(lostResponseResult("unity_open_mcp_find_members", viewEditor(signals(staleLock("idle"), settledPing(), "dead_bridge")), "timeout"));
  assert.equal(wedged.error.code, "response_lost");
  assert.match(wedged.error.message, /main thread is blocked/);
});
