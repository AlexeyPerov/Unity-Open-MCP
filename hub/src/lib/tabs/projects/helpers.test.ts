import { test } from "node:test";
import assert from "node:assert/strict";

import {
  projectKindOf,
  kindLabel,
  statusFor,
  formatSize,
  previewBundleFor,
  validateArgs,
  isValidEnvVarDraft,
  type EnvVarDraft,
} from "./helpers.ts";
import type { BundleStrategy, ProjectEntry, ProjectKind } from "$lib/services/config.ts";

function makeProject(overrides: Partial<ProjectEntry> = {}): ProjectEntry {
  return {
    id: "p1",
    name: "TestProject",
    path: "/home/user/TestProject",
    kind: "unity",
    unityVersion: "2022.3.48f1",
    ...overrides,
  } as ProjectEntry;
}

// ---- projectKindOf ----

test("projectKindOf returns the stored kind when multi-type is enabled", () => {
  assert.equal(projectKindOf(makeProject({ kind: "package" }), true), "package");
  assert.equal(projectKindOf(makeProject({ kind: "openMcp" }), true), "openMcp");
  assert.equal(projectKindOf(makeProject({ kind: "custom" }), true), "custom");
});

test("projectKindOf defaults to unity for legacy entries (no kind field)", () => {
  const legacy = makeProject();
  delete (legacy as { kind?: ProjectKind }).kind;
  assert.equal(projectKindOf(legacy, true), "unity");
});

test("projectKindOf forces unity when multi-type is disabled", () => {
  assert.equal(projectKindOf(makeProject({ kind: "package" }), false), "unity");
  assert.equal(projectKindOf(makeProject({ kind: "custom" }), false), "unity");
});

// ---- kindLabel ----

test("kindLabel returns a short label for every kind", () => {
  assert.equal(kindLabel("unity"), "Unity");
  assert.equal(kindLabel("package"), "Package");
  assert.equal(kindLabel("openMcp"), "Open-MCP");
  assert.equal(kindLabel("custom"), "Custom");
});

// ---- statusFor ----

test("statusFor returns loading when path existence is unknown", () => {
  const s = statusFor({
    project: makeProject(),
    pathExists: undefined,
    running: false,
    kind: "unity",
  });
  assert.equal(s.kind, "loading");
  assert.equal(s.pathExists, null);
  assert.equal(s.launchable, false);
});

test("statusFor returns ok + launchable for a healthy Unity project", () => {
  const s = statusFor({
    project: makeProject(),
    pathExists: true,
    running: false,
    kind: "unity",
  });
  assert.equal(s.kind, "ok");
  assert.equal(s.launchable, true);
  assert.equal(s.running, false);
});

test("statusFor returns running for a Unity project whose editor is alive", () => {
  const s = statusFor({
    project: makeProject(),
    pathExists: true,
    running: true,
    kind: "unity",
  });
  assert.equal(s.kind, "running");
  assert.equal(s.launchable, true);
  assert.equal(s.running, true);
});

test("statusFor returns missingPath when the folder is gone", () => {
  const s = statusFor({
    project: makeProject(),
    pathExists: false,
    running: false,
    kind: "unity",
  });
  assert.equal(s.kind, "missingPath");
  assert.equal(s.launchable, false);
});

test("statusFor returns missingVersion when version is empty", () => {
  const s = statusFor({
    project: makeProject({ unityVersion: "" }),
    pathExists: true,
    running: false,
    kind: "unity",
  });
  assert.equal(s.kind, "missingVersion");
  assert.equal(s.hasVersion, false);
});

test("statusFor returns stale for a stale Unity row", () => {
  const s = statusFor({
    project: makeProject({ stale: true }),
    pathExists: true,
    running: false,
    kind: "unity",
  });
  assert.equal(s.kind, "stale");
  assert.equal(s.launchable, false);
});

test("statusFor for a non-Unity project is never launchable", () => {
  const ok = statusFor({
    project: makeProject({ kind: "package" }),
    pathExists: true,
    running: false,
    kind: "package",
  });
  assert.equal(ok.kind, "ok");
  assert.equal(ok.launchable, false);

  const missing = statusFor({
    project: makeProject({ kind: "package" }),
    pathExists: false,
    running: false,
    kind: "package",
  });
  assert.equal(missing.kind, "missingPath");
  assert.equal(missing.launchable, false);
});

test("statusFor keeps non-Unity entries out of the missing-version filter", () => {
  const s = statusFor({
    project: makeProject({ kind: "package", unityVersion: "" }),
    pathExists: true,
    running: false,
    kind: "package",
  });
  assert.equal(s.hasVersion, true);
});

// ---- formatSize ----

test("formatSize returns — for zero bytes", () => {
  assert.equal(formatSize(0), "—");
});

test("formatSize formats bytes / KB / MB / GB", () => {
  assert.equal(formatSize(512), "512 B");
  assert.equal(formatSize(2048), "2.0 KB");
  assert.equal(formatSize(1048576), "1.0 MB");
  assert.equal(formatSize(1073741824), "1.0 GB");
});

// ---- previewBundleFor ----

test("previewBundleFor leaves the version untouched for strategy none", () => {
  assert.deepEqual(previewBundleFor("1.2.3", "none"), { previous: "1.2.3", next: "1.2.3" });
});

test("previewBundleFor bumps patch / minor / major", () => {
  assert.deepEqual(previewBundleFor("1.2.3", "patch"), { previous: "1.2.3", next: "1.2.4" });
  assert.deepEqual(previewBundleFor("1.2.3", "minor"), { previous: "1.2.3", next: "1.3.0" });
  assert.deepEqual(previewBundleFor("1.2.3", "major"), { previous: "1.2.3", next: "2.0.0" });
});

test("previewBundleFor defaults empty input to 0.0.0", () => {
  assert.deepEqual(previewBundleFor("", "patch"), { previous: "0.0.0", next: "0.0.1" });
});

test("previewBundleFor passes through non-semver strings unchanged", () => {
  assert.deepEqual(previewBundleFor("2022.3", "patch"), { previous: "2022.3", next: "2022.3" });
});

// ---- validateArgs ----

test("validateArgs returns null for a clean string", () => {
  assert.equal(validateArgs("-batchmode -nographics"), null);
});

test("validateArgs flags unsafe characters", () => {
  const pipe = validateArgs("foo | bar");
  assert.ok(pipe && pipe.includes("|"));
  const semi = validateArgs("foo;bar");
  assert.ok(semi && semi.includes(";"));
  const backtick = validateArgs("foo`bar");
  assert.ok(backtick && backtick.includes("`"));
});

// ---- isValidEnvVarDraft ----

function draft(uid: string, key: string, value = ""): EnvVarDraft {
  return { uid, key, value };
}

test("isValidEnvVarDraft accepts a valid set of rows", () => {
  const result = isValidEnvVarDraft([draft("1", "FOO", "bar"), draft("2", "BAZ")]);
  assert.equal(result.ok, true);
  if (result.ok) {
    assert.deepEqual(result.map, { FOO: "bar", BAZ: "" });
  }
});

test("isValidEnvVarDraft rejects empty keys", () => {
  const result = isValidEnvVarDraft([draft("1", "  ")]);
  assert.equal(result.ok, false);
  if (!result.ok) assert.match(result.error, /empty/);
});

test("isValidEnvVarDraft rejects keys containing =", () => {
  const result = isValidEnvVarDraft([draft("1", "FOO=BAR")]);
  assert.equal(result.ok, false);
  if (!result.ok) assert.match(result.error, /=/);
});

test("isValidEnvVarDraft rejects duplicate keys", () => {
  const result = isValidEnvVarDraft([draft("1", "FOO"), draft("2", "FOO")]);
  assert.equal(result.ok, false);
  if (!result.ok) assert.match(result.error, /duplicate/);
});
