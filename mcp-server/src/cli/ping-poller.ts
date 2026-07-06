// M15 T6.1 — compile-aware bridge readiness polling, shared by `ping` and
// `wait-for-ready`.
//
// Polling the bridge is its own concern: it must respect compile state (a 503
// or `compiling: true` /ping is NOT readiness), tolerate transient network
// errors during a domain reload, and fail fast on a dead-bridge signature.
// Pulling it out of the command implementations keeps the command code thin
// and lets the poll loop be unit-tested without touching the network.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { LiveClient } from "../live-client.js";
import {
  readInstanceLock,
  classifyInstance,
  hasRecentPendingTestRun,
} from "../instance-discovery.js";
import { findUnityForProject } from "../running-unity.js";

/** Poll outcome. `ready` true ⇒ exit 0; false ⇒ the caller exits non-zero. */
export interface PollOutcome {
  ready: boolean;
  /** One of: ready | compiling | offline | dead_bridge | timeout. */
  status: "ready" | "compiling" | "offline" | "dead_bridge" | "timeout";
  /** Last /ping body captured (when one ever succeeded). */
  lastPing: PingBody | null;
  /** Human-friendly reason for the outcome; shown in non-JSON mode. */
  reason: string;
  /** Elapsed wall time of the poll, in ms. */
  elapsedMs: number;
}

/** Subset of /ping the poller cares about. Mirrors the bridge contract. */
export interface PingBody {
  connected?: boolean;
  compiling?: boolean;
  isPlaying?: boolean;
  projectPath?: string | null;
  unityVersion?: string | null;
  bridgeVersion?: string;
  mode?: string;
}

export const DEFAULT_WAIT_TIMEOUT_MS = 120_000;
export const DEFAULT_POLL_INTERVAL_MS = 1_000;
export const PING_FETCH_TIMEOUT_MS = 5_000;

export interface PollOptions {
  /** Total budget for the wait, in ms. */
  timeoutMs: number;
  /** Sleep between polls, in ms. */
  intervalMs: number;
  /** Per-fetch timeout forwarded to the LiveClient, in ms. */
  fetchTimeoutMs?: number;
  /** Wall clock used for deadline checks; injectable for tests. */
  now?: () => number;
  /** Sleep implementation; injectable for tests. Default: setTimeout. */
  sleep?: (ms: number) => Promise<void>;
}

function defaultSleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Poll the bridge until it is ready (connected, not compiling) or the deadline
 * passes. A 503 from /ping and `compiling: true` both keep the wait alive —
 * "ready" means usable, not just listening. The poll fails fast on a
 * dead-bridge signature (live PID, stale heartbeat) because /ping will never
 * recover in that state.
 *
 * @param live           LiveClient pointed at the resolved bridge port.
 * @param projectPath    Project path, used to read the instance lock for
 *                       dead-bridge classification. Optional; when absent the
 *                       dead-bridge shortcut is skipped.
 * @param singlePoll     One /ping attempt. Returns `{ ok, status, body }`:
 *                       `ok` true only when connected AND not compiling.
 *                       Indirected so tests can stub the network hop.
 * @param opts           Timing + clock injection.
 */
export async function pollUntilReady(
  live: LiveClient,
  projectPath: string | undefined,
  singlePoll: (live: LiveClient) => Promise<SinglePollResult>,
  opts: PollOptions,
): Promise<PollOutcome> {
  const now = opts.now ?? Date.now;
  const sleep = opts.sleep ?? defaultSleep;
  const start = now();
  const deadline = start + opts.timeoutMs;

  let lastPing: PingBody | null = null;
  let sawCompiling = false;
  let sawOffline = false;

  while (true) {
    const t = now();
    if (t >= deadline) {
      return {
        ready: false,
        status: "timeout",
        lastPing,
        elapsedMs: t - start,
        reason: formatTimeoutReason(opts.timeoutMs, sawCompiling, sawOffline),
      };
    }

    const result = await singlePoll(live);

    if (result.body) lastPing = result.body;
    if (result.status === "compiling") sawCompiling = true;
    if (result.status === "offline" || result.status === "error") {
      sawOffline = true;
    }

    if (result.status === "ready") {
      return {
        ready: true,
        status: "ready",
        lastPing: result.body ?? lastPing,
        elapsedMs: now() - start,
        reason: "Bridge is ready (connected, idle).",
      };
    }

    // Dead-bridge signature: the Unity process is alive but the bridge's
    // [InitializeOnLoad] never re-ran after a compile failure. /ping will not
    // recover until the C# error is fixed, so waiting is pointless.
    if (projectPath) {
      let classification;
      try {
        classification = classifyInstance(readInstanceLock(projectPath));
      } catch {
        classification = null;
      }
      if (classification === "dead_bridge") {
        // specs/feedback.md — a Unity test run (any mode) can freeze the
        // heartbeat writer long enough to flip the classification to
        // dead_bridge even though the run will recover. The bridge writes a
        // test-pending-*.json signal for every run; if a fresh one exists,
        // keep polling (treat as a normal reload) instead of failing fast.
        if (hasRecentPendingTestRun()) {
          await sleep(Math.min(opts.intervalMs, Math.max(0, deadline - now())));
          continue;
        }
        return {
          ready: false,
          status: "dead_bridge",
          lastPing,
          elapsedMs: now() - start,
          reason:
            "Bridge assembly failed to recompile — Unity is in a bad state " +
            "(safe mode / compile errors). Run 'unity-open-mcp run-tool " +
            "unity_open_mcp_read_compile_errors --json' to retrieve the " +
            "compiler errors, fix them, then re-run wait-for-ready.",
        };
      }
      // M27 Plan 1 — cold Safe Mode: no lock (bridge never compiled) + a live
      // Unity process for this project. Same recovery as mid-session
      // dead_bridge — /ping will never come up until the C# error is fixed.
      // Without this branch the poll would spin to `timeout` with no hint that
      // read_compile_errors is the right next step.
      if (classification === "gone" && findUnityForProject(projectPath)) {
        return {
          ready: false,
          status: "dead_bridge",
          lastPing,
          elapsedMs: now() - start,
          reason:
            "Unity is running but the bridge is unreachable and no instance " +
            "lock was found — Unity likely launched straight into Safe Mode " +
            "(the bridge assembly failed to compile from a cold start). Run " +
            "'unity-open-mcp run-tool unity_open_mcp_read_compile_errors " +
            "--json' to retrieve the compiler errors, fix them, then re-run " +
            "wait-for-ready.",
        };
      }
    }

    await sleep(Math.min(opts.intervalMs, Math.max(0, deadline - now())));
  }
}

export interface SinglePollResult {
  /** `ready` only when connected AND not compiling. */
  status: "ready" | "compiling" | "offline" | "error";
  body: PingBody | null;
}

/**
 * Single /ping attempt translated into a poll status. Centralizes the
 * "connected AND not compiling" readiness rule so every command agrees on it.
 *
 * Implementation note: this calls `live.isLiveAvailable()` first to get the
 * ping-cache population side effect + 503/compiling handling, then falls back
 * to a direct route of `unity_open_mcp_ping`. The LiveClient's ping handler
 * already returns the full body; we re-read it from the ping cache so a single
 * network round-trip answers both "is it up?" and "what's its state?".
 */
export async function singlePing(
  live: LiveClient,
  fetchTimeoutMs: number = PING_FETCH_TIMEOUT_MS,
): Promise<SinglePollResult> {
  // `isLiveAvailable` populates the ping cache and returns the connected flag.
  // A 503 (compile in progress) returns true from isLiveAvailable but is NOT
  // readiness — we still need the body to check `compiling`.
  try {
    const available = await live.isLiveAvailable();
    if (!available) {
      return { status: "offline", body: null };
    }
    // Reuse the populated ping cache rather than a second round-trip. The
    // cache was just written by isLiveAvailable.
    const pingResult = await live.route("unity_open_mcp_ping", {});
    const body = extractPingBody(pingResult);
    if (!body) {
      // isLiveAvailable said true but the explicit ping didn't yield a body —
      // treat as compiling/unknown and keep polling.
      return { status: "compiling", body: null };
    }
    if (body.compiling === true) {
      return { status: "compiling", body };
    }
    if (body.connected === false) {
      return { status: "offline", body };
    }
    return { status: "ready", body };
  } catch {
    return { status: "error", body: null };
  }
}

/** Extract the typed ping body from a CallToolResult's first text content. */
export function extractPingBody(result: {
  content: Array<{ type: string; text?: string }>;
}): PingBody | null {
  const first = result.content[0];
  if (!first || first.type !== "text" || typeof first.text !== "string") {
    return null;
  }
  try {
    const parsed = JSON.parse(first.text);
    if (parsed && typeof parsed === "object") {
      return parsed as PingBody;
    }
  } catch {
    // fall through
  }
  return null;
}

function formatTimeoutReason(
  timeoutMs: number,
  sawCompiling: boolean,
  sawOffline: boolean,
): string {
  if (sawCompiling && !sawOffline) {
    return `Bridge was still compiling after ${Math.round(timeoutMs / 1000)}s.`;
  }
  if (sawOffline && !sawCompiling) {
    return `Bridge never became reachable within ${Math.round(timeoutMs / 1000)}s.`;
  }
  return `Bridge did not become ready within ${Math.round(timeoutMs / 1000)}s.`;
}

/** Re-exported for command code that needs to render a CallToolResult. */
export type { CallToolResult };
