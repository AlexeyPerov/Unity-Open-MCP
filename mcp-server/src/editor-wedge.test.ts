// specs/feedback.md 2026-08-14 — wedge detection shared by bridge_status and
// the execute_csharp stale-domain guard.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  scanForFdExhaustion,
  EditorWedgeCache,
  DEFAULT_WEDGE_SCAN_TTL_MS,
} from "./editor-wedge.js";

/** The Bee build-driver hang signature, as Unity writes it. */
const FD_EXHAUSTION_LOG =
  "Refreshing native plugins compatible for Editor in 1.83 ms\n" +
  "Unhandled exception during build: System.NotSupportedException: Could not " +
  "register to wait for file descriptor 1194\n" +
  "  at System.IOSelector.Add (System.IntPtr handle, System.IOSelectorJob job)\n";

async function makeProject(logContent: string | null): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "uomcp-wedge-"));
  await mkdir(join(root, "Assets"), { recursive: true });
  if (logContent !== null) {
    await mkdir(join(root, "Logs"), { recursive: true });
    await writeFile(join(root, "Logs", "Editor.log"), logContent);
  }
  return root;
}

// A global-log path that never exists, so the resolver cannot wander onto the
// host machine's real ~/Library/Logs/Unity/Editor.log (same seam the router
// tests use).
const NO_GLOBAL_LOG = "__test_no_global_log__/Editor.log";

test("scanForFdExhaustion finds the signature in the project-relative log", async () => {
  const root = await makeProject(FD_EXHAUSTION_LOG);
  try {
    const scan = scanForFdExhaustion(root, undefined, NO_GLOBAL_LOG);
    assert.equal(scan.present, true);
    assert.equal(scan.logPath, join(root, "Logs", "Editor.log"));
    assert.ok(scan.raw?.includes("Could not register to wait for file descriptor"));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("scanForFdExhaustion is clean for a healthy log", async () => {
  const root = await makeProject("Refreshing native plugins.\n- Finished compile\n");
  try {
    const scan = scanForFdExhaustion(root, undefined, NO_GLOBAL_LOG);
    assert.equal(scan.present, false);
    assert.equal(scan.raw, null);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("scanForFdExhaustion fails open when there is no log at all", async () => {
  const root = await makeProject(null);
  try {
    const scan = scanForFdExhaustion(root, undefined, NO_GLOBAL_LOG);
    assert.equal(scan.present, false, "a missing log is not evidence of a wedge");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("scanForFdExhaustion fails open with no project path", () => {
  assert.equal(scanForFdExhaustion(null).present, false);
  assert.equal(scanForFdExhaustion(undefined).present, false);
});

test("scanForFdExhaustion ignores a prev_log_fallback log (provenance guard)", async () => {
  // The resolver's last resort reads the GLOBAL Editor-prev.log when the log
  // it wanted does not exist — with nothing tying that file to this project.
  // Fine for "show me whatever errors you can find", NOT fine for condemning a
  // live editor as wedged: an unrelated project's rotated log would start
  // failing this project's execute_csharp calls with editor_build_wedged.
  const root = await makeProject(null);
  const fakeHome = await mkdtemp(join(tmpdir(), "uomcp-wedge-home-"));
  const prevHome = process.env.HOME;
  const prevUserProfile = process.env.USERPROFILE;
  process.env.HOME = fakeHome;
  process.env.USERPROFILE = fakeHome;
  try {
    // A global Editor-prev.log carrying the signature, and no Editor.log — the
    // exact shape that triggers prev_log_fallback.
    const logsDir =
      process.platform === "darwin"
        ? join(fakeHome, "Library", "Logs", "Unity")
        : process.platform === "win32"
          ? join(fakeHome, "AppData", "Local", "Unity", "Editor")
          : join(fakeHome, ".config", "unity3d");
    await mkdir(logsDir, { recursive: true });
    await writeFile(join(logsDir, "Editor-prev.log"), FD_EXHAUSTION_LOG);

    const scan = scanForFdExhaustion(root);
    assert.equal(
      scan.present,
      false,
      "a bare global prev-log fallback must not be read as this editor's wedge",
    );
  } finally {
    if (prevHome === undefined) delete process.env.HOME;
    else process.env.HOME = prevHome;
    if (prevUserProfile === undefined) delete process.env.USERPROFILE;
    else process.env.USERPROFILE = prevUserProfile;
    await rm(fakeHome, { recursive: true, force: true });
    await rm(root, { recursive: true, force: true });
  }
});

test("EditorWedgeCache returns a recorded result inside the TTL", () => {
  const cache = new EditorWedgeCache();
  assert.equal(cache.get(), null, "empty cache misses");
  cache.record({ present: true, logPath: "/x/Editor.log", raw: "boom" });
  const hit = cache.get(DEFAULT_WEDGE_SCAN_TTL_MS);
  assert.ok(hit);
  assert.equal(hit!.present, true);
});

test("EditorWedgeCache honours the 0-disables contract and invalidate()", () => {
  const cache = new EditorWedgeCache();
  cache.record({ present: true, logPath: null, raw: null });
  assert.equal(cache.get(0), null, "a 0 TTL disables the cache");
  assert.ok(cache.get(DEFAULT_WEDGE_SCAN_TTL_MS));
  cache.invalidate();
  assert.equal(cache.get(DEFAULT_WEDGE_SCAN_TTL_MS), null);
});
