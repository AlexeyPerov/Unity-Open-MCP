import test from "node:test";
import assert from "node:assert/strict";

import {
  PingCache,
  DEFAULT_PING_CACHE_TTL_MS,
  readPingCacheTtlMs,
} from "./ping-cache.js";

function makePingBody() {
  return {
    connected: true,
    projectPath: "/path/to/MyGame",
    unityVersion: "6000.0.23f1",
    bridgeVersion: "0.1.0",
    mode: "live",
    compiling: false,
    isPlaying: false,
  };
}

test("PingCache starts empty", () => {
  const cache = new PingCache();
  assert.equal(cache.get(), null);
});

test("PingCache records a ping snapshot with asOf", () => {
  const cache = new PingCache();
  cache.record(makePingBody());
  const snapshot = cache.get();
  assert.ok(snapshot);
  assert.equal(snapshot!.connected, true);
  assert.equal(snapshot!.projectPath, "/path/to/MyGame");
  assert.ok(snapshot!.asOf);
  assert.ok(!isNaN(Date.parse(snapshot!.asOf)));
});

test("PingCache overwrites on subsequent records", () => {
  const cache = new PingCache();
  cache.record(makePingBody());
  const first = cache.get()!;
  assert.equal(first.connected, true);

  cache.record({ ...makePingBody(), connected: false });
  const second = cache.get()!;
  assert.equal(second.connected, false);
});

test("PingCache snapshot is independent of the input object", () => {
  const cache = new PingCache();
  const body = makePingBody();
  cache.record(body);
  body.connected = false;

  const snapshot = cache.get()!;
  assert.equal(snapshot.connected, true, "cache should not reflect mutation");
});

// ----- M31-optimizations Plan 1 / H1 — TTL-gated freshness for ensureReady -----

test("PingCache.fresh: returns null when no snapshot has been recorded", () => {
  const cache = new PingCache();
  assert.equal(cache.fresh(), null);
});

test("PingCache.fresh: returns the snapshot when fresh + connected + idle", () => {
  const cache = new PingCache();
  cache.record(makePingBody());
  const snap = cache.fresh();
  assert.ok(snap, "fresh snapshot must be returned within the TTL");
  assert.equal(snap!.connected, true);
});

test("PingCache.fresh: returns null when the TTL is 0 (disabled)", () => {
  // A 0 TTL is the operator / test escape hatch that forces a real probe on
  // every call. Checked BEFORE the age comparison so it is deterministic.
  const cache = new PingCache();
  cache.record(makePingBody());
  assert.equal(
    cache.fresh(0),
    null,
    "a 0 TTL disables the cache (fresh() always returns null)",
  );
});

test("PingCache.fresh: returns null when the snapshot is older than the TTL", () => {
  // Backdate the snapshot's `asOf` to a clearly past instant so the test does
  // not race the clock. (record() uses `new Date().toISOString()`.)
  const cache = new PingCache();
  cache.record(makePingBody());
  cache.get()!.asOf = new Date(Date.now() - 10_000).toISOString();
  assert.equal(
    cache.fresh(5_000),
    null,
    "an expired snapshot must NOT short-circuit",
  );
});

test("PingCache.fresh: a `compiling` snapshot forces a fresh probe (never short-circuit)", () => {
  const cache = new PingCache();
  cache.record({ ...makePingBody(), compiling: true });
  assert.equal(
    cache.fresh(),
    null,
    "compiling=true must always force a real probe so the wait loop can observe it settle",
  );
});

test("PingCache.fresh: a disconnected snapshot forces a fresh probe", () => {
  const cache = new PingCache();
  cache.record({ ...makePingBody(), connected: false });
  assert.equal(
    cache.fresh(),
    null,
    "connected=false must force a real probe so recovery is observed",
  );
});

test("PingCache.fresh: honors a custom TTL (within window returns snapshot)", () => {
  const cache = new PingCache();
  cache.record(makePingBody());
  // 10s window — well beyond the recorded `asOf`.
  const snap = cache.fresh(10_000);
  assert.ok(snap, "a custom TTL wider than the snapshot age must return it");
});

test("readPingCacheTtlMs: default is DEFAULT_PING_CACHE_TTL_MS when env unset", () => {
  const prev = process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS;
  delete process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS;
  try {
    assert.equal(readPingCacheTtlMs(), DEFAULT_PING_CACHE_TTL_MS);
  } finally {
    if (prev !== undefined) process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS = prev;
  }
});

test("readPingCacheTtlMs: env override wins", () => {
  const prev = process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS;
  process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS = "7500";
  try {
    assert.equal(readPingCacheTtlMs(), 7500);
  } finally {
    if (prev === undefined) delete process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS;
    else process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS = prev;
  }
});

test("readPingCacheTtlMs: unparseable / negative / whitespace values fall back to default", () => {
  const prev = process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS;
  // "" / " " are not deliberate 0s — fall back to default. "-1" / "abc" /
  // "NaN" are not valid TTLs — fall back. ("0" is a deliberate operator
  // escape hatch and is exercised in its own test below.)
  for (const bad of ["", "abc", "-1", "NaN", " "]) {
    process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS = bad;
    assert.equal(
      readPingCacheTtlMs(),
      DEFAULT_PING_CACHE_TTL_MS,
      `invalid TTL '${bad}' must fall back to the default`,
    );
  }
  if (prev === undefined) delete process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS;
  else process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS = prev;
});

test("readPingCacheTtlMs: explicit 0 is honored (forces a real probe every call)", () => {
  // 0 is the operator / test escape hatch — NOT a fall-back case. fresh(0)
  // returns null unconditionally, so the agent re-probes /ping every call.
  const prev = process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS;
  process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS = "0";
  try {
    assert.equal(readPingCacheTtlMs(), 0);
    const cache = new PingCache();
    cache.record(makePingBody());
    assert.equal(cache.fresh(), null, "0 TTL forces a probe every call");
  } finally {
    if (prev === undefined) delete process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS;
    else process.env.UNITY_OPEN_MCP_PING_CACHE_TTL_MS = prev;
  }
});

