// feedback-fable-04-08 §5 — short-TTL in-memory cache for the
// detectStaleAssembly() result used by the `_staleDomain` annotation on
// execute_csharp / invoke_method. The detection stat()s up to ~4000
// `Assets/**/*.cs` files per call; running it on every execute_csharp
// invocation would dominate the hot path. The staleness signal changes only
// on a domain reload (seconds-to-minutes timescale), so a short TTL collapses
// repeated probes in a single agent turn without masking a real reload.
//
// Mirrors BridgeToolsCache: session-scoped (per-LiveClient, in-memory only),
// invalidated on the same lifecycle signals (dead-bridge / reload, compile
// settle), env-driven TTL with a 0-disables contract. No persistent disk
// cache (root AGENTS.md).

import type { StaleAssemblyResult } from "./unity-log.js";

/**
 * Default freshness window for the stale-assembly signal. Picked at the upper
 * end of the band a single agent turn spans (a probe → act → re-probe sequence
 * typically lands inside 5s), so repeated execute_csharp / invoke_method calls
 * in one turn reuse one scan while a genuine reload — which also fires the
 * explicit {@link StaleAssemblyCache.invalidate} — is still observed promptly.
 * Override with `UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS=<millis>`.
 */
export const DEFAULT_STALE_ASSEMBLY_TTL_MS = 5_000;

/**
 * Read the stale-assembly cache TTL from the env, falling back to the default
 * when unset or unparseable. Negative / NaN / non-numeric fall back to the
 * default; an explicit `0` disables caching (every probe re-scans), which is
 * useful for tests and for an operator debugging a false stale signal.
 * Whitespace-only values fall back to the default (they are not a deliberate 0).
 */
export function readStaleAssemblyTtlMs(): number {
  const raw = process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS;
  if (raw === undefined) return DEFAULT_STALE_ASSEMBLY_TTL_MS;
  const trimmed = raw.trim();
  if (trimmed === "") return DEFAULT_STALE_ASSEMBLY_TTL_MS;
  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed) || parsed < 0) return DEFAULT_STALE_ASSEMBLY_TTL_MS;
  return Math.floor(parsed);
}

/**
 * Short-TTL cache for {@link detectStaleAssembly} results. See
 * {@link BridgeToolsCache} for the contract this mirrors.
 */
export class StaleAssemblyCache {
  private entry: { result: StaleAssemblyResult; asOfMs: number } | null = null;

  /** Store a freshly-computed result with the current timestamp. */
  record(result: StaleAssemblyResult): void {
    this.entry = { result, asOfMs: Date.now() };
  }

  /**
   * Return a cached result iff it is within the TTL window; otherwise null.
   * `ttlMs` defaults to the env-resolved value. A `ttlMs` of 0 disables the
   * cache (always returns null). The 0-disables contract is checked BEFORE the
   * age comparison so a 0 TTL is deterministic.
   */
  get(ttlMs: number = readStaleAssemblyTtlMs()): StaleAssemblyResult | null {
    if (ttlMs <= 0) return null;
    const e = this.entry;
    if (e === null) return null;
    if (Date.now() - e.asOfMs > ttlMs) return null;
    return e.result;
  }

  /**
   * Drop the cached result. Called from the LiveClient's lifecycle hooks so
   * the next probe re-scans even within the TTL — a reload may have rebuilt
   * the assembly and cleared the staleness the cache still reports.
   */
  invalidate(): void {
    this.entry = null;
  }
}
