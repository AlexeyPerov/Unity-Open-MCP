/**
 * Node:test coverage for the running-Unity store's pure lookup methods:
 * isRunningForPid / isRunningForPath. These match the `running` chip in the
 * Projects tab against the last process scan.
 *
 * The store uses Svelte 5 `$state` runes; the `test:state` npm script injects
 * a shim (see _test-shim.ts) so the class constructs. These tests seed the
 * `byPid` / `paths` fields directly — no setInterval, no Tauri scan.
 *
 * Run with: `npm run test:state`.
 */
import test from "node:test";
import assert from "node:assert/strict";

import { runningUnityStore } from "./running_unity.svelte.ts";
import type { RunningUnity } from "$lib/services/config.ts";

function reset() {
  runningUnityStore.byPid = {};
  runningUnityStore.paths = new Set();
}

function proc(pid: number, projectPath?: string): RunningUnity {
  return {
    pid,
    projectPath: projectPath ?? null,
    // The remaining fields are not read by the lookup methods.
  } as RunningUnity;
}

// ---------------------------------------------------------------------------
// isRunningForPid
// ---------------------------------------------------------------------------

test("isRunningForPid returns true for a known pid", () => {
  reset();
  runningUnityStore.byPid = { 12345: proc(12345) };
  assert.equal(runningUnityStore.isRunningForPid(12345), true);
});

test("isRunningForPid returns false for an unknown pid", () => {
  reset();
  runningUnityStore.byPid = { 12345: proc(12345) };
  assert.equal(runningUnityStore.isRunningForPid(99999), false);
});

test("isRunningForPid returns false for null / undefined / 0", () => {
  reset();
  runningUnityStore.byPid = { 12345: proc(12345) };
  assert.equal(runningUnityStore.isRunningForPid(null), false);
  assert.equal(runningUnityStore.isRunningForPid(undefined), false);
  assert.equal(runningUnityStore.isRunningForPid(0), false);
});

test("isRunningForPid on an empty scan is always false", () => {
  reset();
  assert.equal(runningUnityStore.isRunningForPid(1), false);
});

// ---------------------------------------------------------------------------
// isRunningForPath
// ---------------------------------------------------------------------------

test("isRunningForPath returns true for a known path", () => {
  reset();
  runningUnityStore.paths = new Set(["/proj/a"]);
  assert.equal(runningUnityStore.isRunningForPath("/proj/a"), true);
});

test("isRunningForPath returns false for an unknown path", () => {
  reset();
  runningUnityStore.paths = new Set(["/proj/a"]);
  assert.equal(runningUnityStore.isRunningForPath("/proj/b"), false);
});

test("isRunningForPath returns false for null / undefined / empty", () => {
  reset();
  runningUnityStore.paths = new Set(["/proj/a"]);
  assert.equal(runningUnityStore.isRunningForPath(null), false);
  assert.equal(runningUnityStore.isRunningForPath(undefined), false);
  assert.equal(runningUnityStore.isRunningForPath(""), false);
});
