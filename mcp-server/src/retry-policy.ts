// Structured retry policy per agent-facing lifecycle class.
//
// Formalizes the ad-hoc retry loops that previously lived as module constants
// in live-client.ts (TRANSIENT_RETRY_ATTEMPTS / TRANSIENT_RETRY_BACKOFF_MS /
// MAX_COMPILE_WAIT_MS). Each lifecycle class (capabilities/lifecycle.ts) now
// carries an explicit retry + settle policy so an agent (or the MCP server on
// its behalf) can branch recovery programmatically instead of parsing
// free-text errors or relying on hardcoded numbers.
//
// The policy is consulted by the LiveClient's transient-offline handler: given
// the tool's lifecycle class, it picks the settle strategy and backoff
// parameters. The dead-bridge / reloading classification in
// instance-discovery.ts is unchanged — this module only governs *how long and
// how often* to retry, not *what* a failure means.
//
// Pure data + pure functions (no I/O). Env overrides are parsed once via
// readRetryConfig() so the live-client can resolve them at construction time,
// mirroring the readDismissConfig() pattern in dialog-dismiss.ts.

import type { LifecycleClass } from "./capabilities/lifecycle.js";

// ---------------------------------------------------------------------------
// Settle strategies (one per lifecycle class).
// ---------------------------------------------------------------------------

/**
 * How the LiveClient should wait between a failure and the next attempt.
 *
 *   - `compile-settle`  — poll /ping until the domain reload completes (the
 *                         bridge tears down its listener for the reload; the
 *                         poll detects when it comes back). Used by
 *                         compile-reload tools.
 *   - `heartbeat-poll`  — bounded backoff re-probing /ping; relies on the
 *                         heartbeat staying fresh. Used by process-stale tools
 *                         where the bridge may stall but not die.
 *   - `transient-backoff` — short escalating backoff for connection churn that
 *                         isn't reflected in the lock yet. The default for
 *                         read-only (none) tools.
 *   - `none`            — do not retry. The failure is surfaced immediately;
 *                         the agent must resolve the underlying state (e.g.
 *                         save or discard a dirty scene before retrying a
 *                         scene-dirty tool).
 */
export type SettleStrategy =
  | "compile-settle"
  | "heartbeat-poll"
  | "transient-backoff"
  | "none";

export interface RetryConfig {
  /** Lifecycle class this config applies to. */
  readonly lifecycleClass: LifecycleClass;
  /** Max retry attempts after the initial failure (0 = never retry). */
  readonly maxAttempts: number;
  /** Base backoff in ms; the n-th attempt sleeps backoffMs * n (escalating). */
  readonly backoffMs: number;
  /** How to wait between attempts. */
  readonly settleStrategy: SettleStrategy;
}

// ---------------------------------------------------------------------------
// Per-class defaults.
//
// These are the documented defaults; every value is overridable via env vars
// (see readRetryConfig). The numbers match the pre-existing hardcoded values
// in live-client.ts so behaviour is preserved when no env override is set.
// ---------------------------------------------------------------------------

export const DEFAULT_COMPILE_WAIT_MS = 120_000;
export const DEFAULT_COMPILE_POLL_INTERVAL_MS = 2_000;
export const DEFAULT_TRANSIENT_RETRY_ATTEMPTS = 3;
export const DEFAULT_TRANSIENT_BACKOFF_MS = 500;

/**
 * Per-class retry policy. The `none` (read-only) class inherits the original
 * transient-backoff behaviour; `scene-dirty` explicitly does not retry (the
 * guard error must be resolved by the agent, not retried through).
 */
export const RETRY_CONFIG: Readonly<Record<LifecycleClass, RetryConfig>> = {
  // Read-only: short transient retry for socket churn during a reload.
  none: {
    lifecycleClass: "none",
    maxAttempts: DEFAULT_TRANSIENT_RETRY_ATTEMPTS,
    backoffMs: DEFAULT_TRANSIENT_BACKOFF_MS,
    settleStrategy: "transient-backoff",
  },
  // Domain reload: wait out the compile, then retry once the listener is back.
  "compile-reload": {
    lifecycleClass: "compile-reload",
    maxAttempts: DEFAULT_TRANSIENT_RETRY_ATTEMPTS,
    backoffMs: DEFAULT_TRANSIENT_BACKOFF_MS,
    settleStrategy: "compile-settle",
  },
  // OS modal: no first-class retry here — the dialog dismiss loop
  // (dialog-policy.ts) runs concurrently inside the compile-settle wait. A
  // mid-call modal that the dismiss loop cannot handle surfaces as a
  // timeout; the agent must dismiss it manually.
  "modal-dialog": {
    lifecycleClass: "modal-dialog",
    maxAttempts: DEFAULT_TRANSIENT_RETRY_ATTEMPTS,
    backoffMs: DEFAULT_TRANSIENT_BACKOFF_MS,
    settleStrategy: "compile-settle",
  },
  // scene-dirty: NEVER retry. The dirty guard refusal is the contract — the
  // agent must save/discard or pass ignore_scene_dirty: true.
  "scene-dirty": {
    lifecycleClass: "scene-dirty",
    maxAttempts: 0,
    backoffMs: 0,
    settleStrategy: "none",
  },
  // process-stale: the heartbeat may stall during a long op; re-probe with
  // bounded backoff rather than declaring the bridge dead.
  "process-stale": {
    lifecycleClass: "process-stale",
    maxAttempts: DEFAULT_TRANSIENT_RETRY_ATTEMPTS,
    backoffMs: DEFAULT_TRANSIENT_BACKOFF_MS,
    settleStrategy: "heartbeat-poll",
  },
};

/**
 * Resolve the retry policy for a lifecycle class. Falls back to the `none`
 * (read-only) policy for an unknown class so a new class added without an
 * entry gets safe defaults.
 */
export function retryConfigFor(
  lifecycleClass: LifecycleClass,
  overrides?: Partial<RetryTunables>,
): RetryConfig {
  const base = RETRY_CONFIG[lifecycleClass] ?? RETRY_CONFIG.none;
  if (!overrides) return base;
  // Only the numeric tunables are overridable; the settle strategy is a
  // structural property of the class, not a knob.
  return {
    ...base,
    maxAttempts: clampNonNegInt(
      overrides.transientRetryAttempts,
      base.maxAttempts,
    ),
    backoffMs: clampPositiveInt(
      overrides.transientBackoffMs,
      base.backoffMs,
    ),
  };
}

// ---------------------------------------------------------------------------
// Env-overridable tunables.
// ---------------------------------------------------------------------------

/**
 * Numeric tunables an operator can override via env vars without changing the
 * per-class settle strategy. Resolved once at LiveClient construction.
 */
export interface RetryTunables {
  /** Hard cap on the compile-settle poll loop, in ms. */
  readonly compileWaitMs: number;
  /** Interval between /ping probes during compile-settle, in ms. */
  readonly compilePollIntervalMs: number;
  /** Max transient retry attempts for the backoff loops. */
  readonly transientRetryAttempts: number;
  /** Base backoff (ms) for the transient-backoff + heartbeat-poll loops. */
  readonly transientBackoffMs: number;
}

export const RETRY_ENV = {
  compileWait: "UNITY_OPEN_MCP_COMPILE_WAIT_MS",
  compilePollInterval: "UNITY_OPEN_MCP_COMPILE_POLL_INTERVAL_MS",
  transientRetryAttempts: "UNITY_OPEN_MCP_TRANSIENT_RETRY_ATTEMPTS",
  transientBackoff: "UNITY_OPEN_MCP_TRANSIENT_BACKOFF_MS",
} as const;

/**
 * Read retry tunables from the environment. Unset / invalid values fall back
 * to the documented defaults. Pure aside from the env read; takes the env as
 * an argument so tests can inject a fake environment.
 */
export function readRetryTunables(
  env: NodeJS.ProcessEnv = process.env,
): RetryTunables {
  return {
    compileWaitMs: parsePositiveInt(
      env[RETRY_ENV.compileWait],
      DEFAULT_COMPILE_WAIT_MS,
    ),
    compilePollIntervalMs: parsePositiveInt(
      env[RETRY_ENV.compilePollInterval],
      DEFAULT_COMPILE_POLL_INTERVAL_MS,
    ),
    transientRetryAttempts: parseNonNegInt(
      env[RETRY_ENV.transientRetryAttempts],
      DEFAULT_TRANSIENT_RETRY_ATTEMPTS,
    ),
    transientBackoffMs: parsePositiveInt(
      env[RETRY_ENV.transientBackoff],
      DEFAULT_TRANSIENT_BACKOFF_MS,
    ),
  };
}

// ---------------------------------------------------------------------------
// Helpers.
// ---------------------------------------------------------------------------

function parsePositiveInt(raw: string | undefined, fallback: number): number {
  if (raw === undefined || raw === "") return fallback;
  const n = parseInt(raw, 10);
  if (!Number.isFinite(n) || n <= 0) return fallback;
  return n;
}

function parseNonNegInt(raw: string | undefined, fallback: number): number {
  if (raw === undefined || raw === "") return fallback;
  const n = parseInt(raw, 10);
  if (!Number.isFinite(n) || n < 0) return fallback;
  return n;
}

function clampPositiveInt(
  value: number | undefined,
  fallback: number,
): number {
  if (value === undefined) return fallback;
  if (!Number.isFinite(value) || value <= 0) return fallback;
  return value;
}

function clampNonNegInt(
  value: number | undefined,
  fallback: number,
): number {
  if (value === undefined) return fallback;
  if (!Number.isFinite(value) || value < 0) return fallback;
  return value;
}
