// M15 T6.1 — one compile-aware /ping probe for `ping` and `status`.
// `wait-for-ready` waits through editor-state.ts#waitForEditor, the same wait
// as `editor_status {"wait_until":…}`.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { LiveClient } from "../live-client.js";

/** Subset of /ping the poller cares about. Mirrors the bridge contract. */
export interface PingBody {
  connected?: boolean;
  compiling?: boolean;
  isPlaying?: boolean;
  projectPath?: string | null;
  unityVersion?: string | null;
  bridgeVersion?: string;
  mode?: string;
  /** compiling / reloading (wire contract 9) / entering_playmode / exiting_playmode, or null (wire contract 8). */
  transition?: string | null;
}

export const DEFAULT_WAIT_TIMEOUT_MS = 120_000;
export const DEFAULT_POLL_INTERVAL_MS = 1_000;
export const PING_FETCH_TIMEOUT_MS = 5_000;

export interface SinglePollResult {
  /** `ready` only when connected AND not compiling. */
  status: "ready" | "compiling" | "offline" | "error";
  body: PingBody | null;
}

/**
 * Single /ping attempt translated into a poll status. Centralizes the
 * "connected AND not compiling" readiness rule so every command agrees on it.
 *
 * Implementation note: calls `live.isLiveAvailable(fetchTimeoutMs)` ONCE — it
 * performs the `/ping` round-trip, classifies 503/HTTP errors, populates the
 * ping cache, AND returns the connected flag — then reads the body back from
 * {@link LiveClient.getCachedPing} so a single network round-trip answers both
 * "is it up?" and "what's its state?". M14 — `fetchTimeoutMs` is now honored
 * (previously the parameter was declared but never read, so `ping
 * --timeout-ms` was silently ignored and both fetches used the hardcoded 5s).
 */
export async function singlePing(
  live: LiveClient,
  fetchTimeoutMs: number = PING_FETCH_TIMEOUT_MS,
): Promise<SinglePollResult> {
  // `isLiveAvailable` performs the single /ping probe, populates the cache,
  // and returns the connected flag. A 503 (compile in progress) returns true
  // from isLiveAvailable but is NOT readiness — we still need the body to
  // check `compiling`.
  try {
    const available = await live.isLiveAvailable(fetchTimeoutMs);
    if (!available) {
      return { status: "offline", body: null };
    }
    // Reuse the snapshot the probe just wrote to the cache — no second
    // round-trip. When the probe hit a 503 (isLiveAvailable returns true but
    // records no body), there is no cached snapshot: treat as compiling.
    const body = live.getCachedPing();
    if (!body) {
      return { status: "compiling", body: null };
    }
    if (body.compiling === true || (typeof body.transition === "string" && body.transition.length > 0)) {
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

/** Re-exported for command code that needs to render a CallToolResult. */
export type { CallToolResult };
