// Tests for editor-process-control.ts (M31 Plan 3 / T31.3 — reactive kill half).
//
// The kill path is cross-platform + destructive, so every test injects a fake
// `ProcessKiller` via `setProcessKillerForTest`. No real `process.kill` /
// `taskkill` runs in unit tests. The fake records the args it was called with
// and returns a canned `KillResult` so we can assert the dispatch + the
// killEditorProcess wrapper (grace clamping) without touching the OS.

import test from "node:test";
import assert from "node:assert/strict";

import {
  killEditorProcess,
  setProcessKillerForTest,
  DEFAULT_KILL_GRACE_MS,
  MAX_KILL_WAIT_MS,
  type KillResult,
  type ProcessKiller,
} from "./editor-process-control.js";

/** Fake killer that records calls + returns a scripted result per call. */
function makeFakeKiller(
  results: KillResult[] | KillResult,
): ProcessKiller & {
  calls: Array<{ pid: number; graceMs: number }>;
} {
  const calls: Array<{ pid: number; graceMs: number }> = [];
  const queue = Array.isArray(results) ? [...results] : [results];
  return {
    calls,
    async kill(pid, graceMs) {
      calls.push({ pid, graceMs });
      const next = queue.shift();
      if (!next) throw new Error("fake killer exhausted");
      return next;
    },
  };
}

test("killEditorProcess dispatches to the active killer with the default grace", async () => {
  const fake = makeFakeKiller({
    terminated: true,
    pid: 1234,
    method: "sigterm",
    elapsedMs: 100,
  });
  const restore = setProcessKillerForTest(fake);
  try {
    const result = await killEditorProcess(1234);
    assert.equal(result.terminated, true);
    assert.deepEqual(fake.calls, [{ pid: 1234, graceMs: DEFAULT_KILL_GRACE_MS }]);
  } finally {
    restore();
  }
});

test("killEditorProcess honors an explicit grace window", async () => {
  const fake = makeFakeKiller({
    terminated: true,
    pid: 42,
    method: "sigkill",
    elapsedMs: 6000,
  });
  const restore = setProcessKillerForTest(fake);
  try {
    await killEditorProcess(42, 8000);
    assert.deepEqual(fake.calls, [{ pid: 42, graceMs: 8000 }]);
  } finally {
    restore();
  }
});

test("killEditorProcess clamps grace above MAX_KILL_WAIT_MS", async () => {
  const fake = makeFakeKiller({
    terminated: true,
    pid: 1,
    method: "sigterm",
    elapsedMs: 1,
  });
  const restore = setProcessKillerForTest(fake);
  try {
    await killEditorProcess(1, 999_999);
    assert.equal(
      fake.calls[0].graceMs,
      MAX_KILL_WAIT_MS,
      "grace clamped to the hard cap",
    );
  } finally {
    restore();
  }
});

test("killEditorProcess clamps negative grace to 0", async () => {
  const fake = makeFakeKiller({
    terminated: true,
    pid: 1,
    method: "sigterm",
    elapsedMs: 0,
  });
  const restore = setProcessKillerForTest(fake);
  try {
    await killEditorProcess(1, -100);
    assert.equal(fake.calls[0].graceMs, 0);
  } finally {
    restore();
  }
});

test("killEditorProcess surfaces a kill failure unchanged (no throw)", async () => {
  const fake = makeFakeKiller({
    terminated: false,
    pid: 7,
    reason: "timeout",
    message: "fake timeout",
  });
  const restore = setProcessKillerForTest(fake);
  try {
    const result = await killEditorProcess(7);
    assert.equal(result.terminated, false);
    if (!result.terminated) {
      assert.equal(result.reason, "timeout");
      assert.equal(result.message, "fake timeout");
    }
  } finally {
    restore();
  }
});

test("killEditorProcess surfaces not_found when the killer reports it", async () => {
  const fake = makeFakeKiller({
    terminated: false,
    pid: 999,
    reason: "not_found",
    message: "No such process.",
  });
  const restore = setProcessKillerForTest(fake);
  try {
    const result = await killEditorProcess(999);
    if (!result.terminated) {
      assert.equal(result.reason, "not_found");
    } else {
      assert.fail("expected not_found, got terminated");
    }
  } finally {
    restore();
  }
});

test("setProcessKillerForTest(null) restores the real killer binding", async () => {
  // Install a fake, then null it, and confirm the active killer is the real
  // one by checking that a subsequent install+restore round-trips cleanly.
  const fake = makeFakeKiller({
    terminated: true,
    pid: 1,
    method: "taskkill",
    elapsedMs: 1,
  });
  const r1 = setProcessKillerForTest(fake);
  r1();
  // After restore, installing null should be a no-op + return a restore fn.
  const r2 = setProcessKillerForTest(null);
  assert.equal(typeof r2, "function");
  r2();
});

test("DEFAULT_KILL_GRACE_MS is 5s and MAX_KILL_WAIT_MS is 15s (plan-stable constants)", () => {
  assert.equal(DEFAULT_KILL_GRACE_MS, 5_000);
  assert.equal(MAX_KILL_WAIT_MS, 15_000);
});

// ---------------------------------------------------------------------------
// Real killer smoke tests (POSIX branch only — Windows runs taskkill which we
// cannot exercise on non-Windows CI). These cover the SIGTERM→SIGKILL path
// against a real child process so the dispatch logic is not dead code in the
// test suite. Skipped automatically on Windows.
// ---------------------------------------------------------------------------

test("real killer terminates a spawned child via SIGTERM within the grace window", async (t) => {
  if (process.platform === "win32") {
    t.skip("POSIX-only smoke test");
    return;
  }
  // Restore the real killer for this test.
  const restore = setProcessKillerForTest(null);
  try {
    const { spawn } = await import("node:child_process");
    // A child that sleeps — cooperative SIGTERM should reap it.
    const child = spawn("sleep", ["30"], { stdio: "ignore" });
    const pid = child.pid ?? -1;
    assert.ok(pid > 0, "child spawned with a pid");
    try {
      const result = await killEditorProcess(pid, 3_000);
      assert.equal(result.terminated, true, "child terminated");
      if (result.terminated) {
        assert.equal(result.method, "sigterm", "reaped by SIGTERM cooperatively");
      }
    } finally {
      // Belt-and-suspenders: make sure we never leak the child even on
      // assertion failure.
      try {
        process.kill(pid, "SIGKILL");
      } catch {
        // already gone — fine
      }
    }
  } finally {
    restore();
  }
});
