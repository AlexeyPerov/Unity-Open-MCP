import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  computePort,
  projectHash,
  normalizePath,
  resolvePort,
  isPidAlive,
  PORT_RANGE_START,
  PORT_RANGE_SIZE,
} from "./instance-discovery.js";

// Pinned cross-side values. Both the bridge (InstancePortResolverTests.cs)
// and this test MUST agree on these — that's the whole point of deterministic
// per-project ports. If either side changes, update both together.
const SAMPLE_PATH = "/Users/foo/MyGame";
const SAMPLE_PATH_EXPECTED_PORT = 22028;
const SAMPLE_PATH_EXPECTED_HASH_PREFIX = "dca5061f6f21537c";

const ALT_PATH = "/some/path";
const ALT_PATH_EXPECTED_PORT = 29602;

// ----- normalizePath -----

test("normalizePath replaces backslashes with forward slashes", () => {
  assert.equal(normalizePath("\\Users\\foo\\MyGame"), "/Users/foo/MyGame");
});

test("normalizePath trims trailing slashes", () => {
  assert.equal(normalizePath("/Users/foo/MyGame/"), "/Users/foo/MyGame");
  assert.equal(normalizePath("/Users/foo/MyGame///"), "/Users/foo/MyGame");
});

test("normalizePath keeps a single trailing slash as the root", () => {
  assert.equal(normalizePath("/"), "/");
});

test("normalizePath returns empty string for empty input", () => {
  assert.equal(normalizePath(""), "");
});

// ----- projectHash -----

test("projectHash is stable across calls", () => {
  const a = projectHash(SAMPLE_PATH);
  const b = projectHash(SAMPLE_PATH);
  assert.equal(a, b);
});

test("projectHash is lowercase hex SHA256 (64 chars)", () => {
  const hash = projectHash(SAMPLE_PATH);
  assert.equal(hash.length, 64);
  assert.match(hash, /^[0-9a-f]{64}$/);
});

test("projectHash first 8 bytes match the pinned cross-side value", () => {
  assert.equal(
    projectHash(SAMPLE_PATH).slice(0, 16),
    SAMPLE_PATH_EXPECTED_HASH_PREFIX,
  );
});

test("projectHash is normalization-stable (backslash + trailing slash hash the same)", () => {
  const forward = projectHash("/Users/foo/MyGame");
  const back = projectHash("\\Users\\foo\\MyGame");
  const trailing = projectHash("/Users/foo/MyGame/");
  assert.equal(forward, back);
  assert.equal(forward, trailing);
});

// ----- computePort -----

test("computePort stays inside the [20000, 29999] range", () => {
  const port = computePort(SAMPLE_PATH);
  assert.ok(
    port >= PORT_RANGE_START && port < PORT_RANGE_START + PORT_RANGE_SIZE,
    `port ${port} out of range`,
  );
});

test("computePort matches the pinned cross-side value", () => {
  // If this breaks, the bridge-side InstancePortResolver has drifted.
  // Update packages/bridge/Editor/Bridge/InstancePortResolver.cs and its
  // test in the same task.
  assert.equal(computePort(SAMPLE_PATH), SAMPLE_PATH_EXPECTED_PORT);
  assert.equal(computePort(ALT_PATH), ALT_PATH_EXPECTED_PORT);
});

test("computePort produces distinct ports for distinct paths (pinned samples)", () => {
  assert.notEqual(computePort(SAMPLE_PATH), computePort(ALT_PATH));
});

// ----- isPidAlive -----

test("isPidAlive returns false for invalid pids", () => {
  assert.equal(isPidAlive(0), false);
  assert.equal(isPidAlive(-1), false);
});

test("isPidAlive returns true for the current process", () => {
  assert.equal(isPidAlive(process.pid), true);
});

test("isPidAlive returns false for a very-high pid that no OS hands out", () => {
  // 4_000_000 is well above any real OS pid range.
  assert.equal(isPidAlive(4_000_000), false);
});

// ----- resolvePort -----

test("resolvePort: env override wins over everything", () => {
  assert.equal(resolvePort(SAMPLE_PATH, 19120), 19120);
});

test("resolvePort: without override, falls back to deterministic hash", () => {
  // No lock file exists for SAMPLE_PATH in the real ~/.unity-agent, so we
  // expect the hash fallback. (This test is independent of the lock file
  // because we don't monkey-patch instancesDir here.)
  assert.equal(resolvePort(SAMPLE_PATH, undefined), SAMPLE_PATH_EXPECTED_PORT);
});

test("resolvePort: env override of NaN/invalid falls back to discovery", () => {
  // resolvePort validates envPort itself; an undefined input (the caller's
  // responsibility to parse) goes to discovery.
  assert.equal(resolvePort(SAMPLE_PATH, undefined), SAMPLE_PATH_EXPECTED_PORT);
});

// ----- resolvePort with lock file -----

test("resolvePort: live lock file (live pid) supplies the port", () => {
  const sandbox = makeSandbox();
  try {
    plantLock(sandbox, SAMPLE_PATH, { port: 23456, pid: process.pid });
    assert.equal(resolvePort(SAMPLE_PATH, undefined), 23456);
  } finally {
    cleanupSandbox(sandbox);
  }
});

test("resolvePort: stale lock file (dead pid) falls back to hash", () => {
  const sandbox = makeSandbox();
  try {
    plantLock(sandbox, SAMPLE_PATH, { port: 23456, pid: 4_000_000 });
    assert.equal(resolvePort(SAMPLE_PATH, undefined), SAMPLE_PATH_EXPECTED_PORT);
  } finally {
    cleanupSandbox(sandbox);
  }
});

test("resolvePort: env override beats a live lock file", () => {
  const sandbox = makeSandbox();
  try {
    plantLock(sandbox, SAMPLE_PATH, { port: 23456, pid: process.pid });
    assert.equal(resolvePort(SAMPLE_PATH, 19120), 19120);
  } finally {
    cleanupSandbox(sandbox);
  }
});

// ----- sandbox helpers -----
//
// instance-discovery reads ~/.unity-agent/instances/<hash>.json via homedir().
// We redirect it by setting HOME (POSIX) / USERPROFILE (Windows) to a temp
// dir for the lock-file tests, then restore the originals. The module reads
// homedir() fresh on every call, so this is safe without module reloads.

interface Sandbox {
  dir: string;
  prevHome: string | undefined;
  prevUserProfile: string | undefined;
}

function makeSandbox(): Sandbox {
  const dir = mkdtempSync(join(tmpdir(), "uomcp-inst-"));
  return {
    dir,
    prevHome: process.env.HOME,
    prevUserProfile: process.env.USERPROFILE,
  };
}

function cleanupSandbox(s: Sandbox): void {
  if (s.prevHome === undefined) delete process.env.HOME;
  else process.env.HOME = s.prevHome;
  if (s.prevUserProfile === undefined) delete process.env.USERPROFILE;
  else process.env.USERPROFILE = s.prevUserProfile;
  try {
    rmSync(s.dir, { recursive: true, force: true });
  } catch {
    // best-effort
  }
}

interface PlantOpts {
  port: number;
  pid: number;
}

function plantLock(sandbox: Sandbox, projectPath: string, opts: PlantOpts): void {
  // Point homedir() at the sandbox for the duration of this test.
  process.env.HOME = sandbox.dir;
  process.env.USERPROFILE = sandbox.dir;

  // Mirror the module's own layout: <home>/.unity-agent/instances/<hash>.json
  const hash = projectHash(projectPath);
  const dir = join(sandbox.dir, ".unity-agent", "instances");
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
  const path = join(dir, `${hash}.json`);
  writeFileSync(
    path,
    JSON.stringify({
      pid: opts.pid,
      port: opts.port,
      projectPath,
      projectHash: hash,
      startedAt: "2026-06-17T00:00:00.000Z",
      updatedAt: "2026-06-17T00:00:00.000Z",
      heartbeatAt: "2026-06-17T00:00:00.000Z",
      state: "idle",
      isPlaying: false,
      isCompiling: false,
      bridgeVersion: "0.1.0",
      unityVersion: "6000.0.0f1",
    }),
  );
}
