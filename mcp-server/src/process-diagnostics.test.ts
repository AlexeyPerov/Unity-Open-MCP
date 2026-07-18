// Tests for process-diagnostics.ts (M31 Plan 3 / T31.4 — proactive prediction
// half). The probe path is OS-specific (lsof / /proc / HandleCount), so every
// test injects a fake `FdProbe` via `setFdProbeForTest`. The pure math
// (headroom + trend) is tested directly with hand-constructed values.

import test from "node:test";
import assert from "node:assert/strict";

import {
  countFileDescriptors,
  computeFdHeadroom,
  analyzeFdTrend,
  setFdProbeForTest,
  FD_CEILING,
  FD_WARN_RATIO,
  FD_CRITICAL_RATIO,
  LAUNCH_CONTEXT_CAVEAT,
  type FdProbe,
  type FdCountResult,
  type FdSample,
} from "./process-diagnostics.js";

// ---------------------------------------------------------------------------
// computeFdHeadroom — pure math against the Mono ceiling.
// ---------------------------------------------------------------------------

test("computeFdHeadroom: zero fds → ok, full headroom", () => {
  const h = computeFdHeadroom(0);
  assert.equal(h.state, "ok");
  assert.equal(h.headroom, FD_CEILING);
  assert.equal(h.pressureRatio, 0);
  assert.equal(h.reliable, true);
});

test("computeFdHeadroom: just under the warn threshold → ok", () => {
  // 80% of 1024 = 819.2 → 819 is below the threshold.
  const h = computeFdHeadroom(FD_CEILING * FD_WARN_RATIO - 1);
  assert.equal(h.state, "ok");
});

test("computeFdHeadroom: exactly at the warn threshold → warn", () => {
  // Smallest count whose pressureRatio rounds to ≥0.8 against a 1024 ceiling.
  // 820/1024 = 0.80078... ≥ FD_WARN_RATIO.
  const h = computeFdHeadroom(Math.ceil(FD_CEILING * FD_WARN_RATIO));
  assert.equal(h.state, "warn");
  assert.ok(h.pressureRatio >= FD_WARN_RATIO);
});

test("computeFdHeadroom: 850 fds (between warn and critical) → warn", () => {
  const h = computeFdHeadroom(850);
  assert.equal(h.state, "warn");
  assert.equal(h.headroom, FD_CEILING - 850);
});

test("computeFdHeadroom: at the critical threshold → critical", () => {
  const h = computeFdHeadroom(Math.ceil(FD_CEILING * FD_CRITICAL_RATIO));
  assert.equal(h.state, "critical");
});

test("computeFdHeadroom: above the ceiling → critical, headroom clamped at 0", () => {
  const h = computeFdHeadroom(FD_CEILING + 50);
  assert.equal(h.state, "critical");
  assert.equal(h.headroom, 0);
  assert.equal(h.pressureRatio, 1, "ratio clamped at 1.0");
});

test("computeFdHeadroom: null count (probe failed) → unknown, unreliable", () => {
  const h = computeFdHeadroom(null);
  assert.equal(h.state, "unknown");
  assert.equal(h.reliable, false);
  assert.equal(h.headroom, FD_CEILING, "unknown reports full nominal headroom");
});

test("computeFdHeadroom: NaN count → unknown (defensive)", () => {
  const h = computeFdHeadroom(Number.NaN);
  assert.equal(h.state, "unknown");
  assert.equal(h.reliable, false);
});

test("computeFdHeadroom: approximate (Windows) counts never warn — only critical", () => {
  // Windows HandleCount is naturally higher than Unix fds; the plan says flag
  // only critical, never warn, so an agent does not over-react.
  const warnLevel = computeFdHeadroom(850, true);
  assert.equal(warnLevel.state, "ok", "approximate count at warn-level → ok");
  assert.equal(warnLevel.reliable, false);

  const criticalLevel = computeFdHeadroom(
    Math.ceil(FD_CEILING * FD_CRITICAL_RATIO),
    true,
  );
  assert.equal(criticalLevel.state, "critical", "approximate at critical still critical");
  assert.equal(criticalLevel.reliable, false);
});

test("FD_CEILING is 1024 (Mono internal ceiling, not OS soft limit)", () => {
  assert.equal(FD_CEILING, 1024);
  assert.equal(FD_WARN_RATIO, 0.8);
  assert.equal(FD_CRITICAL_RATIO, 0.9);
});

// ---------------------------------------------------------------------------
// analyzeFdTrend — leak detection across the session-scoped ring.
// ---------------------------------------------------------------------------

function sample(ts: number, pid: number, count: number | null): FdSample {
  return { ts, pid, count };
}

test("analyzeFdTrend: empty samples → no_history", () => {
  const t = analyzeFdTrend([]);
  assert.equal(t.state, "no_history");
  assert.equal(t.delta, null);
  assert.equal(t.sampleCount, 0);
});

test("analyzeFdTrend: single sample → no_history (need ≥2)", () => {
  const t = analyzeFdTrend([sample(1, 100, 500)]);
  assert.equal(t.state, "no_history");
  assert.equal(t.delta, null);
  assert.equal(t.sampleCount, 1);
});

test("analyzeFdTrend: flat samples → stable, delta 0", () => {
  const t = analyzeFdTrend([
    sample(1, 100, 600),
    sample(2, 100, 600),
    sample(3, 100, 600),
  ]);
  assert.equal(t.state, "stable");
  assert.equal(t.delta, 0);
  assert.equal(t.sampleCount, 3);
});

test("analyzeFdTrend: monotonic climb ≥10% of ceiling → leaking", () => {
  // 600 → 700 → 800: delta 200 ≥ 102 (10% of 1024), strictly non-decreasing.
  const t = analyzeFdTrend([
    sample(1, 100, 600),
    sample(2, 100, 700),
    sample(3, 100, 800),
  ]);
  assert.equal(t.state, "leaking");
  assert.equal(t.delta, 200);
  assert.equal(t.sampleCount, 3);
});

test("analyzeFdTrend: monotonic but small climb → rising (not leaking)", () => {
  // 600 → 605 → 610: delta 10 < 102 (10% threshold). Monotonic but noise.
  const t = analyzeFdTrend([
    sample(1, 100, 600),
    sample(2, 100, 605),
    sample(3, 100, 610),
  ]);
  assert.equal(t.state, "rising");
  assert.equal(t.delta, 10);
});

test("analyzeFdTrend: non-monotonic climb → rising", () => {
  // 600 → 700 → 650: delta 50 (positive) but not monotonic.
  const t = analyzeFdTrend([
    sample(1, 100, 600),
    sample(2, 100, 700),
    sample(3, 100, 650),
  ]);
  assert.equal(t.state, "rising");
  assert.equal(t.delta, 50);
});

test("analyzeFdTrend: decreasing trend → stable (delta negative)", () => {
  const t = analyzeFdTrend([
    sample(1, 100, 800),
    sample(2, 100, 700),
    sample(3, 100, 600),
  ]);
  assert.equal(t.state, "stable");
  assert.equal(t.delta, -200);
});

test("analyzeFdTrend: ignores samples for a DIFFERENT pid (restart case)", () => {
  // After a Unity restart the PID changes — pre-restart samples must not
  // pollute the post-restart trend.
  const t = analyzeFdTrend([
    sample(1, 100, 950),
    sample(2, 100, 980),
    sample(3, 200, 100), // new PID after restart
    sample(4, 200, 110),
  ]);
  assert.equal(t.state, "rising");
  assert.equal(t.delta, 10, "trend uses only the most-recent PID's samples");
  assert.equal(t.sampleCount, 2);
});

test("analyzeFdTrend: skips null-count samples (probe gaps)", () => {
  const t = analyzeFdTrend([
    sample(1, 100, 600),
    sample(2, 100, null), // probe failed — gap, not a zero
    sample(3, 100, 700),
  ]);
  assert.equal(t.sampleCount, 2, "null sample excluded from usable set");
  assert.equal(t.delta, 100);
});

// ---------------------------------------------------------------------------
// countFileDescriptors — dispatch through the injectable probe.
// ---------------------------------------------------------------------------

function makeFakeProbe(results: FdCountResult[] | FdCountResult): FdProbe & {
  calls: number[];
} {
  const calls: number[] = [];
  const queue = Array.isArray(results) ? [...results] : [results];
  return {
    calls,
    count(pid) {
      calls.push(pid);
      const next = queue.shift();
      if (!next) throw new Error("fake probe exhausted");
      return next;
    },
  };
}

test("countFileDescriptors dispatches to the active probe", () => {
  const fake = makeFakeProbe({ count: 432, method: "lsof", approximate: false });
  const restore = setFdProbeForTest(fake);
  try {
    const r = countFileDescriptors(1234);
    assert.deepEqual(fake.calls, [1234]);
    assert.equal(r.count, 432);
    if (r.count !== null) {
      assert.equal(r.method, "lsof");
      assert.equal(r.approximate, false);
    }
  } finally {
    restore();
  }
});

test("countFileDescriptors surfaces probe failures unchanged", () => {
  const fake = makeFakeProbe({
    count: null,
    method: "lsof",
    reason: "not_found",
    message: "no such process",
  });
  const restore = setFdProbeForTest(fake);
  try {
    const r = countFileDescriptors(99);
    assert.equal(r.count, null);
    if (r.count === null) {
      assert.equal(r.reason, "not_found");
      assert.equal(r.message, "no such process");
    }
  } finally {
    restore();
  }
});

test("countFileDescriptors surfaces proc_unreadable from the Linux probe", () => {
  const fake = makeFakeProbe({
    count: null,
    method: "proc",
    reason: "proc_unreadable",
    message: "permission denied",
  });
  const restore = setFdProbeForTest(fake);
  try {
    const r = countFileDescriptors(42);
    if (r.count === null) {
      assert.equal(r.reason, "proc_unreadable");
    } else {
      assert.fail("expected null count");
    }
  } finally {
    restore();
  }
});

test("countFileDescriptors surfaces handle_count_failed from the Windows probe (never lsof_failed)", () => {
  // Windows PowerShell failures must report handle_count_failed, not
  // lsof_failed — lsof does not exist on Windows and the reason code is
  // surfaced to agents as a branching signal.
  const fake = makeFakeProbe({
    count: null,
    method: "handle_count",
    reason: "handle_count_failed",
    message: "PowerShell blew up",
  });
  const restore = setFdProbeForTest(fake);
  try {
    const r = countFileDescriptors(31415);
    if (r.count === null) {
      assert.equal(r.reason, "handle_count_failed");
      assert.notEqual(r.reason, "lsof_failed");
    } else {
      assert.fail("expected null count");
    }
  } finally {
    restore();
  }
});

test("LAUNCH_CONTEXT_CAVEAT mentions Mono and the GUI-launch nuance", () => {
  assert.ok(LAUNCH_CONTEXT_CAVEAT.includes("Mono"));
  assert.ok(
    LAUNCH_CONTEXT_CAVEAT.includes("launchctl") ||
      LAUNCH_CONTEXT_CAVEAT.includes("GUI"),
    "caveat mentions the macOS GUI-launch context",
  );
});

// ---------------------------------------------------------------------------
// Real probe smoke test (Linux only — /proc is the only platform we can
// deterministically probe in CI without spawning a real process tree).
// ---------------------------------------------------------------------------

test("real probe on Linux reads the current process's /proc/self/fd entry count", (t) => {
  if (process.platform !== "linux") {
    t.skip("Linux-only /proc smoke test");
    return;
  }
  const restore = setFdProbeForTest(null);
  try {
    // Use process.pid — the test runner itself — so we know the PID is live.
    const r = countFileDescriptors(process.pid);
    assert.notEqual(r.count, null, "live process has a readable fd count");
    if (r.count !== null) {
      assert.equal(r.method, "proc");
      assert.equal(r.approximate, false);
      assert.ok(r.count > 0, "test runner has at least stdin/stdout/stderr open");
    }
  } finally {
    restore();
  }
});
