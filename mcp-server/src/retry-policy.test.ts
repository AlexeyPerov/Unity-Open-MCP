// Tests for the structured retry policy module. Pure data/function checks —
// no I/O, no HTTP. The env-override cases inject a fake environment.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  RETRY_CONFIG,
  retryConfigFor,
  readRetryTunables,
  RETRY_ENV,
  DEFAULT_COMPILE_WAIT_MS,
  DEFAULT_COMPILE_POLL_INTERVAL_MS,
  DEFAULT_TRANSIENT_RETRY_ATTEMPTS,
  DEFAULT_TRANSIENT_BACKOFF_MS,
  type RetryTunables,
} from "./retry-policy.js";
import type { LifecycleClass } from "./capabilities/lifecycle.js";

const ALL_CLASSES: LifecycleClass[] = [
  "none",
  "compile-reload",
  "modal-dialog",
  "scene-dirty",
  "process-stale",
];

test("RETRY_CONFIG covers every lifecycle class", () => {
  for (const cls of ALL_CLASSES) {
    const cfg = RETRY_CONFIG[cls];
    assert.ok(cfg, `missing config for ${cls}`);
    assert.equal(cfg.lifecycleClass, cls);
  }
});

test("scene-dirty never retries (maxAttempts === 0, settleStrategy none)", () => {
  const cfg = RETRY_CONFIG["scene-dirty"];
  assert.equal(cfg.maxAttempts, 0);
  assert.equal(cfg.settleStrategy, "none");
  assert.equal(cfg.backoffMs, 0);
});

test("compile-reload uses compile-settle", () => {
  assert.equal(RETRY_CONFIG["compile-reload"].settleStrategy, "compile-settle");
});

test("process-stale uses heartbeat-poll", () => {
  assert.equal(RETRY_CONFIG["process-stale"].settleStrategy, "heartbeat-poll");
});

test("none (read-only) uses transient-backoff", () => {
  assert.equal(RETRY_CONFIG.none.settleStrategy, "transient-backoff");
});

test("retryConfigFor returns the class config by default", () => {
  for (const cls of ALL_CLASSES) {
    const cfg = retryConfigFor(cls);
    assert.equal(cfg.lifecycleClass, cls);
    assert.equal(cfg.settleStrategy, RETRY_CONFIG[cls].settleStrategy);
  }
});

test("retryConfigFor falls back to 'none' for an unknown class", () => {
  // @ts-expect-error — deliberately unknown class
  const cfg = retryConfigFor("unknown-class");
  assert.equal(cfg.lifecycleClass, "none");
  assert.equal(cfg.settleStrategy, "transient-backoff");
});

test("retryConfigFor applies numeric overrides without changing strategy", () => {
  const base = RETRY_CONFIG["compile-reload"];
  const overrides: Partial<RetryTunables> = {
    transientRetryAttempts: 7,
    transientBackoffMs: 1234,
  };
  const cfg = retryConfigFor("compile-reload", overrides);
  assert.equal(cfg.maxAttempts, 7);
  assert.equal(cfg.backoffMs, 1234);
  // Strategy is structural — overrides must not change it.
  assert.equal(cfg.settleStrategy, base.settleStrategy);
});

test("retryConfigFor ignores invalid overrides (keeps defaults)", () => {
  const base = RETRY_CONFIG.none;
  const cfg = retryConfigFor("none", {
    transientRetryAttempts: -1, // invalid
    transientBackoffMs: 0, // invalid (must be positive)
  });
  assert.equal(cfg.maxAttempts, base.maxAttempts);
  assert.equal(cfg.backoffMs, base.backoffMs);
});

test("retryConfigFor preserves scene-dirty 0-retry even with overrides", () => {
  // scene-dirty's contract is "never retry"; an override to attempts must
  // still take effect (the operator asked for it) but the strategy stays none.
  const cfg = retryConfigFor("scene-dirty", { transientRetryAttempts: 5 });
  assert.equal(cfg.maxAttempts, 5);
  assert.equal(cfg.settleStrategy, "none");
});

// ---------------------------------------------------------------------------
// Env overrides → readRetryTunables.
// ---------------------------------------------------------------------------

test("readRetryTunables returns documented defaults for an empty env", () => {
  const t = readRetryTunables({});
  assert.equal(t.compileWaitMs, DEFAULT_COMPILE_WAIT_MS);
  assert.equal(t.compilePollIntervalMs, DEFAULT_COMPILE_POLL_INTERVAL_MS);
  assert.equal(t.transientRetryAttempts, DEFAULT_TRANSIENT_RETRY_ATTEMPTS);
  assert.equal(t.transientBackoffMs, DEFAULT_TRANSIENT_BACKOFF_MS);
});

test("readRetryTunables parses valid env overrides", () => {
  const env = {
    [RETRY_ENV.compileWait]: "60000",
    [RETRY_ENV.compilePollInterval]: "500",
    [RETRY_ENV.transientRetryAttempts]: "5",
    [RETRY_ENV.transientBackoff]: "250",
  };
  const t = readRetryTunables(env);
  assert.equal(t.compileWaitMs, 60000);
  assert.equal(t.compilePollIntervalMs, 500);
  assert.equal(t.transientRetryAttempts, 5);
  assert.equal(t.transientBackoffMs, 250);
});

test("readRetryTunables falls back on invalid env values", () => {
  const env = {
    [RETRY_ENV.compileWait]: "not-a-number",
    [RETRY_ENV.compilePollInterval]: "-1",
    [RETRY_ENV.transientRetryAttempts]: "abc",
    [RETRY_ENV.transientBackoff]: "0",
  };
  const t = readRetryTunables(env);
  assert.equal(t.compileWaitMs, DEFAULT_COMPILE_WAIT_MS);
  assert.equal(t.compilePollIntervalMs, DEFAULT_COMPILE_POLL_INTERVAL_MS);
  assert.equal(t.transientRetryAttempts, DEFAULT_TRANSIENT_RETRY_ATTEMPTS);
  assert.equal(t.transientBackoffMs, DEFAULT_TRANSIENT_BACKOFF_MS);
});

test("readRetryTunables accepts transientRetryAttempts=0 (opt-out)", () => {
  // 0 attempts is a valid "disable retries" opt-out; the parser must accept it.
  const t = readRetryTunables({ [RETRY_ENV.transientRetryAttempts]: "0" });
  assert.equal(t.transientRetryAttempts, 0);
});
