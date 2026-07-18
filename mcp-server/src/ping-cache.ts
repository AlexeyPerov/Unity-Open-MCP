// M31-optimizations Plan 1 / H1 — PingCache now carries a TTL so callers can
// short-circuit a redundant pre-flight /ping without losing the safety of a
// fresh probe on a stale or `compiling` snapshot. The TTL is env-overridable
// (UNITY_OPEN_MCP_PING_CACHE_TTL_MS) so threshold tuning does not require a
// code change; the default (1.5s) is intentionally short — long enough to
// collapse the double-probe on a burst of interactive tool calls, short
// enough that a real state change (compile starts, bridge goes away) is
// observed on the next call.

/**
 * Default freshness window for {@link PingCache.fresh}. Picked at the low end
 * of the 1–2s band cited in the plan: an interactive agent issues tool calls
 * in bursts well under this, while a real compile/reload flips `compiling`
 * well outside it. Override with
 * `UNITY_OPEN_MCP_PING_CACHE_TTL_MS=<millis>`.
 */
export const DEFAULT_PING_CACHE_TTL_MS = 1_500;

/**
 * Read the PingCache TTL from the env, falling back to the default when the
 * var is unset or unparseable. Negative / NaN / non-numeric values fall back
 * to the default; an explicit `0` forces a real probe on every call (the
 * operator / test escape hatch). Whitespace-only values fall back to the
 * default. Resolved lazily on each call so a test (or an operator re-exporting
 * the env mid-process) is honored without needing to reload the module.
 */
export function readPingCacheTtlMs(): number {
  const raw = process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS;
  if (raw === undefined) return DEFAULT_PING_CACHE_TTL_MS;
  const trimmed = raw.trim();
  if (trimmed === "") return DEFAULT_PING_CACHE_TTL_MS;
  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed) || parsed < 0) return DEFAULT_PING_CACHE_TTL_MS;
  return Math.floor(parsed);
}

export interface PingSnapshot {
  connected: boolean;
  projectPath: string | null;
  unityVersion: string | null;
  bridgeVersion: string;
  mode: string;
  compiling: boolean;
  isPlaying: boolean;
  asOf: string;
}

export class PingCache {
  private snapshot: PingSnapshot | null = null;

  record(body: Omit<PingSnapshot, "asOf">): void {
    this.snapshot = { ...body, asOf: new Date().toISOString() };
  }

  get(): PingSnapshot | null {
    return this.snapshot;
  }

  /**
   * M31 Plan 1 / H1 — return the cached snapshot iff it is fresh enough to
   * short-circuit a pre-flight /ping AND the bridge is in a ready state
   * (`connected && !compiling`). Returns null when:
   *   - no snapshot has been recorded yet,
   *   - `ttlMs` is 0 (operator / test escape hatch that forces a real probe),
   *   - the snapshot is older than `ttlMs` (default: env-resolved TTL),
   *   - the snapshot reports `compiling` (a compile in progress MUST force a
   *     fresh probe so the wait loop observes it settle),
   *   - the snapshot reports `connected: false` (bridge listener up but
   *     session not initialized — needs a real probe to detect recovery).
   *
   * Callers that get a non-null result can skip their pre-flight probe
   * entirely. A null result is NOT an error — it just means "probe as usual."
   * The `ttlMs <= 0` check runs FIRST so a 0 TTL is deterministic (no clock
   * race on fast machines).
   */
  fresh(ttlMs: number = readPingCacheTtlMs()): PingSnapshot | null {
    if (ttlMs <= 0) return null;
    const snap = this.snapshot;
    if (snap === null) return null;
    if (snap.compiling) return null;
    if (!snap.connected) return null;
    const recordedAt = Date.parse(snap.asOf);
    if (!Number.isFinite(recordedAt)) return null;
    if (Date.now() - recordedAt > ttlMs) return null;
    return snap;
  }
}
