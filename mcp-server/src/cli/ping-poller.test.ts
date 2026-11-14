// Tests for the single compile-aware /ping probe (src/cli/ping-poller.ts) used
// by `ping` and `status`: ready only when /ping says connected AND not
// compiling (nor changing play mode).
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { projectHash } from "../instance-discovery.js";

import {
  singlePing,
  extractPingBody,
} from "./ping-poller.js";
import type { LiveClient } from "../live-client.js";
import type { PingSnapshot } from "../ping-cache.js";

// wait-for-ready's wait (settled, no compile due, --after-generation) is
// tested with the shared editor_status wait in ../editor-state.test.ts.

// ---------------------------------------------------------------------------
// extractPingBody
// ---------------------------------------------------------------------------
test("extractPingBody: parses a text content ping body", () => {
  const body = extractPingBody({
    content: [{ type: "text", text: '{"connected":true,"compiling":false}' }],
  });
  assert.deepEqual(body, { connected: true, compiling: false });
});

test("extractPingBody: returns null for missing/invalid text", () => {
  assert.equal(extractPingBody({ content: [] }), null);
  assert.equal(
    extractPingBody({ content: [{ type: "image", text: "x" }] }),
    null,
  );
  assert.equal(
    extractPingBody({ content: [{ type: "text", text: "not-json" }] }),
    null,
  );
});

// ---------------------------------------------------------------------------
// M14 — singlePing honors fetchTimeoutMs and reads the cached body (no second /ping)
// ---------------------------------------------------------------------------

// A fake LiveClient that records the fetchTimeoutMs passed to isLiveAvailable
// and serves a configurable cached snapshot. route() is intentionally NOT
// implemented — singlePing must NOT call it (the old code issued two /ping
// requests per poll; the fix reads the cache the single probe populated).
function makeProbeFake(opts: {
  available: boolean;
  cached: PingSnapshot | null;
}): { live: LiveClient; seenTimeouts: number[]; routeCalled: boolean } {
  const seenTimeouts: number[] = [];
  let routeCalled = false;
  const live = {
    async isLiveAvailable(fetchTimeoutMs?: number): Promise<boolean> {
      seenTimeouts.push(fetchTimeoutMs ?? -1);
      return opts.available;
    },
    getCachedPing(): PingSnapshot | null {
      return opts.cached;
    },
    async route(): Promise<never> {
      routeCalled = true;
      throw new Error("singlePing must not call route() — read getCachedPing()");
    },
  } as unknown as LiveClient;
  return { live, seenTimeouts, get routeCalled() { return routeCalled; } };
}

test("M14: singlePing forwards fetchTimeoutMs to isLiveAvailable (no longer ignored)", async () => {
  // Regression: singlePing(live, fetchTimeoutMs) previously declared the param
  // but never read it. The CLI `ping --timeout-ms` value is threaded through
  // here, so it must reach the underlying fetch.
  const fake = makeProbeFake({
    available: true,
    cached: {
      connected: true, compiling: false, isPlaying: false,
      projectPath: null, unityVersion: null, bridgeVersion: "x", mode: "live",
      asOf: new Date().toISOString(),
    },
  });
  await singlePing(fake.live, 12_345);
  assert.deepEqual(
    fake.seenTimeouts,
    [12_345],
    "fetchTimeoutMs must be forwarded to isLiveAvailable (was silently ignored pre-fix)",
  );
  assert.equal(fake.routeCalled, false, "must not issue a second /ping via route()");
});

test("M14: singlePing uses the cached body (single /ping round-trip)", async () => {
  const cached: PingSnapshot = {
    connected: true, compiling: false, isPlaying: false,
    projectPath: null, unityVersion: null, bridgeVersion: "x", mode: "live",
    asOf: new Date().toISOString(),
  };
  const fake = makeProbeFake({ available: true, cached });
  const result = await singlePing(fake.live);
  assert.equal(result.status, "ready");
  assert.equal(result.body, cached, "body comes from getCachedPing, not a second probe");
});

test("M14: singlePing treats a missing cached body (503 compile) as compiling", async () => {
  // isLiveAvailable returns true on a 503 but records no body → getCachedPing
  // is null. singlePing must report compiling, not error.
  const fake = makeProbeFake({ available: true, cached: null });
  const result = await singlePing(fake.live);
  assert.equal(result.status, "compiling");
  assert.equal(result.body, null);
});

test("M14: singlePing reports offline when isLiveAvailable is false", async () => {
  const fake = makeProbeFake({ available: false, cached: null });
  const result = await singlePing(fake.live);
  assert.equal(result.status, "offline");
  assert.equal(result.body, null);
});


// ---------------------------------------------------------------------------
// sanity: projectHash + createHash agree (guards the test helper itself)
// ---------------------------------------------------------------------------

test("projectHash matches createHash(normalizePath) for the dead-bridge sample", () => {
  // If this ever drifts, the dead-bridge test above plants a lock at the wrong
  // path and the fail-fast assertion silently no-ops.
  const projectPath = "/fake/project/for/dead-bridge-test";
  const norm = projectPath.replace(/\\/g, "/").replace(/\/+$/, "") || "/";
  const expected = createHash("sha256").update(norm, "utf8").digest("hex");
  assert.equal(projectHash(projectPath), expected);
});
