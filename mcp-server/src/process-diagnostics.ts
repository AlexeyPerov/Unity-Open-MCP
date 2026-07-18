// M31 Plan 3 / T31.4 — Editor fd-exhaustion prediction (proactive fd-usage
// monitoring).
//
// Companion to `editor-process-control.ts` (the reactive kill half). Where the
// kill tool acts AFTER the Editor is hung, this module samples the live Unity
// process's file-descriptor count BEFORE exhaustion so an agent can warn the
// operator to save and restart while the bridge is still healthy. The bridge
// is the thing that dies on fd-exhaustion, so the probe MUST NOT depend on it
// — it runs server-side against the OS, exactly like the process scan in
// `running-unity.ts`.
//
// Threshold rationale (the plan's "Mono ceiling, not OS soft limit" point):
//
//   Mono's IOSelector (kqueue/kevent on macOS/BSD, epoll on Linux) refuses to
//   register a descriptor whose number crosses an internal ~1024 ceiling —
//   that is the real trip point for the Bee build-driver hang. The OS
//   `ulimit -n` soft limit is only a loose upper bound and is misleading on
//   macOS: a GUI-launched Unity inherits `launchctl limit maxfiles`, not the
//   MCP server's shell `ulimit`. So we measure headroom against the fixed
//   Mono ceiling (FD_CEILING), not the variable OS limit.
//
// Cross-platform probe:
//   - macOS: `lsof -p <pid>` (no /proc on macOS). Bounded execFileSync
//     timeout; line count is the fd total. Heavy — callers cache briefly.
//   - Linux: `readdirSync(/proc/<pid>/fd).length` (no shell-out).
//   - Windows: `Get-Process -Id <pid>.HandleCount` (approximate — Windows
//     handle count is broader than Unix fds; the threshold differs and the
//     result is flagged `approximate: true`).

import { execFileSync } from "node:child_process";
import { readdirSync } from "node:fs";

/**
 * Mono's internal file-descriptor ceiling — the real trip point for the Bee
 * build-driver fd-exhaustion hang. Mono's IOSelector refuses to register a
 * descriptor whose number crosses this (kqueue/kevent on macOS/BSD, epoll on
 * Linux). The OS `ulimit -n` soft limit is only a loose upper bound and is
 * misleading for a GUI-launched Unity on macOS (it inherits `launchctl limit
 * maxfiles`, not the shell's ulimit). Documented in the tool response via
 * `launchContextCaveat`.
 */
export const FD_CEILING = 1024;

/**
 * Headroom fraction (of FD_CEILING) at or below which the state is `warn`:
 * the Editor is approaching the Mono ceiling and an agent should surface the
 * trend so the operator can save + restart before the hang. ≥80% usage.
 */
export const FD_WARN_RATIO = 0.8;

/**
 * Headroom fraction at or below which the state is `critical`: the next
 * domain reload is likely to trip the Bee build-driver hang. ≥90% usage.
 */
export const FD_CRITICAL_RATIO = 0.9;

/** execFileSync timeout for the macOS `lsof` probe (ms). */
export const FD_PROBE_TIMEOUT_MS = 3_000;

/** OS method used to count file descriptors for a PID. */
export type FdCountMethod = "lsof" | "proc" | "handle_count";

/** Successful fd-count probe. */
export interface FdCountOk {
  /** The raw fd count (or Windows handle count — see `approximate`). */
  count: number;
  method: FdCountMethod;
  /**
   * `true` when the count is a Windows HandleCount approximation rather than
   * a Unix fd count. Windows handles cover more than just fds (kernel +
   * GDI + user objects), so the headroom math is looser there. The response
   * flags this so an agent does not over-react to a naturally-higher number.
   */
  approximate: boolean;
}

/** Failed fd-count probe. The count is unknown — callers must NOT treat
 *  this as "low pressure" (it is "unknown pressure"). */
export interface FdCountFailed {
  count: null;
  method: FdCountMethod;
  /**
   * Machine-readable reason for branching. Each reason is paired with the
   * matching {@link FdCountMethod} — `lsof_failed` only comes from the macOS
   * probe, `proc_unreadable` only from the Linux probe, and
   * `handle_count_failed` only from the Windows probe.
   */
  reason:
    | "not_found"
    | "proc_unreadable"
    | "lsof_failed"
    | "handle_count_failed"
    | "unsupported_platform";
  message: string;
}

export type FdCountResult = FdCountOk | FdCountFailed;

/** Coarse pressure state derived from `computeFdHeadroom`. */
export type FdPressureState = "ok" | "warn" | "critical" | "unknown";

/** Headroom metric against the Mono fd ceiling. */
export interface FdHeadroom {
  /** Open fds / FD_CEILING, clamped to [0, 1] when count is known. */
  pressureRatio: number;
  /** FD_CEILING - count, clamped at 0. */
  headroom: number;
  /** The fixed ceiling (Mono ~1024). */
  ceiling: number;
  state: FdPressureState;
  /** `false` when the underlying count was an approximation (Windows). */
  reliable: boolean;
}

/**
 * Injectable fd-count probe. Tests swap in a fake via the setter below so no
 * `lsof` / `/proc` / PowerShell I/O runs in unit tests — same seam pattern as
 * `UnityProcessScanner` / `ProcessKiller`.
 */
export interface FdProbe {
  count(pid: number): FdCountResult;
}

// ---------------------------------------------------------------------------
// Platform-specific probe implementations.
// ---------------------------------------------------------------------------

/** macOS `lsof -p <pid>` scanner. Returns the line count (one line per fd). */
function probeMacos(pid: number): FdCountResult {
  let stdout: string;
  try {
    stdout = execFileSync("lsof", ["-p", String(pid)], {
      encoding: "utf8",
      timeout: FD_PROBE_TIMEOUT_MS,
      stdio: ["ignore", "pipe", "ignore"],
    });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    // lsof exits non-zero when the PID is gone. Treat that as not_found.
    if (/no such file|exit code: 1/i.test(message)) {
      return {
        count: null,
        method: "lsof",
        reason: "not_found",
        message: `lsof reported no process for PID ${pid}.`,
      };
    }
    return {
      count: null,
      method: "lsof",
      reason: "lsof_failed",
      message: `lsof failed for PID ${pid}: ${message}`,
    };
  }
  // `lsof -p <pid>` prints a header line ("COMMAND PID USER FD TYPE ...") then
  // one line per open fd. Subtract the header. An empty/blank stdout means the
  // PID vanished between the spawn and the read — treat as not_found.
  const lines = stdout.split(/\r?\n/).filter((l) => l.length > 0);
  if (lines.length === 0) {
    return {
      count: null,
      method: "lsof",
      reason: "not_found",
      message: `lsof produced no output for PID ${pid}.`,
    };
  }
  // Header is the first line; its FD column says "FD" literally. The rest are
  // per-fd rows. Subtract 1 for the header.
  return {
    count: Math.max(0, lines.length - 1),
    method: "lsof",
    approximate: false,
  };
}

/** Linux `/proc/<pid>/fd` directory entry count (no shell-out). */
function probeLinux(pid: number): FdCountResult {
  let entries: string[];
  try {
    entries = readdirSync(`/proc/${pid}/fd`);
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code;
    if (code === "ENOENT") {
      return {
        count: null,
        method: "proc",
        reason: "not_found",
        message: `No /proc/${pid}/fd (process gone).`,
      };
    }
    return {
      count: null,
      method: "proc",
      reason: "proc_unreadable",
      message: `Could not read /proc/${pid}/fd: ${
        err instanceof Error ? err.message : String(err)
      }`,
    };
  }
  // `/proc/<pid>/fd` contains one entry per open fd (symbolic links). readdir
  // returns the names — the count is the length.
  return {
    count: entries.length,
    method: "proc",
    approximate: false,
  };
}

/** Windows `Get-Process -Id <pid>.HandleCount` (approximate). */
function probeWindows(pid: number): FdCountResult {
  let stdout: string;
  try {
    stdout = execFileSync(
      "powershell",
      [
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        `(Get-Process -Id ${pid} -ErrorAction SilentlyContinue).HandleCount`,
      ],
      {
        encoding: "utf8",
        windowsHide: true,
        timeout: FD_PROBE_TIMEOUT_MS,
      },
    );
  } catch (err) {
    return {
      count: null,
      method: "handle_count",
      reason: "handle_count_failed",
      message: `PowerShell handle-count probe failed for PID ${pid}: ${
        err instanceof Error ? err.message : String(err)
      }`,
    };
  }
  const trimmed = stdout.trim();
  if (trimmed.length === 0) {
    return {
      count: null,
      method: "handle_count",
      reason: "not_found",
      message: `Get-Process reported no process for PID ${pid}.`,
    };
  }
  const parsed = Number.parseInt(trimmed, 10);
  if (!Number.isFinite(parsed) || parsed < 0) {
    return {
      count: null,
      method: "handle_count",
      reason: "handle_count_failed",
      message: `Unparseable HandleCount '${trimmed}' for PID ${pid}.`,
    };
  }
  return {
    count: parsed,
    method: "handle_count",
    // Windows HandleCount includes kernel + GDI + user handles — broader
    // than Unix fds. The headroom math still applies as a loose signal.
    approximate: true,
  };
}

const realProbe: FdProbe = {
  count(pid: number): FdCountResult {
    if (!Number.isInteger(pid) || pid <= 0) {
      return {
        count: null,
        method: "lsof",
        reason: "not_found",
        message: `Invalid PID ${pid}.`,
      };
    }
    if (process.platform === "darwin") return probeMacos(pid);
    if (process.platform === "linux") return probeLinux(pid);
    if (process.platform === "win32") return probeWindows(pid);
    return {
      count: null,
      method: "lsof",
      reason: "unsupported_platform",
      message: `fd-count probe is not implemented for platform '${process.platform}'.`,
    };
  },
};

// Mutable binding so tests can swap in a fake probe.
let currentProbe: FdProbe = realProbe;

/** Read the active probe. Production callers use this via `countFileDescriptors`. */
export function getFdProbe(): FdProbe {
  return currentProbe;
}

/**
 * Install a fake probe for tests. Returns a restore function that re-binds the
 * previous probe. Production code MUST NOT call this.
 */
export function setFdProbeForTest(fake: FdProbe | null): () => void {
  const prev = currentProbe;
  currentProbe = fake ?? realProbe;
  return () => {
    currentProbe = prev;
  };
}

/**
 * Count the open file descriptors for a live Unity PID. Never throws —
 * failures (process gone, lsof/proc unreadable, unsupported platform) surface
 * as `FdCountFailed`. The result's `method` records how the count was obtained
 * so an agent can interpret the headroom math correctly (Windows
 * `handle_count` is approximate).
 *
 * @param pid the live Unity PID (from `findUnityForProject`).
 */
export function countFileDescriptors(pid: number): FdCountResult {
  return currentProbe.count(pid);
}

/**
 * Pure headroom math against the fixed Mono fd ceiling. The OS soft limit
 * (`ulimit -n`) is intentionally NOT used — it is a loose upper bound and is
 * misleading for GUI-launched Unity on macOS (inherits `launchctl limit
 * maxfiles`, not the shell ulimit).
 *
 * `null` count (probe failed) → `state: "unknown"` with `reliable: false`. An
 * agent must NOT treat "unknown" as "ok"; it should surface the trend (if any
 * prior samples exist) and tell the operator the live count could not be read.
 */
export function computeFdHeadroom(count: number | null, approximate = false): FdHeadroom {
  if (count === null || !Number.isFinite(count)) {
    return {
      pressureRatio: 0,
      headroom: FD_CEILING,
      ceiling: FD_CEILING,
      state: "unknown",
      reliable: false,
    };
  }
  const clamped = Math.max(0, count);
  const pressureRatio = Math.min(1, clamped / FD_CEILING);
  const headroom = Math.max(0, FD_CEILING - clamped);
  let state: FdPressureState;
  if (approximate) {
    // Windows HandleCount is naturally higher than Unix fds — only flag
    // critical, never warn, so an agent does not over-react.
    state = pressureRatio >= FD_CRITICAL_RATIO ? "critical" : "ok";
  } else if (pressureRatio >= FD_CRITICAL_RATIO) {
    state = "critical";
  } else if (pressureRatio >= FD_WARN_RATIO) {
    state = "warn";
  } else {
    state = "ok";
  }
  return {
    pressureRatio,
    headroom,
    ceiling: FD_CEILING,
    state,
    reliable: !approximate,
  };
}

/** One fd sample recorded in the session-scoped ring. */
export interface FdSample {
  /** Wall-clock timestamp (ms since epoch). */
  ts: number;
  /** The Unity PID the sample was taken against. */
  pid: number;
  /** Open fd count at sample time (`null` when the probe failed). */
  count: number | null;
}

/**
 * Trend classification across successive fd samples. A monotonic climb across
 * domain reloads is the leak signature — the agent should warn the operator to
 * save + restart BEFORE the next reload trips the ceiling. Absolute count is
 * NOT enough: a stable 600 fds is healthy; a climb 600 → 800 → 950 across
 * three reloads is a leak in progress.
 */
export type FdTrendState =
  | "no_history"
  | "stable"
  | "rising"
  | "leaking"
  | "unknown";

export interface FdTrend {
  state: FdTrendState;
  /**
   * First-to-last delta in fd count (negative for a leak — the count grew).
   * `null` when there are fewer than two samples with known counts for the
   * same PID.
   */
  delta: number | null;
  /** Number of samples used for the trend (same-PID, known-count). */
  sampleCount: number;
}

/**
 * Pure trend detector over a sample list. Only considers samples with a known
 * count for the SAME pid (a Unity restart changes the PID; the trend must not
 * mix pre- and post-restart samples). A monotonic climb of ≥10% of the
 * ceiling across ≥3 samples is `leaking`; a non-monotonic climb is `rising`;
 * otherwise `stable`. Fewer than two usable samples → `no_history`.
 */
export function analyzeFdTrend(samples: readonly FdSample[]): FdTrend {
  if (samples.length === 0) {
    return { state: "no_history", delta: null, sampleCount: 0 };
  }
  // Take the tail for the most-recent PID (a restart changes the PID).
  const lastPid = samples[samples.length - 1].pid;
  const usable = samples.filter(
    (s) => s.pid === lastPid && s.count !== null && Number.isFinite(s.count),
  ) as Array<{ ts: number; pid: number; count: number }>;
  if (usable.length < 2) {
    return { state: "no_history", delta: null, sampleCount: usable.length };
  }
  const first = usable[0].count;
  const last = usable[usable.length - 1].count;
  const delta = last - first;

  // `rising` = strictly non-decreasing across all usable samples (each step
  // ≥ the previous). `leaking` = rising AND the total climb is ≥10% of the
  // Mono ceiling (a meaningful leak, not measurement noise).
  let monotonic = true;
  for (let i = 1; i < usable.length; i++) {
    if (usable[i].count < usable[i - 1].count) {
      monotonic = false;
      break;
    }
  }
  if (monotonic && delta >= FD_CEILING * 0.1) {
    return { state: "leaking", delta, sampleCount: usable.length };
  }
  if (delta > 0) {
    return { state: "rising", delta, sampleCount: usable.length };
  }
  return { state: "stable", delta, sampleCount: usable.length };
}

/**
 * Caveat string surfaced in the resource_pressure response so an agent (and
 * the operator) understand the launch-context nuance: the Mono ceiling, not
 * the OS soft limit, is the trip point; a GUI-launched Unity inherits
 * `launchctl limit maxfiles` (macOS) rather than the shell's ulimit.
 */
export const LAUNCH_CONTEXT_CAVEAT =
  "Headroom is measured against Mono's internal ~1024 file-descriptor ceiling " +
  "(the real trip point for the Bee build-driver hang), NOT the OS soft limit " +
  "(ulimit -n / launchctl limit maxfiles). A GUI-launched Unity inherits the " +
  "desktop environment's file limit, not the MCP server's shell limit, so the " +
  "OS soft limit is only a loose upper bound.";
