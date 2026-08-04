// Unit tests for the short-TTL StaleAssemblyCache that backs the
// `_staleDomain` annotation. Mirrors the BridgeToolsCache test shape.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  StaleAssemblyCache,
  DEFAULT_STALE_ASSEMBLY_TTL_MS,
  readStaleAssemblyTtlMs,
} from "./stale-assembly-cache.js";
import type { StaleAssemblyResult } from "./unity-log.js";

const STALE: StaleAssemblyResult = {
  staleAssembly: true,
  dllMtimeMs: 1000,
  newerSources: ["Assets/Scripts/Foo.cs"],
  hint: "stale",
};
const FRESH: StaleAssemblyResult = {
  staleAssembly: false,
  dllMtimeMs: 5000,
  newerSources: [],
  hint: "",
};

test("StaleAssemblyCache: returns null when empty", () => {
  const c = new StaleAssemblyCache();
  assert.equal(c.get(), null);
});

test("StaleAssemblyCache: returns the recorded result within the TTL", () => {
  const c = new StaleAssemblyCache();
  c.record(STALE);
  const got = c.get();
  assert.equal(got, STALE, "get returns the same reference within the TTL");
  assert.equal(got?.staleAssembly, true);
});

test("StaleAssemblyCache: returns null after the TTL elapses", async () => {
  const c = new StaleAssemblyCache();
  c.record(STALE);
  // 1ms TTL — immediately expired by the time get runs.
  assert.equal(c.get(1), STALE);
  await new Promise((r) => setTimeout(r, 10));
  assert.equal(c.get(1), null, "expired entry returns null");
});

test("StaleAssemblyCache: ttlMs=0 disables the cache (always null)", () => {
  const c = new StaleAssemblyCache();
  c.record(FRESH);
  assert.equal(c.get(0), null, "ttlMs=0 must disable caching");
});

test("StaleAssemblyCache: invalidate drops the entry", () => {
  const c = new StaleAssemblyCache();
  c.record(STALE);
  assert.equal(c.get()?.staleAssembly, true);
  c.invalidate();
  assert.equal(c.get(), null);
});

test("StaleAssemblyCache: records a fresh (not-stale) result faithfully", () => {
  const c = new StaleAssemblyCache();
  c.record(FRESH);
  const got = c.get();
  assert.equal(got?.staleAssembly, false);
  assert.deepEqual(got?.newerSources, []);
});

test("readStaleAssemblyTtlMs: default when unset", () => {
  const prev = process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS;
  delete process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS;
  try {
    assert.equal(readStaleAssemblyTtlMs(), DEFAULT_STALE_ASSEMBLY_TTL_MS);
  } finally {
    if (prev === undefined) delete process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS;
    else process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS = prev;
  }
});

test("readStaleAssemblyTtlMs: explicit value wins", () => {
  const prev = process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS;
  process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS = "7000";
  try {
    assert.equal(readStaleAssemblyTtlMs(), 7000);
  } finally {
    if (prev === undefined) delete process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS;
    else process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS = prev;
  }
});

test("readStaleAssemblyTtlMs: 0 disables (passes through, not reset to default)", () => {
  const prev = process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS;
  process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS = "0";
  try {
    assert.equal(readStaleAssemblyTtlMs(), 0);
  } finally {
    if (prev === undefined) delete process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS;
    else process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS = prev;
  }
});

test("readStaleAssemblyTtlMs: negative / non-numeric fall back to default", () => {
  const prev = process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS;
  try {
    process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS = "-5";
    assert.equal(readStaleAssemblyTtlMs(), DEFAULT_STALE_ASSEMBLY_TTL_MS);
    process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS = "abc";
    assert.equal(readStaleAssemblyTtlMs(), DEFAULT_STALE_ASSEMBLY_TTL_MS);
    process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS = "  ";
    assert.equal(readStaleAssemblyTtlMs(), DEFAULT_STALE_ASSEMBLY_TTL_MS);
  } finally {
    if (prev === undefined) delete process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS;
    else process.env.UNITY_OPEN_MCP_STALE_ASSEMBLY_TTL_MS = prev;
  }
});
