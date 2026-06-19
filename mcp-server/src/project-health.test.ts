import test from "node:test";
import assert from "node:assert/strict";

import {
  extractProjectHealthIssues,
  summarizeProjectHealth,
  MAX_HEALTH_ISSUES,
  type ProjectHealth,
} from "./project-health.js";

// ---------------------------------------------------------------------------
// extractProjectHealthIssues — assembly resolution failures
// ---------------------------------------------------------------------------

test("extractProjectHealthIssues surfaces an unresolved-assembly signature", () => {
  // Real ProBuilder-5.x-on-Unity-6 log excerpt (lightly trimmed).
  const log = [
    "Refreshing native plugins",
    "Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly: " +
      "'Unity.ProBuilder.AddOns.Editor, Version=0.0.0.0, Culture=neutral, " +
      "PublicKeyToken=null' ---> System.Exception: Failed to resolve assembly " +
      "'Unity.ProBuilder.AddOns.Editor, Version=0.0.0.0, Culture=neutral, " +
      "PublicKeyToken=null' in directories: /Applications/Unity/...",
    "  at Mono.Cecil.BaseAssemblyResolver.Resolve (...)",
  ].join("\n");

  const issues = extractProjectHealthIssues(log);
  assert.equal(issues.length, 1);
  const issue = issues[0];
  assert.equal(issue.kind, "assembly_resolution");
  assert.equal(issue.summary, "Unresolved assembly: Unity.ProBuilder.AddOns.Editor");
  assert.ok(issue.raw.includes("Mono.Cecil.AssemblyResolutionException"));
  assert.ok(issue.hint && issue.hint.includes("manifest.json"));
});

test("extractProjectHealthIssues dedupes the same assembly across repeated log entries", () => {
  // Burst prints the resolution failure twice (inner + outer exception).
  const log = [
    "Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly: 'Unity.ProBuilder.AddOns.Editor, Version=0.0.0.0'",
    " ---> System.Exception: Failed to resolve assembly 'Unity.ProBuilder.AddOns.Editor, Version=0.0.0.0'",
  ].join("\n");
  const issues = extractProjectHealthIssues(log);
  assert.equal(issues.length, 1, "deduped to one entry by summary");
});

test("extractProjectHealthIssues handles an assembly identity with no version suffix", () => {
  const log = "Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly: 'My.Bare.Assembly'";
  const issues = extractProjectHealthIssues(log);
  assert.equal(issues.length, 1);
  assert.equal(issues[0].summary, "Unresolved assembly: My.Bare.Assembly");
});

// ---------------------------------------------------------------------------
// extractProjectHealthIssues — package deprecation
// ---------------------------------------------------------------------------

test("extractProjectHealthIssues surfaces Package Manager deprecation notices", () => {
  const log =
    "[Package Manager] com.unity.ide.vscode is deprecated: Visual Studio Code " +
    "package is not supported anymore. You can continue to use it, but we " +
    "won't provide any update anymore.";
  const issues = extractProjectHealthIssues(log);
  assert.equal(issues.length, 1);
  assert.equal(issues[0].kind, "package_deprecated");
  assert.equal(issues[0].summary, "Deprecated package: com.unity.ide.vscode");
  assert.ok(issues[0].hint && issues[0].hint.includes("com.unity.ide.vscode"));
});

// ---------------------------------------------------------------------------
// extractProjectHealthIssues — generic Package Manager errors
// ---------------------------------------------------------------------------

test("extractProjectHealthIssues captures a Package Manager conflict line", () => {
  const log = "[Package Manager] Error adding package: dependency conflict on com.unity.x";
  const issues = extractProjectHealthIssues(log);
  assert.equal(issues.length, 1);
  assert.equal(issues[0].kind, "package_manager_error");
  assert.ok(issues[0].raw.includes("dependency conflict"));
});

test("extractProjectHealthIssues ignores benign Package Manager log lines", () => {
  const log = [
    "[Package Manager] Connected to IPC stream \"Upm-6217\" after 0.0 seconds.",
    "[Package Manager] Done registering packages in 0.02 seconds",
    "[Package Manager] Registered 22 packages:",
  ].join("\n");
  const issues = extractProjectHealthIssues(log);
  assert.equal(issues.length, 0);
});

// ---------------------------------------------------------------------------
// summarizeProjectHealth — aggregate verdict
// ---------------------------------------------------------------------------

test("summarizeProjectHealth reports healthy for an empty / clean log", () => {
  const health = summarizeProjectHealth("everything is fine\n");
  assert.equal(health.unhealthy, false);
  assert.equal(health.compilerErrors.length, 0);
  assert.equal(health.issues.length, 0);
  assert.equal(health.headline, "");
});

test("summarizeProjectHealth reports both compiler errors AND issues when present", () => {
  const log = [
    "Assets/Foo.cs(10,5): error CS0103: The name 'Bar' does not exist",
    "Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly: 'Unity.ProBuilder.AddOns.Editor, Version=0.0.0.0'",
  ].join("\n");
  const health = summarizeProjectHealth(log);
  assert.equal(health.unhealthy, true);
  assert.equal(health.compilerErrors.length, 1);
  assert.equal(health.issues.length, 1);
  assert.equal(health.issues[0].kind, "assembly_resolution");
  assert.ok(health.headline.includes("1 compiler error"));
  assert.ok(health.headline.includes("package/assembly"));
});

test("summarizeProjectHealth headline calls out assembly_resolution as compile-blocking", () => {
  const log =
    "Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly: 'Unity.ProBuilder.AddOns.Editor, Version=0.0.0.0'";
  const health = summarizeProjectHealth(log);
  assert.equal(health.compilerErrors.length, 0);
  assert.equal(health.issues.length, 1);
  assert.ok(
    health.headline.includes("unresolved assembly"),
    `headline should mention unresolved assembly: ${health.headline}`,
  );
});

test("summarizeProjectHealth caps the issues list at MAX_HEALTH_ISSUES", () => {
  // Build a log with more deprecation notices than the cap.
  const lines: string[] = [];
  for (let i = 0; i < MAX_HEALTH_ISSUES + 10; i++) {
    lines.push(
      `[Package Manager] com.example.pkg${i} is deprecated: no longer maintained.`,
    );
  }
  const health = summarizeProjectHealth(lines.join("\n"));
  assert.equal(health.issues.length, MAX_HEALTH_ISSUES);
  assert.equal(health.unhealthy, true);
});

// ---------------------------------------------------------------------------
// Regression: the real ProBuilder-on-Unity-6 excerpt end-to-end.
// ---------------------------------------------------------------------------

test("summarizeProjectHealth on the ProBuilder 5.x / Unity 6 log excerpt", () => {
  const log = [
    "While compiling job:",
    "Failed to find entry-points:",
    "Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly: " +
      "'Unity.ProBuilder.AddOns.Editor, Version=0.0.0.0, Culture=neutral, " +
      "PublicKeyToken=null' ---> System.Exception: Failed to resolve assembly " +
      "'Unity.ProBuilder.AddOns.Editor, Version=0.0.0.0, Culture=neutral, " +
      "PublicKeyToken=null' in directories: /Applications/Unity/...",
  ].join("\n");
  const health: ProjectHealth = summarizeProjectHealth(log);
  assert.equal(health.unhealthy, true);
  assert.equal(health.issues.length, 1);
  assert.equal(health.issues[0].kind, "assembly_resolution");
  assert.equal(
    health.issues[0].summary,
    "Unresolved assembly: Unity.ProBuilder.AddOns.Editor",
  );
  assert.ok(health.headline.length > 0);
});
