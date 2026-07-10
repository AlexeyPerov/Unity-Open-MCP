import { test } from "node:test";
import assert from "node:assert/strict";

import {
  buildTargetLabel,
  intentOptions,
  upgradeCandidatesFor,
  isNewProjectFormValid,
  isPackageFormValid,
  pipelineSupportedForVersion,
  resolveTemplate,
  formatLaunchError,
  formatAddProjectError,
  formatRelinkError,
  formatUpgradeError,
  formatNewProjectError,
  formatCreatePackageError,
  formatRemoveError,
  formatKillResult,
  formatGitStatusError,
  formatSetProjectFlagError,
  BUILD_TARGETS,
  LAUNCH_ARGS_DOCS_URL,
  LAUNCH_ARGS_EXAMPLES,
} from "./constants.ts";
import type { ProjectEntry } from "$lib/services/config.ts";

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

// ---- buildTargetLabel ----

test("buildTargetLabel returns — for null/undefined", () => {
  assert.equal(buildTargetLabel(null), "—");
  assert.equal(buildTargetLabel(undefined), "—");
});

test("buildTargetLabel maps known targets to labels", () => {
  assert.equal(buildTargetLabel("StandaloneWindows64"), "Windows");
  assert.equal(buildTargetLabel("StandaloneOSX"), "macOS");
  assert.equal(buildTargetLabel("WebGL"), "WebGL");
});

test("buildTargetLabel passes through unknown targets", () => {
  assert.equal(buildTargetLabel("SomeFutureTarget"), "SomeFutureTarget");
});

// ---- intentOptions ----

test("intentOptions returns the default list when current is empty", () => {
  const opts = intentOptions("");
  assert.equal(opts, BUILD_TARGETS);
});

test("intentOptions prepends an unknown current target", () => {
  const opts = intentOptions("MagicTarget");
  assert.equal(opts[0], "MagicTarget");
  assert.ok(opts.length > BUILD_TARGETS.length);
});

test("intentOptions does not duplicate a known target", () => {
  const opts = intentOptions("WebGL");
  assert.equal(opts, BUILD_TARGETS);
});

// ---- upgradeCandidatesFor ----

test("upgradeCandidatesFor returns versions strictly higher than the project's", () => {
  const project = makeProject({ unityVersion: "2022.3.48f1" });
  const installed = ["2022.3.40f1", "2022.3.48f1", "2022.3.50f1", "2023.1.0f1"];
  assert.deepEqual(upgradeCandidatesFor(project, installed), ["2022.3.50f1", "2023.1.0f1"]);
});

test("upgradeCandidatesFor returns empty when the project has no version", () => {
  const project = makeProject({ unityVersion: undefined });
  assert.deepEqual(upgradeCandidatesFor(project, ["2022.3.48f1"]), []);
});

// ---- isNewProjectFormValid ----

test("isNewProjectFormValid requires parent, name, and version", () => {
  assert.equal(isNewProjectFormValid("", "Name", "2022.3", "empty", "", ""), false);
  assert.equal(isNewProjectFormValid("/parent", "", "2022.3", "empty", "", ""), false);
  assert.equal(isNewProjectFormValid("/parent", "Name", "", "empty", "", ""), false);
  assert.equal(isNewProjectFormValid("/parent", "Name", "2022.3", "empty", "", ""), true);
});

test("isNewProjectFormValid requires a hub template path for hub-default", () => {
  assert.equal(isNewProjectFormValid("/parent", "Name", "2022.3", "hub-default", "", ""), false);
  assert.equal(isNewProjectFormValid("/parent", "Name", "2022.3", "hub-default", "/hub/tpl", ""), true);
});

test("isNewProjectFormValid requires a custom template path for custom", () => {
  assert.equal(isNewProjectFormValid("/parent", "Name", "2022.3", "custom", "", ""), false);
  assert.equal(isNewProjectFormValid("/parent", "Name", "2022.3", "custom", "", "/custom/tpl"), true);
});

// ---- isPackageFormValid ----

test("isPackageFormValid requires parent and a valid UPM name", () => {
  assert.equal(isPackageFormValid("", "com.author.pkg"), false);
  assert.equal(isPackageFormValid("/parent", "Invalid Name"), false);
  assert.equal(isPackageFormValid("/parent", "com.author.my-pkg"), true);
  assert.equal(isPackageFormValid("/parent", "com.author.my_pkg"), false);
});

// ---- pipelineSupportedForVersion ----

test("pipelineSupportedForVersion returns true for empty input", () => {
  assert.equal(pipelineSupportedForVersion(""), true);
});

test("pipelineSupportedForVersion returns false for pre-2019.3", () => {
  assert.equal(pipelineSupportedForVersion("2018.4"), false);
  assert.equal(pipelineSupportedForVersion("2019.2"), false);
});

test("pipelineSupportedForVersion returns true for 2019.3+", () => {
  assert.equal(pipelineSupportedForVersion("2019.3"), true);
  assert.equal(pipelineSupportedForVersion("2022.3"), true);
  assert.equal(pipelineSupportedForVersion("2023.1"), true);
});

// ---- resolveTemplate ----

test("resolveTemplate returns null for empty", () => {
  assert.equal(resolveTemplate("empty", "", ""), null);
});

test("resolveTemplate resolves hub-default", () => {
  assert.deepEqual(resolveTemplate("hub-default", "/hub/tpl", ""), { source: "hub-default", path: "/hub/tpl" });
  assert.equal(resolveTemplate("hub-default", "", ""), null);
});

test("resolveTemplate resolves custom", () => {
  assert.deepEqual(resolveTemplate("custom", "", "/custom/tpl"), { source: "custom", path: "/custom/tpl" });
  assert.equal(resolveTemplate("custom", "", ""), null);
});

// ---- error formatters ----

test("formatLaunchError formats each error type", () => {
  assert.match(formatLaunchError({ type: "projectNotFound", projectId: "x" } as never, makeProject()), /project not found/);
  assert.match(formatLaunchError({ type: "installNotFound", version: "2022.3" } as never, makeProject()), /not installed/);
  assert.match(
    formatLaunchError({ type: "alreadyRunning", pid: 1234 } as never, makeProject()),
    /already running/,
  );
});

test("formatAddProjectError formats each error type", () => {
  assert.match(formatAddProjectError({ type: "notADirectory", path: "/x" } as never), /not a directory/);
  assert.match(formatAddProjectError({ type: "duplicate", path: "/x" } as never), /already in list/);
});

test("formatRelinkError formats each error type", () => {
  assert.match(formatRelinkError({ type: "notAUnityProject", reason: "no ProjectVersion", path: "/x" } as never), /not a Unity project/);
});

test("formatUpgradeError formats each error type", () => {
  assert.match(formatUpgradeError({ type: "versionNotInstalled", version: "2023.1" } as never), /not installed/);
});

test("formatNewProjectError formats each error type", () => {
  assert.match(formatNewProjectError({ type: "nameEmpty" } as never), /cannot be empty/);
  assert.match(
    formatNewProjectError({ type: "pipelineUnsupported", pipeline: "urp", version: "2018.4" } as never),
    /not supported/,
  );
});

test("formatCreatePackageError formats each error type", () => {
  assert.match(formatCreatePackageError({ type: "invalidName", reason: "bad" } as never), /invalid package name/);
});

test("formatRemoveError formats each error type", () => {
  assert.match(formatRemoveError({ type: "projectNotFound", projectId: "x" } as never), /project not found/);
});

test("formatKillResult formats each status", () => {
  assert.match(formatKillResult({ status: "killed", pid: 1, message: "ok" }), /terminated/);
  assert.match(formatKillResult({ status: "notFound", pid: 1, message: "gone" }), /not running/);
  assert.match(formatKillResult({ status: "accessDenied", pid: 1, message: "no" }), /access denied/);
});

test("formatGitStatusError formats each error type", () => {
  assert.match(formatGitStatusError({ type: "notARepo", path: "/x" } as never), /not a git repository/);
  assert.match(formatGitStatusError({ type: "gitMissingBinary" } as never), /not installed/);
});

test("formatSetProjectFlagError prefixes the message with the action", () => {
  assert.match(
    formatSetProjectFlagError({ type: "projectNotFound", projectId: "x" } as never, "hide"),
    /^hide failed: project not found/,
  );
});

// ---- constants sanity ----

test("BUILD_TARGETS is a non-empty list", () => {
  assert.ok(BUILD_TARGETS.length > 5);
  assert.ok(BUILD_TARGETS.includes("WebGL"));
});

test("LAUNCH_ARGS_DOCS_URL is the Unity manual URL", () => {
  assert.equal(LAUNCH_ARGS_DOCS_URL, "https://docs.unity3d.com/Manual/CommandLineArguments.html");
});

test("LAUNCH_ARGS_EXAMPLES entries have args + description", () => {
  assert.ok(LAUNCH_ARGS_EXAMPLES.length >= 3);
  for (const ex of LAUNCH_ARGS_EXAMPLES) {
    assert.ok(ex.args.length > 0);
    assert.ok(ex.description.length > 0);
  }
});
