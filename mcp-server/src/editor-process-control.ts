// M31 Plan 3 / T31.3 — Editor fd-exhaustion auto-recovery (kill half).
//
// Companion to `project-health.ts`'s `editor_fd_exhaustion` HealthIssueKind.
// The diagnose-only layer (read_compile_errors surfacing the Bee build-driver
// hang signature) ships already; this module is the *acting* half — terminating
// the hung Unity process so the operator/Hub can relaunch it. The relaunch
// itself is intentionally NOT here: the interactive Editor's launch recipe (the
// flags the Hub or original launcher used) is not knowable from the server, so
// a botched relaunch would leave the operator worse off. The shipped contract is
// "kill only, response tells the operator to relaunch via the Hub."
//
// Cross-platform process control mirrors the running-unity.ts scanner's split:
// `process.kill` (SIGTERM → grace period → SIGKILL) on POSIX, `taskkill /T` on
// Windows via `execFileSync`. The two halves share a single ProcessKiller
// interface and a module-level mutable binding (`currentKiller`) so tests inject
// a fake without spawning/killing real processes — the same seam pattern as
// `UnityProcessScanner` in running-unity.ts.

import { execFileSync } from "node:child_process";

/**
 * Grace period (ms) between SIGTERM and the SIGKILL fallback on POSIX. Generous
 * enough for Unity to flush its log and release the per-project lock on a
 * cooperative shutdown; short enough that a fully-hung Editor does not make the
 * tool feel unresponsive. Tunable per-call via `killEditorProcess({ graceMs })`.
 */
export const DEFAULT_KILL_GRACE_MS = 5_000;

/**
 * Hard upper bound on the total time `killEditorProcess` will wait. Guards
 * against a pathological POSIX kill where neither SIGTERM nor SIGKILL reaps the
 * process within a reasonable window (zombie / uninterruptible sleep). The call
 * returns `terminated: false` with a reason rather than hanging the MCP request.
 */
export const MAX_KILL_WAIT_MS = 15_000;

/** How the process was actually terminated. */
export type KillMethod = "sigterm" | "sigkill" | "taskkill";

/** Successful kill result. */
export interface KillOk {
  terminated: true;
  /** The PID the kill targeted (echoed for correlation with the caller's scan). */
  pid: number;
  /** Which signal/method finally reaped the process. */
  method: KillMethod;
  /** Wall-clock ms from the first signal to confirmed termination. */
  elapsedMs: number;
}

/** Failed kill result. The process is still alive (or its state is unknown). */
export interface KillFailed {
  terminated: false;
  pid: number;
  /** Machine-readable reason code for branching. */
  reason:
    | "invalid_pid"
    | "signal_error"
    | "timeout"
    | "spawn_failed"
    | "not_found";
  /** Human-readable detail. */
  message: string;
}

export type KillResult = KillOk | KillFailed;

/** Cross-platform process killer. Tests inject a fake via the setter below. */
export interface ProcessKiller {
  /**
   * Terminate the given PID. Resolves once the process is confirmed gone or
   * the grace window expires. Never throws — failures surface as
   * `KillFailed`. `nowMs` is injected for deterministic elapsed-time tests.
   */
  kill(pid: number, graceMs: number, nowMs: () => number): Promise<KillResult>;
}

/** Sleep helper (kept as a parameter so tests can drive the clock). */
type Sleeper = (ms: number) => Promise<void>;
const realSleep: Sleeper = (ms) =>
  new Promise((resolve) => setTimeout(resolve, ms));

/** Default sleeper — production code path. */
const defaultSleeper: Sleeper = realSleep;

// ---------------------------------------------------------------------------
// POSIX kill: SIGTERM → grace period (polling liveness) → SIGKILL fallback.
// ---------------------------------------------------------------------------

/** `process.kill(pid, 0)` liveness check wrapped to never throw. */
function isAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code;
    if (code === "EPERM") return true; // exists but we can't probe
    return false; // ESRCH or anything else → treat as dead
  }
}

async function killPosix(
  pid: number,
  graceMs: number,
  sleeper: Sleeper,
  nowMs: () => number,
): Promise<KillResult> {
  const start = nowMs();
  try {
    try {
      process.kill(pid, "SIGTERM");
    } catch (err) {
      const code = (err as NodeJS.ErrnoException).code;
      if (code === "ESRCH") {
        return {
          terminated: false,
          pid,
          reason: "not_found",
          message: `No such process for PID ${pid} (ESRCH on SIGTERM).`,
        };
      }
      return {
        terminated: false,
        pid,
        reason: "signal_error",
        message: `SIGTERM failed for PID ${pid}: ${
          err instanceof Error ? err.message : String(err)
        }`,
      };
    }

    // Poll for graceful exit across the grace window. 100ms cadence keeps the
    // tool responsive without burning CPU; we exit early the moment the PID is
    // reaped.
    const pollInterval = 100;
    let waited = 0;
    while (waited < graceMs) {
      await sleeper(Math.min(pollInterval, graceMs - waited));
      waited = nowMs() - start;
      if (!isAlive(pid)) {
        return {
          terminated: true,
          pid,
          method: "sigterm",
          elapsedMs: nowMs() - start,
        };
      }
      if (nowMs() - start >= MAX_KILL_WAIT_MS) {
        return {
          terminated: false,
          pid,
          reason: "timeout",
          message:
            `SIGTERM sent to PID ${pid} but it did not exit within the ` +
            `${MAX_KILL_WAIT_MS}ms hard cap.`,
        };
      }
    }

    // SIGKILL fallback. The process is still alive after the grace window.
    try {
      process.kill(pid, "SIGKILL");
    } catch (err) {
      const code = (err as NodeJS.ErrnoException).code;
      if (code === "ESRCH") {
        // Raced — exited between the last poll and the SIGKILL.
        return {
          terminated: true,
          pid,
          method: "sigterm",
          elapsedMs: nowMs() - start,
        };
      }
      return {
        terminated: false,
        pid,
        reason: "signal_error",
        message: `SIGKILL failed for PID ${pid}: ${
          err instanceof Error ? err.message : String(err)
        }`,
      };
    }

    // Give SIGKILL a brief window to reap. SIGKILL is async at the kernel
    // level — the process gets reaped on the next scheduler tick.
    await sleeper(Math.min(500, MAX_KILL_WAIT_MS - (nowMs() - start)));
    if (!isAlive(pid)) {
      return {
        terminated: true,
        pid,
        method: "sigkill",
        elapsedMs: nowMs() - start,
      };
    }

    return {
      terminated: false,
      pid,
      reason: "timeout",
      message:
        `Both SIGTERM and SIGKILL sent to PID ${pid} but it did not exit ` +
        `within the grace + fallback window.`,
    };
  } catch (err) {
    return {
      terminated: false,
      pid,
      reason: "signal_error",
      message: `Unexpected error killing PID ${pid}: ${
        err instanceof Error ? err.message : String(err)
      }`,
    };
  }
}

// ---------------------------------------------------------------------------
// Windows kill: `taskkill /PID <pid> /T /F`. /T kills the whole process tree
// (Unity spawns Bee build workers + child IPC processes — fd-exhaustion hangs
// often leave those children orphaned); /F forces termination (the hung main
// thread cannot honor a cooperative WM_CLOSE).
// ---------------------------------------------------------------------------

async function killWindows(
  pid: number,
  nowMs: () => number,
): Promise<KillResult> {
  const start = nowMs();
  try {
    // taskkill exits 0 on success, non-zero when the PID was not found or the
    // kill failed. /F is forced — there is no graceful/forced escalation on
    // Windows like there is on POSIX (WM_CLOSE would need a GUI message pump,
    // which a hung main thread cannot service).
    execFileSync("taskkill", ["/PID", String(pid), "/T", "/F"], {
      stdio: "ignore",
      windowsHide: true,
    });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    if (/not found|no such|exit code: 128/i.test(message)) {
      return {
        terminated: false,
        pid,
        reason: "not_found",
        message: `taskkill reported no process for PID ${pid}.`,
      };
    }
    return {
      terminated: false,
      pid,
      reason: "spawn_failed",
      message: `taskkill failed for PID ${pid}: ${message}`,
    };
  }
  return {
    terminated: true,
    pid,
    method: "taskkill",
    elapsedMs: nowMs() - start,
  };
}

// ---------------------------------------------------------------------------
// Real killer — dispatches by platform.
// ---------------------------------------------------------------------------

const realKiller: ProcessKiller = {
  async kill(pid, graceMs, nowMs): Promise<KillResult> {
    if (!Number.isInteger(pid) || pid <= 0) {
      return {
        terminated: false,
        pid,
        reason: "invalid_pid",
        message: `Invalid PID ${pid}.`,
      };
    }
    if (process.platform === "win32") {
      // taskkill /F is forced — there is no grace window on Windows.
      return killWindows(pid, nowMs);
    }
    return killPosix(pid, graceMs, defaultSleeper, nowMs);
  },
};

// Mutable binding so tests can swap in a fake killer without threading a
// dependency through every caller. Default is the real OS killer.
let currentKiller: ProcessKiller = realKiller;

/** Read the active killer. Production callers use this via `killEditorProcess`. */
export function getProcessKiller(): ProcessKiller {
  return currentKiller;
}

/**
 * Install a fake killer for tests. Returns a restore function that re-binds the
 * previous killer (so concurrent test files don't leak state). Production code
 * MUST NOT call this.
 */
export function setProcessKillerForTest(
  fake: ProcessKiller | null,
): () => void {
  const prev = currentKiller;
  currentKiller = fake ?? realKiller;
  return () => {
    currentKiller = prev;
  };
}

/**
 * Terminate a Unity Editor process by PID. The contract:
 *
 *   - SIGTERM first (POSIX) so Unity can flush its log + release the
 *     per-project lock, then SIGKILL after `graceMs` if still alive.
 *   - `taskkill /T /F` on Windows (the hung main thread cannot honor a
 *     cooperative close; the /T also reaps orphaned Bee build workers).
 *   - Never throws. Failures (not found, signal error, timeout) surface as
 *     `KillFailed` with a machine-readable `reason` for branching.
 *
 * @param pid     Target Unity PID (from `findUnityForProject`).
 * @param graceMs SIGTERM→SIGKILL grace window on POSIX. Defaults to
 *                {@link DEFAULT_KILL_GRACE_MS}.
 */
export async function killEditorProcess(
  pid: number,
  graceMs: number = DEFAULT_KILL_GRACE_MS,
): Promise<KillResult> {
  return currentKiller.kill(
    pid,
    Math.max(0, Math.min(graceMs, MAX_KILL_WAIT_MS)),
    () => Date.now(),
  );
}
