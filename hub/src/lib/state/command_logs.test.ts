/**
 * Node:test coverage for the command-logs store — the per-(project, panel)
 * ring buffer that backs the Open-MCP settings popup's build/test/publish
 * command runners. Pure state transitions; no Svelte component, no Tauri.
 *
 * The store class uses Svelte 5 `$state` runes for its field initializer;
 * the `test:state` npm script injects a shim (see _test-shim.ts) that makes
 * `$state(x)` return `x` as a plain property, so the class constructs and its
 * transition methods run. Reactivity is out of scope — we test the logic.
 *
 * Run with: `npm run test:state` (or the file directly via the command in the
 * npm script).
 */
import test from "node:test";
import assert from "node:assert/strict";

import {
  commandLogsStore,
  emptyProjectPanels,
  type PanelState,
  type ProjectPanels,
} from "./command_logs.svelte.ts";

// MAX_LOG_LINES is a private const in the store module (not exported). We
// mirror the value here so the cap test is meaningful; if the source constant
// changes, update this to match.
const MAX_LOG_LINES = 1000;

// The store is a singleton; reset its state between tests so order does not
// matter.
function reset() {
  for (const id of Object.keys(commandLogsStore.projects)) {
    delete commandLogsStore.projects[id];
  }
}

// ---------------------------------------------------------------------------
// emptyProjectPanels — the pure factory
// ---------------------------------------------------------------------------

test("emptyProjectPanels returns one panel per known key, each empty", () => {
  const panels = emptyProjectPanels();
  const keys = Object.keys(panels) as (keyof ProjectPanels)[];
  assert.deepEqual(keys.sort(), [
    "build",
    "custom",
    "publish",
    "publishDryRun",
    "sync",
    "test",
    "version",
  ]);
  for (const key of keys) {
    const p: PanelState = panels[key];
    assert.deepEqual(p.lines, []);
    assert.equal(p.running, false);
    assert.equal(p.lastExitCode, null);
  }
});

// ---------------------------------------------------------------------------
// forProject — lazy creation
// ---------------------------------------------------------------------------

test("forProject creates panels on first access", () => {
  reset();
  const panels = commandLogsStore.forProject("proj-1");
  assert.ok(panels);
  assert.deepEqual(panels.build.lines, []);
  assert.equal(panels.build.running, false);
});

test("forProject returns the same object on subsequent calls (stable identity)", () => {
  reset();
  const a = commandLogsStore.forProject("proj-2");
  const b = commandLogsStore.forProject("proj-2");
  assert.equal(a, b, "forProject must not recreate an existing project's panels");
});

test("forProject isolates projects", () => {
  reset();
  const a = commandLogsStore.forProject("proj-a");
  const b = commandLogsStore.forProject("proj-b");
  assert.notEqual(a, b);
  a.build.lines.push("from-a");
  assert.equal(b.build.lines.length, 0, "writing to proj-a must not leak into proj-b");
});

// ---------------------------------------------------------------------------
// markRunning / markExited lifecycle
// ---------------------------------------------------------------------------

test("markRunning sets running=true and clears lastExitCode", () => {
  reset();
  commandLogsStore.forProject("p");
  commandLogsStore.markRunning("p", "build");
  const panel = commandLogsStore.forProject("p").build;
  assert.equal(panel.running, true);
  assert.equal(panel.lastExitCode, null);
});

test("markExited sets running=false and records the code", () => {
  reset();
  commandLogsStore.markRunning("p", "test");
  commandLogsStore.markExited("p", "test", 0);
  const panel = commandLogsStore.forProject("p").test;
  assert.equal(panel.running, false);
  assert.equal(panel.lastExitCode, 0);
});

test("markExited records a non-zero code", () => {
  reset();
  commandLogsStore.markRunning("p", "build");
  commandLogsStore.markExited("p", "build", 127);
  assert.equal(commandLogsStore.forProject("p").build.lastExitCode, 127);
});

test("markExited accepts null (terminated without an exit code)", () => {
  reset();
  commandLogsStore.markRunning("p", "custom");
  commandLogsStore.markExited("p", "custom", null);
  assert.equal(commandLogsStore.forProject("p").custom.lastExitCode, null);
  assert.equal(commandLogsStore.forProject("p").custom.running, false);
});

// ---------------------------------------------------------------------------
// appendLine — ring-buffer cap
// ---------------------------------------------------------------------------

test("appendLine appends in order", () => {
  reset();
  commandLogsStore.appendLine("p", "build", "first");
  commandLogsStore.appendLine("p", "build", "second");
  assert.deepEqual(commandLogsStore.forProject("p").build.lines, [
    "first",
    "second",
  ]);
});

test("appendLine isolates panels within a project", () => {
  reset();
  commandLogsStore.appendLine("p", "build", "b1");
  commandLogsStore.appendLine("p", "test", "t1");
  assert.deepEqual(commandLogsStore.forProject("p").build.lines, ["b1"]);
  assert.deepEqual(commandLogsStore.forProject("p").test.lines, ["t1"]);
});

test(`appendLine caps the buffer at MAX_LOG_LINES (${MAX_LOG_LINES})`, () => {
  reset();
  // Write well past the cap; only the tail must survive.
  for (let i = 0; i < MAX_LOG_LINES + 50; i++) {
    commandLogsStore.appendLine("p", "build", `line-${i}`);
  }
  const lines = commandLogsStore.forProject("p").build.lines;
  assert.equal(lines.length, MAX_LOG_LINES);
  // The oldest entries are dropped; the buffer keeps the most recent.
  assert.equal(lines[0], `line-${50}`);
  assert.equal(lines[lines.length - 1], `line-${MAX_LOG_LINES + 49}`);
});

test("appendLine then markExited preserves the lines", () => {
  reset();
  commandLogsStore.markRunning("p", "build");
  commandLogsStore.appendLine("p", "build", "compiling...");
  commandLogsStore.markExited("p", "build", 0);
  const panel = commandLogsStore.forProject("p").build;
  assert.deepEqual(panel.lines, ["compiling..."]);
  assert.equal(panel.running, false);
});

// ---------------------------------------------------------------------------
// clear — drops lines, keeps running/exit state
// ---------------------------------------------------------------------------

test("clear empties lines but keeps running/exit state", () => {
  reset();
  commandLogsStore.markRunning("p", "build");
  commandLogsStore.appendLine("p", "build", "x");
  commandLogsStore.clear("p", "build");
  const panel = commandLogsStore.forProject("p").build;
  assert.deepEqual(panel.lines, []);
  // running stays true (the command is still in flight); clear is for the log.
  assert.equal(panel.running, true);
});

test("clear on a panel with a recorded exit keeps the exit code", () => {
  reset();
  commandLogsStore.appendLine("p", "test", "done");
  commandLogsStore.markExited("p", "test", 3);
  commandLogsStore.clear("p", "test");
  const panel = commandLogsStore.forProject("p").test;
  assert.deepEqual(panel.lines, []);
  assert.equal(panel.lastExitCode, 3);
});
