// M31-optimizations Plan 1 / M3 — short-TTL in-memory cache for the bridge
// tool inventory (`GET /tools`). The compiled-tool set changes only on a
// bridge recompile (seconds-to-minutes timescale), not per call, but the
// meta-tools (capabilities / manage_tools(list_groups|suggest|activate_for))
// re-fetched it on every invocation — two HTTP round-trips per meta-tool call.
//
// The cache is session-scoped (per-LiveClient instance, in-memory only) and
// invalidated on the same lifecycle signals PingCache already listens to: a
// detected dead-bridge / reload (handleTransientOffline) and a compile settle
// (waitForCompile exit). Consistent with root AGENTS.md's "no persistent disk
// caches" rule — this is the same category as PingCache, which is already
// permitted.

/**
 * Default freshness window for the bridge-tools cache. Picked in the middle
 * of the 2–5s band cited in the plan: long enough to collapse the
 * double-fetch across the capabilities / list_groups / activate_for triad in
 * a single agent turn, short enough that a real bridge recompile (which
// flips `compiling` and invalidates via {@link BridgeToolsCache.invalidate}
// anyway) is observed promptly even without the explicit invalidation.
 * Override with `UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS=<millis>`.
 */
export const DEFAULT_BRIDGE_TOOLS_TTL_MS = 3_000;

/**
 * Read the bridge-tools cache TTL from the env, falling back to the default
 * when unset or unparseable. Negative / NaN / non-numeric fall back to the
 * default; an explicit `0` disables caching (every call hits the network),
 * which is useful for tests and for an operator debugging a stale-inventory
 * issue. Whitespace-only values fall back to the default (they are not a
 * deliberate 0).
 */
export function readBridgeToolsTtlMs(): number {
  const raw = process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS;
  if (raw === undefined) return DEFAULT_BRIDGE_TOOLS_TTL_MS;
  const trimmed = raw.trim();
  if (trimmed === "") return DEFAULT_BRIDGE_TOOLS_TTL_MS;
  // Require a strictly numeric value so "abc" / "yes" / "1e3" style payloads
  // do not get coerced to NaN-then-0 by Number() and silently disable the
  // cache. Number(trimmed) handles "-5", "0", "3000", etc.
  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed) || parsed < 0) return DEFAULT_BRIDGE_TOOLS_TTL_MS;
  return Math.floor(parsed);
}

/** Inventory payload returned by the bridge's `GET /tools`. */
export interface BridgeToolsInventory {
  tools: Set<string>;
  groups: Array<{ id: string; tools: string[] }>;
}

export class BridgeToolsCache {
  private entry: { inventory: BridgeToolsInventory; asOfMs: number } | null = null;

  /** Store a freshly-fetched inventory with the current timestamp. */
  record(inventory: BridgeToolsInventory): void {
    this.entry = {
      // Shallow-clone the Set so a caller mutating its own copy cannot poison
      // the cache; groups is left as-is (callers do not mutate it).
      inventory: { tools: new Set(inventory.tools), groups: inventory.groups },
      asOfMs: Date.now(),
    };
  }

  /**
   * Return a cached inventory iff it is within the TTL window; otherwise null.
   * `ttlMs` defaults to the env-resolved value. A `ttlMs` of 0 disables the
   * cache (always returns null) — useful for tests and for forcing a refresh.
   * This 0-disables contract is checked BEFORE the age comparison so a 0 TTL
   * is deterministic (does not race the clock on a fast machine).
   */
  get(ttlMs: number = readBridgeToolsTtlMs()): BridgeToolsInventory | null {
    if (ttlMs <= 0) return null;
    const e = this.entry;
    if (e === null) return null;
    if (Date.now() - e.asOfMs > ttlMs) return null;
    return e.inventory;
  }

  /**
   * Drop the cached inventory. Called from the LiveClient's lifecycle hooks
   * (handleTransientOffline, waitForCompile settle) so the next call refetches
   * even within the TTL — a reload or compile may have changed the inventory.
   */
  invalidate(): void {
    this.entry = null;
  }
}
