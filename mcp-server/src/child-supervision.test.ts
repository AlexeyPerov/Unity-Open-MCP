import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import type { ChildProcess } from "node:child_process";
import {
  trackBatchChild,
  supervisedChildCount,
  killActiveBatchChildren,
  terminateActiveBatchChildren,
  installBatchChildSupervision,
} from "./child-supervision.js";

// Supervision registry tests. Real (tiny) node children — spawn cost is a
// few ms each and the kill paths need genuine OS semantics to be meaningful.

interface ExitOutcome {
  code: number | null;
  signal: string | null;
}

/** Spawn `node -e script` with pipes; the registry only needs a live Child. */
function spawnNode(script: string): ChildProcess {
  return spawn(process.execPath, ["-e", script], {
    stdio: ["ignore", "ignore", "ignore"],
  });
}

/**
 * Spawn a child that traps SIGTERM and announces readiness on stdout. The
 * announcement closes the spawn race — signaling earlier can hit the child
 * before its SIGTERM handler is registered (default disposition = instant
 * death), which would make every escalation test flaky.
 */
function spawnSigtermTrap(): { child: ChildProcess; ready: Promise<void> } {
  const child = spawn(
    process.execPath,
    ["-e", "process.on('SIGTERM', () => {}); console.log('ready'); setInterval(() => {}, 1000);"],
    { stdio: ["ignore", "pipe", "ignore"] },
  );
  const ready = new Promise<void>((resolve, reject) => {
    const fail = setTimeout(() => reject(new Error("sigterm-trap child never became ready")), 10_000);
    child.stdout?.on("data", () => {
      clearTimeout(fail);
      resolve();
    });
    child.on("close", () => {
      clearTimeout(fail);
      reject(new Error("sigterm-trap child exited before becoming ready"));
    });
  });
  return { child, ready };
}

function waitForExit(child: ChildProcess, timeoutMs: number): Promise<ExitOutcome> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(
      () => reject(new Error(`child did not exit within ${timeoutMs}ms`)),
      timeoutMs,
    );
    child.on("close", (code, signal) => {
      clearTimeout(timer);
      resolve({ code: code ?? null, signal: signal ?? null });
    });
  });
}

test("trackBatchChild: retires the child from the registry when it exits", async () => {
  const before = supervisedChildCount();
  const child = spawnNode("setTimeout(() => {}, 50);");
  trackBatchChild(child);
  assert.equal(supervisedChildCount(), before + 1, "tracked while alive");
  await waitForExit(child, 10_000);
  assert.equal(supervisedChildCount(), before, "retired on close");
});

test("killActiveBatchChildren: SIGTERM kills a normally-behaving tracked child", async (t) => {
  if (process.platform === "win32") t.skip("POSIX signal semantics");
  const child = spawnNode("setInterval(() => {}, 1000);");
  trackBatchChild(child);
  const signaled = killActiveBatchChildren("SIGTERM");
  assert.ok(signaled >= 1, "at least our child was signaled");
  const outcome = await waitForExit(child, 10_000);
  assert.equal(outcome.signal, "SIGTERM");
});

test("killActiveBatchChildren: SIGKILL kills a child that traps SIGTERM", async (t) => {
  if (process.platform === "win32") t.skip("POSIX signal semantics");
  // The wedge case: a child that refuses the cooperative shutdown.
  const { child, ready } = spawnSigtermTrap();
  trackBatchChild(child);
  await ready;
  killActiveBatchChildren("SIGTERM");
  // Give the ignored SIGTERM a moment to prove it didn't kill, then escalate.
  await new Promise((r) => setTimeout(r, 300));
  // `child.killed` only means "kill() was called"; liveness is exitCode /
  // signalCode still null (they are set on close).
  assert.ok(child.exitCode === null && child.signalCode === null,
    "child ignored SIGTERM (still alive before escalation)");
  killActiveBatchChildren("SIGKILL");
  const outcome = await waitForExit(child, 10_000);
  assert.equal(outcome.signal, "SIGKILL", "SIGKILL is unblockable");
});

test("terminateActiveBatchChildren: escalates a wedged child to SIGKILL within the grace window", async (t) => {
  if (process.platform === "win32") t.skip("POSIX signal semantics");
  // SIGTERM ladder: grace is 5s (SUPERVISION_KILL_GRACE_MS), so a
  // SIGTERM-trapping child must die by SIGKILL shortly after it.
  const { child, ready } = spawnSigtermTrap();
  trackBatchChild(child);
  await ready;
  const startedAt = Date.now();
  terminateActiveBatchChildren();
  const outcome = await waitForExit(child, 15_000);
  assert.equal(outcome.signal, "SIGKILL");
  const elapsed = Date.now() - startedAt;
  assert.ok(elapsed >= 4_000 && elapsed <= 12_000,
    `escalation fired near the 5s grace (took ${elapsed}ms)`);
});

test("installBatchChildSupervision: installs handlers exactly once", () => {
  const sigintBefore = process.listenerCount("SIGINT");
  const sigtermBefore = process.listenerCount("SIGTERM");
  const exitBefore = process.listenerCount("exit");
  installBatchChildSupervision();
  installBatchChildSupervision();
  assert.equal(process.listenerCount("SIGINT"), sigintBefore + 1, "one SIGINT handler");
  assert.equal(process.listenerCount("SIGTERM"), sigtermBefore + 1, "one SIGTERM handler");
  assert.equal(process.listenerCount("exit"), exitBefore + 1, "one exit handler");
  // NOTE: the handlers themselves are NOT fired here — they process.exit, and
  // this process is the test runner. Their behavior is covered by the kill
  // functions above, which are all the handlers call.
});
