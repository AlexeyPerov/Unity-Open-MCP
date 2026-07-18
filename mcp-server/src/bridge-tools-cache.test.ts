// M31-optimizations Plan 1 / M3 — unit tests for the BridgeToolsCache that
// backs LiveClient.listBridgeTools. The TTL window + invalidation contract is
// what lets the meta-tools collapse their double-/triple-fetch of `GET
// /tools` per call; these tests pin both halves of that contract.

import test from "node:test";
import assert from "node:assert/strict";

import {
  BridgeToolsCache,
  DEFAULT_BRIDGE_TOOLS_TTL_MS,
  readBridgeToolsTtlMs,
  type BridgeToolsInventory,
} from "./bridge-tools-cache.js";

function makeInventory(tools: string[] = ["unity_open_mcp_ping"]): BridgeToolsInventory {
  return { tools: new Set(tools), groups: [] };
}

test("BridgeToolsCache starts empty", () => {
  const cache = new BridgeToolsCache();
  assert.equal(cache.get(), null);
});

test("BridgeToolsCache returns the recorded inventory within the TTL", () => {
  const cache = new BridgeToolsCache();
  cache.record(makeInventory(["a", "b"]));
  const got = cache.get();
  assert.ok(got);
  assert.deepEqual(Array.from(got!.tools).sort(), ["a", "b"]);
});

test("BridgeToolsCache returns null after invalidate()", () => {
  const cache = new BridgeToolsCache();
  cache.record(makeInventory());
  assert.ok(cache.get() !== null, "sanity: cache populated");
  cache.invalidate();
  assert.equal(cache.get(), null, "invalidate() must drop the entry");
});

test("BridgeToolsCache expires entries past the TTL", async () => {
  const cache = new BridgeToolsCache();
  cache.record(makeInventory());
  // Within a 10s window — must serve.
  assert.ok(cache.get(10_000) !== null, "within TTL must serve");
  // Sleep past a 1ms TTL so the entry is unambiguously expired (no clock-tick
  // race — `Date.now()` is guaranteed to have advanced).
  await new Promise<void>((resolve) => setTimeout(() => resolve(), 5));
  assert.equal(cache.get(1), null, "past TTL must NOT serve");
});

test("BridgeToolsCache isolates the cached Set from caller mutations", () => {
  // The cache shallow-clones the Set on record(); a caller mutating its own
  // copy must NOT poison the cached inventory (otherwise a follow-up call
  // would see the mutated set and skip a needed refetch).
  const cache = new BridgeToolsCache();
  const inv = makeInventory(["a"]);
  cache.record(inv);
  inv.tools.add("poison");
  const got = cache.get();
  assert.ok(got);
  assert.deepEqual(
    Array.from(got!.tools),
    ["a"],
    "cached Set must be independent of the caller's Set",
  );
});

test("readBridgeToolsTtlMs: default is DEFAULT_BRIDGE_TOOLS_TTL_MS when env unset", () => {
  const prev = process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS;
  delete process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS;
  try {
    assert.equal(readBridgeToolsTtlMs(), DEFAULT_BRIDGE_TOOLS_TTL_MS);
  } finally {
    if (prev !== undefined) process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS = prev;
  }
});

test("readBridgeToolsTtlMs: env override wins", () => {
  const prev = process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS;
  process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS = "7000";
  try {
    assert.equal(readBridgeToolsTtlMs(), 7000);
  } finally {
    if (prev === undefined) delete process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS;
    else process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS = prev;
  }
});

test("readBridgeToolsTtlMs: 0 disables caching (always returns null)", () => {
  // A 0 TTL is the documented operator escape hatch for "every call hits the
  // network" — useful when debugging a stale-inventory issue.
  const prev = process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS;
  process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS = "0";
  try {
    assert.equal(readBridgeToolsTtlMs(), 0);
    const cache = new BridgeToolsCache();
    cache.record(makeInventory());
    assert.equal(
      cache.get(),
      null,
      "a 0 TTL disables the cache (get() always returns null)",
    );
  } finally {
    if (prev === undefined) delete process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS;
    else process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS = prev;
  }
});

test("readBridgeToolsTtlMs: unparseable / negative / whitespace values fall back to default", () => {
  const prev = process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS;
  // "" / " " are not deliberate 0s — fall back to default. "-1" / "abc" /
  // "NaN" are not valid TTLs — fall back. ("0" is a deliberate operator
  // escape hatch and is exercised in its own test below.)
  for (const bad of ["", "abc", "-1", "NaN", " "]) {
    process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS = bad;
    assert.equal(
      readBridgeToolsTtlMs(),
      DEFAULT_BRIDGE_TOOLS_TTL_MS,
      `invalid TTL '${bad}' must fall back to the default`,
    );
  }
  if (prev === undefined) delete process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS;
  else process.env.UNITY_OPEN_MCP_TOOLS_CACHE_TTL_MS = prev;
});
