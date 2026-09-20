import { test, type TestContext } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path, { join } from "node:path";

import {
  ProjectPathError,
  UNITY_ROOT_MARKERS,
  notUnityProjectMessage,
  parseServerFlags,
  resolveProjectPath,
  validateUnityProjectRoot,
} from "./project-path.js";

const POSIX = { cwd: "/repo", pathApi: path.posix };
const WIN32 = { cwd: "C:\\repo", pathApi: path.win32 };

// --- resolution order -------------------------------------------------------

test("absolute UNITY_PROJECT_PATH is used as-is (regression: no behavior change)", () => {
  const resolved = resolveProjectPath({ ...POSIX, envPath: "/abs/MyGame" });
  assert.deepEqual(resolved, { absolute: "/abs/MyGame", source: "env" });
});

test("absolute UNITY_PROJECT_PATH loses a trailing separator (port hash input)", () => {
  const resolved = resolveProjectPath({ ...POSIX, envPath: "/abs/MyGame/" });
  assert.equal(resolved.absolute, "/abs/MyGame");
});

test("relative UNITY_PROJECT_PATH resolves against the spawn cwd", () => {
  const resolved = resolveProjectPath({ ...POSIX, envPath: "Client" });
  assert.deepEqual(resolved, { absolute: "/repo/Client", source: "env" });
});

test("--project-from-cwd resolves to the spawn cwd", () => {
  const resolved = resolveProjectPath({ ...POSIX, projectFromCwd: true });
  assert.deepEqual(resolved, { absolute: "/repo", source: "cwd" });
});

test("--project-from-cwd --unity-subpath Client resolves the monorepo layout", () => {
  const resolved = resolveProjectPath({
    ...POSIX,
    projectFromCwd: true,
    unitySubpath: "Client",
  });
  assert.deepEqual(resolved, { absolute: "/repo/Client", source: "cwd+subpath" });
});

test("nested subpath segments resolve", () => {
  const resolved = resolveProjectPath({
    ...POSIX,
    projectFromCwd: true,
    unitySubpath: "games/Client",
  });
  assert.equal(resolved.absolute, "/repo/games/Client");
});

test("UNITY_PROJECT_PATH wins over --project-from-cwd (documented order)", () => {
  const resolved = resolveProjectPath({
    ...POSIX,
    envPath: "/abs/Other",
    projectFromCwd: true,
    unitySubpath: "Client",
  });
  assert.deepEqual(resolved, { absolute: "/abs/Other", source: "env" });
});

test("--project wins over UNITY_PROJECT_PATH", () => {
  const resolved = resolveProjectPath({
    ...POSIX,
    flagPath: "Client",
    envPath: "/abs/Other",
  });
  assert.deepEqual(resolved, { absolute: "/repo/Client", source: "flag" });
});

test("blank env values are ignored, not treated as a cwd-relative path", () => {
  const resolved = resolveProjectPath({
    ...POSIX,
    envPath: "   ",
    projectFromCwd: true,
  });
  assert.deepEqual(resolved, { absolute: "/repo", source: "cwd" });
});

// --- Windows fixtures -------------------------------------------------------

test("Windows: absolute env path is preserved", () => {
  const resolved = resolveProjectPath({ ...WIN32, envPath: "D:\\Games\\MyGame" });
  assert.deepEqual(resolved, { absolute: "D:\\Games\\MyGame", source: "env" });
});

test("Windows: --project-from-cwd --unity-subpath Client joins with backslashes", () => {
  const resolved = resolveProjectPath({
    ...WIN32,
    projectFromCwd: true,
    unitySubpath: "Client",
  });
  assert.deepEqual(resolved, { absolute: "C:\\repo\\Client", source: "cwd+subpath" });
});

test("Windows: forward-slash subpath normalizes to the platform separator", () => {
  const resolved = resolveProjectPath({
    ...WIN32,
    projectFromCwd: true,
    unitySubpath: "games/Client",
  });
  assert.equal(resolved.absolute, "C:\\repo\\games\\Client");
});

// --- errors -----------------------------------------------------------------

test("no input at all throws an actionable error", () => {
  assert.throws(
    () => resolveProjectPath(POSIX),
    (err: unknown) => {
      assert.ok(err instanceof ProjectPathError);
      assert.equal(err.code, "missing_project_path");
      assert.match(err.message, /--project-from-cwd/);
      return true;
    },
  );
});

test("--unity-subpath without --project-from-cwd throws", () => {
  assert.throws(
    () => resolveProjectPath({ ...POSIX, unitySubpath: "Client" }),
    (err: unknown) => {
      assert.ok(err instanceof ProjectPathError);
      assert.equal(err.code, "subpath_without_cwd");
      return true;
    },
  );
});

test("an absolute --unity-subpath is rejected", () => {
  assert.throws(
    () =>
      resolveProjectPath({
        ...POSIX,
        projectFromCwd: true,
        unitySubpath: "/abs/Client",
      }),
    (err: unknown) => {
      assert.ok(err instanceof ProjectPathError);
      assert.equal(err.code, "subpath_not_relative");
      return true;
    },
  );
});

// --- Unity root validation --------------------------------------------------

async function unityTree(t: TestContext, markers: readonly string[]): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "unity-open-mcp-project-path-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  for (const marker of markers) await mkdir(join(root, marker), { recursive: true });
  return root;
}

test("a complete Unity tree validates", async (t) => {
  const root = await unityTree(t, UNITY_ROOT_MARKERS);
  assert.deepEqual(validateUnityProjectRoot(root), { valid: true, missing: [] });
});

test("a non-Unity path reports every missing marker", async (t) => {
  const root = await unityTree(t, []);
  const validation = validateUnityProjectRoot(root);
  assert.equal(validation.valid, false);
  assert.deepEqual(validation.missing, ["Assets", "Packages", "ProjectSettings"]);
  const message = notUnityProjectMessage(
    { absolute: root, source: "cwd" },
    validation,
  );
  assert.match(message, /is not a Unity project root \(from cwd\)/);
  assert.match(message, /Assets\//);
});

test("a workspace root whose Unity project is in a subfolder does not validate", async (t) => {
  const root = await unityTree(t, []);
  for (const marker of UNITY_ROOT_MARKERS) {
    await mkdir(join(root, "Client", marker), { recursive: true });
  }
  assert.equal(validateUnityProjectRoot(root).valid, false);
  assert.equal(validateUnityProjectRoot(join(root, "Client")).valid, true);
});

test("a file named like a marker directory does not count", async (t) => {
  const root = await unityTree(t, ["Packages", "ProjectSettings"]);
  await writeFile(join(root, "Assets"), "not a directory");
  assert.deepEqual(validateUnityProjectRoot(root).missing, ["Assets"]);
});

// --- stdio flag pre-parse ---------------------------------------------------

test("parseServerFlags reads --project-from-cwd and --unity-subpath", () => {
  assert.deepEqual(parseServerFlags(["--project-from-cwd", "--unity-subpath", "Client"]), {
    projectFromCwd: true,
    unitySubpath: "Client",
    error: undefined,
  });
});

test("parseServerFlags accepts the --unity-subpath=Client form", () => {
  const flags = parseServerFlags(["--project-from-cwd", "--unity-subpath=Client"]);
  assert.equal(flags.unitySubpath, "Client");
  assert.equal(flags.error, undefined);
});

test("parseServerFlags reports a missing --unity-subpath value", () => {
  const flags = parseServerFlags(["--unity-subpath"]);
  assert.match(String(flags.error), /requires a relative path/);
});

test("parseServerFlags ignores unrelated argv tokens", () => {
  assert.deepEqual(parseServerFlags(["-y", "unity-open-mcp@1.2.3"]), {
    projectFromCwd: false,
    unitySubpath: undefined,
    error: undefined,
  });
});
