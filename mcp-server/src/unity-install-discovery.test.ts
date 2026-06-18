// Tests for Unity-install auto-discovery. Uses temp-dir fixtures so behavior
// is deterministic across machines (no dependence on a real Unity install).
import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  compareUnityVersions,
  defaultHubRoots,
  discoverUnityInstalls,
  executableForInstall,
  resolveUnityPath,
  scannedHubRoots,
  versionMatches,
} from "./unity-install-discovery.js";

/**
 * Build a fake Unity install directory tree under `root/<version>/...` that
 * the per-OS `executableForInstall` will accept. Creates the platform-
 * appropriate executable path with a placeholder file so `existsSync` passes.
 */
function fakeInstall(root: string, version: string): string {
  const installDir = join(root, version);
  mkdirSync(installDir, { recursive: true });
  if (process.platform === "darwin") {
    const exe = join(installDir, "Unity.app", "Contents", "MacOS", "Unity");
    mkdirSync(join(installDir, "Unity.app", "Contents", "MacOS"), { recursive: true });
    writeFileSync(exe, "#!/bin/sh\n# fake unity\n");
    return exe;
  }
  if (process.platform === "win32") {
    const exe = join(installDir, "Editor", "Unity.exe");
    mkdirSync(join(installDir, "Editor"), { recursive: true });
    writeFileSync(exe, "fake");
    return exe;
  }
  // Linux.
  const exe = join(installDir, "Editor", "Unity");
  mkdirSync(join(installDir, "Editor"), { recursive: true });
  writeFileSync(exe, "#!/bin/sh\n# fake unity\n");
  return exe;
}

function withTempRoot<T>(fn: (root: string) => T): T {
  const root = mkdtempSync(join(tmpdir(), "unity-disc-test-"));
  try {
    return fn(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

// ---------------------------------------------------------------------------
// compareUnityVersions
// ---------------------------------------------------------------------------

test("compareUnityVersions: higher patch wins", () => {
  assert.ok(compareUnityVersions("6000.4.1f1", "6000.4.0f1") > 0);
  assert.ok(compareUnityVersions("6000.4.0f1", "6000.4.1f1") < 0);
});

test("compareUnityVersions: higher minor wins over patch", () => {
  assert.ok(compareUnityVersions("6000.5.0f1", "6000.4.9f1") > 0);
});

test("compareUnityVersions: equal versions return 0", () => {
  assert.equal(compareUnityVersions("2022.3.62f2", "2022.3.62f2"), 0);
});

test("compareUnityVersions: Unity 6 sorts above 2022 LTS", () => {
  assert.ok(compareUnityVersions("6000.4.0f1", "2022.3.62f2") > 0);
});

// ---------------------------------------------------------------------------
// versionMatches
// ---------------------------------------------------------------------------

test("versionMatches: exact match", () => {
  assert.ok(versionMatches("6000.4.0f1", "6000.4.0f1"));
});

test("versionMatches: prefix match within same minor line", () => {
  assert.ok(versionMatches("6000.4.5f1", "6000.4.0f1"));
  assert.ok(versionMatches("2022.3.99f1", "2022.3.62f2"));
});

test("versionMatches: different minor line does not match", () => {
  assert.ok(!versionMatches("6000.5.0f1", "6000.4.0f1"));
});

test("versionMatches: null/empty project version matches nothing", () => {
  assert.ok(!versionMatches("6000.4.0f1", null));
  assert.ok(!versionMatches("6000.4.0f1", undefined));
  assert.ok(!versionMatches("6000.4.0f1", ""));
});

// ---------------------------------------------------------------------------
// executableForInstall
// ---------------------------------------------------------------------------

test("executableForInstall: returns null when binary absent", () => {
  withTempRoot((root) => {
    const empty = join(root, "6000.0.0f1");
    mkdirSync(empty, { recursive: true });
    assert.equal(executableForInstall(empty), null);
  });
});

test("executableForInstall: returns path when binary present", () => {
  withTempRoot((root) => {
    const exe = fakeInstall(root, "6000.0.0f1");
    const installDir = join(root, "6000.0.0f1");
    assert.equal(executableForInstall(installDir), exe);
  });
});

// ---------------------------------------------------------------------------
// discoverUnityInstalls (with explicit roots override)
// ---------------------------------------------------------------------------

test("discoverUnityInstalls: empty roots returns empty", () => {
  assert.deepEqual(discoverUnityInstalls([]), []);
});

test("discoverUnityInstalls: skips folders without the editor binary", () => {
  withTempRoot((root) => {
    fakeInstall(root, "6000.4.0f1");
    // A non-install folder (no binary) — must be skipped.
    mkdirSync(join(root, "not-an-install"), { recursive: true });
    const installs = discoverUnityInstalls([root]);
    assert.equal(installs.length, 1);
    assert.equal(installs[0].version, "6000.4.0f1");
  });
});

test("discoverUnityInstalls: sorts newest-first", () => {
  withTempRoot((root) => {
    fakeInstall(root, "2022.3.62f2");
    fakeInstall(root, "6000.4.0f1");
    const installs = discoverUnityInstalls([root]);
    assert.equal(installs.length, 2);
    assert.equal(installs[0].version, "6000.4.0f1");
    assert.equal(installs[1].version, "2022.3.62f2");
  });
});

test("discoverUnityInstalls: dedupes the same install across roots", () => {
  withTempRoot((root) => {
    fakeInstall(root, "6000.4.0f1");
    // Pass the same root twice — should not double-list.
    const installs = discoverUnityInstalls([root, root]);
    assert.equal(installs.length, 1);
  });
});

test("discoverUnityInstalls: silently skips missing/unreadable roots", () => {
  withTempRoot((root) => {
    fakeInstall(root, "6000.4.0f1");
    const installs = discoverUnityInstalls([root, "/definitely/does/not/exist/xyz"]);
    assert.equal(installs.length, 1);
  });
});

// ---------------------------------------------------------------------------
// resolveUnityPath
// ---------------------------------------------------------------------------

test("resolveUnityPath: UNITY_PATH env wins when it points at a file", () => {
  withTempRoot((root) => {
    const fakeExe = join(root, "my-unity");
    writeFileSync(fakeExe, "fake");
    const saved = process.env.UNITY_PATH;
    process.env.UNITY_PATH = fakeExe;
    try {
      // Even with no discovery roots, env path should win.
      const resolved = resolveUnityPath(undefined, []);
      assert.ok(resolved);
      assert.equal(resolved!.source, "env");
      assert.equal(resolved!.path, fakeExe);
    } finally {
      if (saved === undefined) delete process.env.UNITY_PATH;
      else process.env.UNITY_PATH = saved;
    }
  });
});

test("resolveUnityPath: env override is NOT required — discovery fills in", () => {
  const saved = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    withTempRoot((root) => {
      fakeInstall(root, "6000.4.0f1");
      const resolved = resolveUnityPath(undefined, [root]);
      assert.ok(resolved);
      assert.equal(resolved!.source, "discovered");
      assert.equal(resolved!.version, "6000.4.0f1");
    });
  } finally {
    if (saved === undefined) delete process.env.UNITY_PATH;
    else process.env.UNITY_PATH = saved;
  }
});

test("resolveUnityPath: preferredVersion pins the right minor line", () => {
  const saved = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    withTempRoot((root) => {
      fakeInstall(root, "6000.4.0f1");
      fakeInstall(root, "2022.3.62f2");
      // Project runs 2022.3.62f2 — discovery should pick 2022.3 not the
      // newer 6000.4, even though 6000.4 sorts first.
      const resolved = resolveUnityPath("2022.3.62f2", [root]);
      assert.ok(resolved);
      assert.equal(resolved!.version, "2022.3.62f2");
    });
  } finally {
    if (saved === undefined) delete process.env.UNITY_PATH;
    else process.env.UNITY_PATH = saved;
  }
});

test("resolveUnityPath: returns null when env unset AND discovery empty", () => {
  const saved = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    assert.equal(resolveUnityPath(undefined, []), null);
  } finally {
    if (saved === undefined) delete process.env.UNITY_PATH;
    else process.env.UNITY_PATH = saved;
  }
});

test("resolveUnityPath: falls back to newest when preferredVersion not found", () => {
  const saved = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    withTempRoot((root) => {
      fakeInstall(root, "2022.3.62f2");
      fakeInstall(root, "6000.4.0f1");
      const resolved = resolveUnityPath("9999.0.0f1", [root]); // no such version
      assert.ok(resolved);
      assert.equal(resolved!.version, "6000.4.0f1"); // newest
    });
  } finally {
    if (saved === undefined) delete process.env.UNITY_PATH;
    else process.env.UNITY_PATH = saved;
  }
});

// ---------------------------------------------------------------------------
// defaultHubRoots / scannedHubRoots (smoke — real machine state)
// ---------------------------------------------------------------------------

test("defaultHubRoots: returns at least one root per OS", () => {
  const roots = defaultHubRoots();
  assert.ok(roots.length >= 1);
  // macOS asserts a well-known path.
  if (process.platform === "darwin") {
    assert.ok(roots.includes("/Applications/Unity/Hub/Editor"));
  }
});

test("scannedHubRoots: filters to existing directories only", () => {
  const roots = scannedHubRoots();
  for (const r of roots) {
    assert.ok(existsSync(r), `scanned root should exist: ${r}`);
  }
});

test("scannedHubRoots: honors UNITY_HUB env override when its dir exists", () => {
  withTempRoot((root) => {
    const saved = process.env.UNITY_HUB;
    process.env.UNITY_HUB = root;
    try {
      const roots = scannedHubRoots();
      assert.ok(roots.includes(root), "UNITY_HUB override should be scanned");
    } finally {
      if (saved === undefined) delete process.env.UNITY_HUB;
      else process.env.UNITY_HUB = saved;
    }
  });
});
