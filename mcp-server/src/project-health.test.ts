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
// extractProjectHealthIssues — Editor file-descriptor exhaustion (Bee hang)
// ---------------------------------------------------------------------------

test("extractProjectHealthIssues surfaces the Bee fd-exhaustion signature", () => {
  // Real log excerpt from a hung Unity demo project after heavy MCP use.
  const log = [
    "Unhandled exception during build: System.NotSupportedException: Could not register to wait for file descriptor 1194",
    "  at (wrapper managed-to-native) System.IOSelector.Add(intptr,System.IOSelectorJob)",
    "  at System.Net.Sockets.Socket.QueueIOSelectorJob (System.Threading.SemaphoreSlim sem, System.IntPtr handle, System.IOSelectorJob job) [0x0003e] in <fe01db4ec71d4ebfa6ce042aa72c9c03>:0",
    "  at System.Net.Sockets.Socket.BeginAccept (System.AsyncCallback callback, System.Object state) [0x0003f] in <fe01db4ec71d4ebfa6ce042aa72c9c03>:0",
    "  at System.IO.Pipes.NamedPipeServerStream.<WaitForConnectionAsync>g__WaitForConnectionAsyncCore|8_0 () [0x0001c] in <cb951819652d428c85bd75cb224d8506>:0",
    "  at Bee.BeeDriver.BeeDriver_RunBackend.ReadEntireBinlogFromIpcAsync (Bee.BeeDriver.InternalState state, Bee.BeeDriver.RunningProgram runningBackendProgram, IPCConnection ipcConnection, System.Action`1[T] nodeFinishedCallback, System.Threading.Tasks.Task writePipeConnectionTask) [0x0022f] in /Users/bokken/build/output/unity/unity/Tools/Bee/Bee.BeeDriver2/BeeDriver_RunBackend.cs:189",
    "  at Bee.BeeDriver.BeeDriver_RunBackend.RunBackend (Bee.BeeDriver.InternalState state, NiceIO.NPath newDagJsonFile, System.Action`1[T] nodeFinishedCallback, Bee.Core.Track mainAsyncTrack) [0x0022a] in /Users/bokken/build/output/unity/unity/Tools/Bee/Bee.BeeDriver2/BeeDriver_RunBackend.cs:49",
    "  at Bee.BeeDriver.BeeDriver.EntryPoint (Bee.BeeDriver.InternalState state) [0x00174] in /Users/bokken/build/output/unity/unity/Tools/Bee/Bee.BeeDriver2/BeeDriver.cs:149",
  ].join("\n");
  const issues = extractProjectHealthIssues(log);
  assert.equal(issues.length, 1, "one issue for the whole stack");
  const issue = issues[0];
  assert.equal(issue.kind, "editor_fd_exhaustion");
  assert.ok(
    issue.raw.includes("Could not register to wait for file descriptor"),
    `raw should carry the signature: ${issue.raw}`,
  );
  // The hint must steer the agent away from a phantom C# fix.
  assert.ok(issue.hint && issue.hint.includes("NOT a C# compile error"));
  assert.ok(issue.hint && issue.hint.toLowerCase().includes("restart"));
});

test("extractProjectHealthIssues dedupes repeated fd-exhaustion stacks to one issue", () => {
  // Unity can emit the same exception multiple times across build retries.
  const log = [
    "Unhandled exception during build: System.NotSupportedException: Could not register to wait for file descriptor 1194",
    "  at System.IOSelector.Add(...)",
    "Unhandled exception during build: System.NotSupportedException: Could not register to wait for file descriptor 1272",
    "  at System.IOSelector.Add(...)",
  ].join("\n");
  const issues = extractProjectHealthIssues(log);
  assert.equal(issues.length, 1, "deduped to one entry by summary");
  assert.equal(issues[0].kind, "editor_fd_exhaustion");
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

// ---------------------------------------------------------------------------
// summarizeProjectHealth — Editor fd-exhaustion headline
// ---------------------------------------------------------------------------

test("summarizeProjectHealth flags fd-exhaustion as a restart, not a code fix", () => {
  const log =
    "Unhandled exception during build: System.NotSupportedException: Could not register to wait for file descriptor 1194\n" +
    "  at System.IOSelector.Add(intptr,System.IOSelectorJob)\n" +
    "  at Bee.BeeDriver.BeeDriver_RunBackend.ReadEntireBinlogFromIpcAsync (...)\n";
  const health = summarizeProjectHealth(log);
  assert.equal(health.unhealthy, true);
  assert.equal(health.compilerErrors.length, 0, "no C# errors accompany it");
  assert.equal(health.issues.length, 1);
  assert.equal(health.issues[0].kind, "editor_fd_exhaustion");
  // Headline must NOT send the agent to fix a compile error.
  assert.ok(
    health.headline.includes("file-descriptor exhaustion"),
    `headline should name fd-exhaustion: ${health.headline}`,
  );
  assert.ok(
    health.headline.includes("NOT a compile error") ||
      health.headline.includes("NOT a C#"),
    `headline should rule out a code fix: ${health.headline}`,
  );
  assert.ok(
    health.headline.toLowerCase().includes("restart"),
    `headline should call for a restart: ${health.headline}`,
  );
});

test("summarizeProjectHealth prefers the fd-exhaustion headline over milder package notices", () => {
  // fd-exhaustion is the actionable signal even when package notices are also
  // present — the restart guidance must not be masked.
  const log = [
    "[Package Manager] com.unity.ide.vscode is deprecated: no longer maintained.",
    "Unhandled exception during build: System.NotSupportedException: Could not register to wait for file descriptor 1194",
    "  at System.IOSelector.Add(...)",
  ].join("\n");
  const health = summarizeProjectHealth(log);
  assert.equal(health.issues.length, 2);
  assert.ok(
    health.headline.includes("file-descriptor exhaustion"),
    `fd-exhaustion must win the headline: ${health.headline}`,
  );
});

test("summarizeProjectHealth reports both a CSxxxx error and fd-exhaustion when both are present", () => {
  // An fd-exhausted Editor could still have unrelated prior compile errors in
  // the log tail — surface both so the operator can fix the code on restart.
  const log = [
    "Assets/Foo.cs(10,5): error CS0103: The name 'Bar' does not exist",
    "Unhandled exception during build: System.NotSupportedException: Could not register to wait for file descriptor 1194",
    "  at System.IOSelector.Add(...)",
  ].join("\n");
  const health = summarizeProjectHealth(log);
  assert.equal(health.compilerErrors.length, 1);
  assert.equal(health.issues.length, 1);
  assert.equal(health.issues[0].kind, "editor_fd_exhaustion");
  assert.ok(health.headline.includes("1 compiler error"));
  assert.ok(health.headline.includes("package/assembly issue"));
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
