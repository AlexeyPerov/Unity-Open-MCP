import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, openSync, closeSync, utimesSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  editorLogsDir,
  editorLogPath,
  projectEditorLogPath,
  resolveEditorLogPath,
  readLogTail,
  detectStaleLog,
  DEFAULT_LOG_TAIL_BYTES,
} from "./unity-log.js";

// ---------------------------------------------------------------------------
// editorLogsDir / editorLogPath — platform branches
// ---------------------------------------------------------------------------

test("editorLogsDir resolves the macOS Unity logs folder", () => {
  const dir = editorLogsDir("darwin");
  assert.ok(dir.endsWith(join("Library", "Logs", "Unity")), dir);
});

test("editorLogsDir resolves the Windows Unity Editor folder via LOCALAPPDATA", () => {
  const saved = process.env.LOCALAPPDATA;
  process.env.LOCALAPPDATA = "C:\\Users\\test\\AppData\\Local";
  try {
    const dir = editorLogsDir("win32");
    assert.equal(
      dir,
      join("C:\\Users\\test\\AppData\\Local", "Unity", "Editor"),
    );
  } finally {
    process.env.LOCALAPPDATA = saved;
  }
});

test("editorLogsDir resolves the Linux unity3d folder via XDG_CONFIG_HOME", () => {
  const saved = process.env.XDG_CONFIG_HOME;
  process.env.XDG_CONFIG_HOME = "/home/test/.config";
  try {
    const dir = editorLogsDir("linux");
    assert.equal(dir, join("/home/test/.config", "unity3d"));
  } finally {
    delete process.env.XDG_CONFIG_HOME;
    if (saved !== undefined) process.env.XDG_CONFIG_HOME = saved;
  }
});

test("editorLogPath appends Editor.log to the resolved dir", () => {
  assert.ok(editorLogPath("darwin").endsWith("Editor.log"));
  assert.ok(editorLogPath("darwin").endsWith(join("Unity", "Editor.log")));
});

// ---------------------------------------------------------------------------
// projectEditorLogPath / resolveEditorLogPath — Unity 6000.5+ project log
// ---------------------------------------------------------------------------

test("projectEditorLogPath resolves <project>/Logs/Editor.log", () => {
  const p = projectEditorLogPath("/Users/me/MyProject");
  assert.equal(p, join("/Users/me/MyProject", "Logs", "Editor.log"));
});

test("projectEditorLogPath returns null when projectPath is null/undefined/empty", () => {
  assert.equal(projectEditorLogPath(null), null);
  assert.equal(projectEditorLogPath(undefined), null);
  assert.equal(projectEditorLogPath(""), null);
});

test("resolveEditorLogPath prefers the project-relative log when it exists", () => {
  // Create a fake project with a Logs/Editor.log; the resolver must pick it
  // over the global log.
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "Logs"));
  writeFileSync(join(project, "Logs", "Editor.log"), "project log content");

  const resolved = resolveEditorLogPath(project, "darwin");
  assert.equal(resolved, join(project, "Logs", "Editor.log"));
});

test("resolveEditorLogPath falls back to the global log when the project log is absent", () => {
  // A project dir with no Logs/Editor.log → resolver returns the global path.
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  // No Logs/ folder created → project log does not exist.
  const resolved = resolveEditorLogPath(project, "darwin");
  assert.equal(resolved, editorLogPath("darwin"));
});

test("resolveEditorLogPath falls back to the global log when projectPath is null", () => {
  // No project context (e.g. the tool was called without --project) → global.
  const resolved = resolveEditorLogPath(null, "darwin");
  assert.equal(resolved, editorLogPath("darwin"));
});

// ---------------------------------------------------------------------------
// readLogTail
// ---------------------------------------------------------------------------

test("readLogTail returns exists:false when the file is absent", () => {
  const result = readLogTail(join(mkdtempSync(join(tmpdir(), "ul-")), "nope.log"));
  assert.equal(result.exists, false);
  assert.equal(result.content, "");
  assert.equal(result.bytes, 0);
  assert.equal(result.error, undefined);
});

test("readLogTail reads a small file in full", () => {
  const dir = mkdtempSync(join(tmpdir(), "ul-"));
  const path = join(dir, "Editor.log");
  const content = "hello unity editor log\n";
  writeFileSync(path, content);

  const result = readLogTail(path);
  assert.equal(result.exists, true);
  assert.equal(result.content, content);
  assert.equal(result.bytes, Buffer.byteLength(content));
  assert.equal(result.error, undefined);
});

test("readLogTail returns only the last maxBytes of a large file", () => {
  const dir = mkdtempSync(join(tmpdir(), "ul-"));
  const path = join(dir, "Editor.log");
  // Write 3KB of A's then 1KB of B's; tail of 1KB must be all B's.
  const head = "A".repeat(3 * 1024);
  const tail = "B".repeat(1024);
  writeFileSync(path, head + tail);

  const result = readLogTail(path, 1024);
  assert.equal(result.exists, true);
  assert.equal(result.bytes, 1024);
  assert.equal(result.content, tail);
});

test("readLogTail caps at DEFAULT_LOG_TAIL_BYTES by default", () => {
  const dir = mkdtempSync(join(tmpdir(), "ul-"));
  const path = join(dir, "Editor.log");
  // Make a file bigger than the default tail and confirm we never read more.
  const huge = "X".repeat(DEFAULT_LOG_TAIL_BYTES + 4096);
  writeFileSync(path, huge);

  const result = readLogTail(path);
  assert.ok(result.bytes <= DEFAULT_LOG_TAIL_BYTES);
  assert.equal(result.bytes, DEFAULT_LOG_TAIL_BYTES);
});

test("readLogTail handles a file that is smaller than maxBytes", () => {
  const dir = mkdtempSync(join(tmpdir(), "ul-"));
  const path = join(dir, "Editor.log");
  writeFileSync(path, "tiny");
  const result = readLogTail(path, 1024 * 1024);
  assert.equal(result.content, "tiny");
  assert.equal(result.bytes, 4);
});

test("readLogTail surfaces a read error without throwing", () => {
  // Open a directory as if it were a file — readSync will fail. The function
  // must catch and return error rather than throw.
  const dir = mkdtempSync(join(tmpdir(), "ul-"));
  // Opening a directory with 'r' succeeds on POSIX, but readSync into a
  // Buffer fails with EISDIR. Verify the error path is taken.
  // Pre-create a path that exists but is a directory:
  const result = readLogTail(dir);
  // Either it reads nothing (bytes 0) or returns an error; either way, no throw.
  assert.ok(result.exists === true);
  assert.equal(result.content, "");
});

// ---------------------------------------------------------------------------
// detectStaleLog — specs/feedback.md 2026-07-05 entry
//
// When an assembly is stuck in a failed-compile state, AssetDatabase.Refresh
// no-ops and the most-recent CSxxxx block in Editor.log can reference on-disk
// source that has ALREADY been fixed. detectStaleLog compares each cited
// source file's mtime against the log's mtime and flags staleness when any
// source file is newer than the log.
// ---------------------------------------------------------------------------

// Helper: set a file's mtime/atime to a specific epoch-second value. Used to
// synthesize the "log older than source" / "log newer than source" cases
// deterministically without racing real wall-clock writes.
function setMtime(path: string, epochSeconds: number): void {
  const t = new Date(epochSeconds * 1000);
  utimesSync(path, t, t);
}

test("detectStaleLog flags staleness when a cited source file is newer than the log", () => {
  // Real-shaped scenario: the log was written at t=1000, but the agent edited
  // Assets/Scripts/Foo.cs at t=2000 (after fixing the namespace the log still
  // complains about). The log's error block is stale.
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "Logs"));
  mkdirSync(join(project, "Assets", "Scripts"), { recursive: true });
  const logPath = join(project, "Logs", "Editor.log");
  const srcPath = join(project, "Assets", "Scripts", "Foo.cs");
  writeFileSync(logPath, "log content");
  writeFileSync(srcPath, "namespace Fixed {}");
  setMtime(logPath, 1000);
  setMtime(srcPath, 2000);

  const result = detectStaleLog(logPath, ["Assets/Scripts/Foo.cs"], project);
  assert.equal(result.staleLogSuspected, true);
  assert.equal(result.newerFiles.length, 1);
  assert.equal(result.newerFiles[0], "Assets/Scripts/Foo.cs");
  assert.ok(result.hint.length > 0, "stale result must carry a recovery hint");
  assert.ok(result.logMtimeMs !== undefined);
});

test("detectStaleLog returns not-stale when the log is newer than every cited source", () => {
  // Log written AFTER the latest source edit → a fresh compile just wrote the
  // log; the error block is current. Must NOT flag staleness.
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "Logs"));
  mkdirSync(join(project, "Assets", "Scripts"), { recursive: true });
  const logPath = join(project, "Logs", "Editor.log");
  const srcPath = join(project, "Assets", "Scripts", "Foo.cs");
  writeFileSync(logPath, "fresh log content");
  writeFileSync(srcPath, "namespace Still.Broken {}");
  setMtime(srcPath, 1000);
  setMtime(logPath, 2000);

  const result = detectStaleLog(logPath, ["Assets/Scripts/Foo.cs"], project);
  assert.equal(result.staleLogSuspected, false);
  assert.equal(result.newerFiles.length, 0);
  assert.equal(result.hint, "");
});

test("detectStaleLog treats equal mtimes as NOT stale (fresh compile wrote both)", () => {
  // Edge case: source and log share the same mtime (a compile just finished
  // and rewrote both in the same second). Equal is the OPPOSITE of stale, so
  // the > comparison must keep it out of the flag.
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "Logs"));
  mkdirSync(join(project, "Assets"), { recursive: true });
  const logPath = join(project, "Logs", "Editor.log");
  const srcPath = join(project, "Assets", "Foo.cs");
  writeFileSync(logPath, "x");
  writeFileSync(srcPath, "x");
  setMtime(logPath, 1500);
  setMtime(srcPath, 1500);

  const result = detectStaleLog(logPath, ["Assets/Foo.cs"], project);
  assert.equal(result.staleLogSuspected, false);
});

test("detectStaleLog accepts Unity asset locators (path with trailing (line,col))", () => {
  // The structured compiler-error extractor separates file from line, but the
  // helper must also tolerate raw asset locators like Assets/Foo.cs(10,14).
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "Logs"));
  mkdirSync(join(project, "Assets"), { recursive: true });
  const logPath = join(project, "Logs", "Editor.log");
  const srcPath = join(project, "Assets", "Foo.cs");
  writeFileSync(logPath, "x");
  writeFileSync(srcPath, "x");
  setMtime(logPath, 1000);
  setMtime(srcPath, 2000);

  const result = detectStaleLog(logPath, ["Assets/Foo.cs(10,14)"], project);
  assert.equal(result.staleLogSuspected, true);
  assert.equal(result.newerFiles[0], "Assets/Foo.cs");
});

test("detectStaleLog ignores cited files that don't exist on disk", () => {
  // A cited file the agent already deleted (or that lived in a package cache
  // the helper can't see) must not crash or falsely flag — skip it.
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "Logs"));
  const logPath = join(project, "Logs", "Editor.log");
  writeFileSync(logPath, "x");
  setMtime(logPath, 1000);

  const result = detectStaleLog(logPath, ["Assets/Gone.cs"], project);
  assert.equal(result.staleLogSuspected, false);
  assert.equal(result.newerFiles.length, 0);
});

test("detectStaleLog ignores cited files that resolve outside the project root", () => {
  // Editor.log sometimes cites paths under Library/Temp or a package cache.
  // Their mtimes aren't under the agent's control and would produce noise, so
  // they must be excluded from the comparison.
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "Logs"));
  const logPath = join(project, "Logs", "Editor.log");
  writeFileSync(logPath, "x");
  setMtime(logPath, 1000);

  // An absolute path and a path-traversal attempt both resolve outside the
  // project root and must be skipped.
  const result = detectStaleLog(
    logPath,
    ["/etc/passwd", "../outside/Foo.cs", "Library/Temp/foo.cs"],
    project,
  );
  assert.equal(result.staleLogSuspected, false);
});

test("detectStaleLog returns not-stale when no project root is supplied", () => {
  // No --project context (the tool was called without it) → cannot resolve
  // cited files; degrade gracefully to "not stale" rather than guessing.
  const logPath = "/dev/null/Foo.log";
  const result = detectStaleLog(logPath, ["Assets/Foo.cs"], null);
  assert.equal(result.staleLogSuspected, false);
  assert.equal(result.newerFiles.length, 0);
  assert.equal(result.hint, "");
});

test("detectStaleLog returns not-stale when the log file doesn't exist", () => {
  // Missing log (no Unity session, or the path resolved to a global log that
  // isn't there) → no mtime to compare; cannot flag staleness.
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  const result = detectStaleLog(
    join(project, "Logs", "Missing.log"),
    ["Assets/Foo.cs"],
    project,
  );
  assert.equal(result.staleLogSuspected, false);
});

test("detectStaleLog bounds newerFiles at 5 entries", () => {
  // A solution-wide rename can touch dozens of files at once. The list is
  // evidence, not an exhaustive roster — bounding it keeps the payload small.
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "Logs"));
  mkdirSync(join(project, "Assets", "Scripts"), { recursive: true });
  const logPath = join(project, "Logs", "Editor.log");
  writeFileSync(logPath, "x");
  setMtime(logPath, 1000);
  const cited: string[] = [];
  for (let i = 0; i < 10; i++) {
    const rel = `Assets/Scripts/File${i}.cs`;
    const abs = join(project, rel);
    writeFileSync(abs, "x");
    setMtime(abs, 2000 + i);
    cited.push(rel);
  }

  const result = detectStaleLog(logPath, cited, project);
  assert.equal(result.staleLogSuspected, true);
  assert.ok(result.newerFiles.length <= 5, "newerFiles must be bounded");
});
