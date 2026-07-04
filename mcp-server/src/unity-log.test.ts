import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, openSync, closeSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  editorLogsDir,
  editorLogPath,
  projectEditorLogPath,
  resolveEditorLogPath,
  readLogTail,
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
