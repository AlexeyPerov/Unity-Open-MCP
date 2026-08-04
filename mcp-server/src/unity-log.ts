// Unity Editor.log path resolution + tail, for the read_compile_errors tool.
//
// When the bridge assembly itself fails to compile, every in-bridge channel
// (read_console, editor_status, an in-bridge CompilationPipeline listener) is
// dead with it, and batch compile_check can't run either (the batch entry
// point lives in the same broken assembly, and Unity's per-project lock blocks
// a second instance). The ONE channel that survives is the live Editor's
// platform Editor.log — Unity writes CSxxxx diagnostics there regardless of
// bridge health. This module resolves that path per-OS (porting the logic from
// hub/src-tauri/src/config/logs.rs) and reads a bounded tail.
//
// Unity 6000.5 moved the Editor.log to a PROJECT-RELATIVE path
// (<project>/Logs/Editor.log) and stops writing to the global per-user log.
// resolveEditorLogPath() prefers the project-relative log when it exists, so
// the tool reads the authoritative log on both old (global-only) and new
// (project-relative) Unity versions without a version check.
//
// No runtime deps beyond node built-ins (mcp-server/AGENTS.md).

import { existsSync, openSync, readSync, fstatSync, closeSync, statSync, readdirSync } from "node:fs";
import type { Dirent } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

export type UnityLogPlatform = "win32" | "darwin" | "linux";

/** Resolve the live Editor.log directory for the current platform. Mirrors
 *  hub/src-tauri/src/config/logs.rs (editor_logs_dir_*).
 *  - macOS: ~/Library/Logs/Unity
 *  - Windows: %LOCALAPPDATA%\Unity\Editor
 *  - Linux: $XDG_CONFIG_HOME/unity3d or ~/.config/unity3d
 */
export function editorLogsDir(
  platform: UnityLogPlatform = process.platform as UnityLogPlatform,
): string {
  switch (platform) {
    case "darwin":
      return join(homedir(), "Library", "Logs", "Unity");
    case "win32": {
      const local = process.env.LOCALAPPDATA;
      if (local) return join(local, "Unity", "Editor");
      // LOCALAPPDATA is effectively always set on Windows; fall back to the
      // Public user profile if it is somehow missing.
      return join(
        "C:\\Users\\Public\\AppData\\Local",
        "Unity",
        "Editor",
      );
    }
    case "linux": {
      const xdg = process.env.XDG_CONFIG_HOME;
      if (xdg) return join(xdg, "unity3d");
      return join(homedir(), ".config", "unity3d");
    }
    default:
      return join(homedir(), "Library", "Logs", "Unity");
  }
}

/** Resolve the global (per-user) Editor.log file path for the current platform. */
export function editorLogPath(
  platform: UnityLogPlatform = process.platform as UnityLogPlatform,
): string {
  return join(editorLogsDir(platform), "Editor.log");
}

/**
 * Resolve the project-relative Editor.log path Unity 6000.5+ writes
 * (`<project>/Logs/Editor.log`). Returns null when no project path is given.
 */
export function projectEditorLogPath(
  projectPath: string | null | undefined,
): string | null {
  if (!projectPath) return null;
  return join(projectPath, "Logs", "Editor.log");
}

/**
 * The rotated previous log. Unity renames the live Editor.log to
 * Editor-prev.log on every fresh launch (a new Editor start, AND a failed
 * batch spawn against an already-open project). When a live editor holds the
 * project and a batch invocation rotates the log, the live editor keeps its
 * open handle and writes to Editor-prev.log from then on, while Editor.log is
 * frozen at a tiny startup size — reading it returns "no errors" forever
 * (feedback-fable-31-07 §5).
 *
 * Returns the GLOBAL prev-log path. On Unity 6000.5+ the rotated prev log
 * lives next to the project-relative live log (`<project>/Logs/Editor-prev.log`),
 * not in the global dir — use {@link projectEditorPrevLogPath} when the
 * project-relative live log is the resolved candidate.
 */
export function editorPrevLogPath(
  platform: UnityLogPlatform = process.platform as UnityLogPlatform,
): string {
  return join(editorLogsDir(platform), "Editor-prev.log");
}

/**
 * Resolve the project-relative Editor-prev.log path Unity 6000.5+ rotates the
 * live log into (`<project>/Logs/Editor-prev.log`). Returns null when no
 * project path is given. Mirrors {@link projectEditorLogPath}: on 6000.5+ the
 * prev log follows the live log into the project's Logs dir, so the prev-log
 * fallback must read THAT file, not the stale global Editor-prev.log left over
 * from a pre-6000.5 session.
 */
export function projectEditorPrevLogPath(
  projectPath: string | null | undefined,
): string | null {
  if (!projectPath) return null;
  return join(projectPath, "Logs", "Editor-prev.log");
}

/** Outcome of resolveEditorLogPath: the chosen path plus a short machine/
 *  agent-facing reason so callers can surface WHY a non-obvious file was read
 *  (e.g. "Editor.log frozen at 2.5KB while a live editor holds the project;
 *  fell back to Editor-prev.log"). */
export interface ResolvedEditorLog {
  path: string;
  reason:
    | "project_log"
    | "global_log"
    | "prev_log_live_editor"
    | "prev_log_fallback";
}

/**
 * Pick the authoritative Editor.log to read for compile-error extraction.
 *
 * Unity 6000.5+ redirects the Editor.log to a project-relative path
 * (`<project>/Logs/Editor.log`) and stops writing to the global per-user log.
 * On those versions the global file is stale (left over from pre-6000.5
 * sessions) and reading it returns "0 errors" even when the Editor is in Safe
 * Mode with real compile errors.
 *
 * Resolution order (no version check needed — just prefer whichever log is
 * available, with the project log winning ties):
 *   1. project-relative `<project>/Logs/Editor.log` — when it exists
 *   2. global `editorLogPath()` — fallback for pre-6000.5 Unity
 *
 * When the project path is unknown (no `--project`), or the project-relative
 * log doesn't exist, the global log is used as before.
 *
 * feedback-fable-31-07 §5 — Editor-prev.log fallback. When a live editor
 * holds the project (a known `livePid` is alive) and the resolved Editor.log
 * looks frozen (implausibly small, e.g. < 4KB — the tell-tale sign of a
 * rotated log the live editor is no longer writing to), fall back to
 * Editor-prev.log, which is where the live editor keeps writing after the
 * rotation. `livePid` is optional; when absent the prev.log fallback only
 * triggers on a frozen-looking log, which is conservative.
 */
export function resolveEditorLogPath(
  projectPath: string | null | undefined,
  platform: UnityLogPlatform = process.platform as UnityLogPlatform,
  livePid?: number,
): ResolvedEditorLog {
  const project = projectEditorLogPath(projectPath);
  const global = editorLogPath(platform);

  const candidate = project && existsSync(project) ? project : global;
  const candidateReason: ResolvedEditorLog["reason"] =
    project && existsSync(project) ? "project_log" : "global_log";

  // On 6000.5+ the rotated prev log lives next to the project-relative live
  // log (<project>/Logs/Editor-prev.log), not in the global dir. When the
  // resolved candidate is the project log, the prev-log fallback must read the
  // project prev log; otherwise (pre-6000.5 global candidate) fall back to the
  // global prev log as before. Reading the stale global prev log here would
  // return compile errors from an unrelated older session.
  const usingProjectLog = candidateReason === "project_log";
  const prev =
    usingProjectLog && project
      ? projectEditorPrevLogPath(projectPath) ?? editorPrevLogPath(platform)
      : editorPrevLogPath(platform);
  const prevExists = existsSync(prev);
  const candidateFrozen = isLogFrozen(candidate);

  // Strong signal: a live editor is known to hold the project AND Editor.log
  // is frozen/small. This is exactly the log-rotation scenario — a batch spawn
  // rotated Editor.log while the live editor keeps writing to Editor-prev.log.
  // Prefer Editor-prev.log when it exists.
  //
  // The frozen check is ONLY meaningful with a live PID: a machine that is not
  // currently running Unity naturally has a small stale global Editor.log and a
  // recent Editor-prev.log, which is the NORMAL state, not a rotation in
  // progress. Without a live PID we never fall back on frozen-ness alone.
  if (prevExists && livePid && livePid > 0 && candidateFrozen) {
    return { path: prev, reason: "prev_log_live_editor" };
  }
  // No live PID context: only fall back when the resolved candidate does not
  // exist at all but Editor-prev.log does (a hard missing-log case).
  if (!livePid && !existsSync(candidate) && prevExists) {
    return { path: prev, reason: "prev_log_fallback" };
  }
  return { path: candidate, reason: candidateReason };
}

/** Threshold below which Editor.log is "frozen" — a freshly-rotated log the
 *  live editor stopped writing to is typically a few hundred bytes to ~2.5KB
 *  (just the batch spawn's startup banner before it died on the project lock). */
const FROZEN_LOG_BYTES = 4096;

function isLogFrozen(path: string): boolean {
  // A frozen log is implausibly small. The real live log is usually tens of KB
  // minimum once the editor has compiled anything.
  return statSize(path) < FROZEN_LOG_BYTES;
}

function statSize(path: string): number {
  try {
    return existsSync(path) ? statSync(path).size : 0;
  } catch {
    return 0;
  }
}

/** Default tail size. Bounded so a multi-MB log can't blow up the tool
 *  response; 256KB is ample for a compile-error burst (Unity writes the
 *  diagnostics in a contiguous block near the end of the log). */
export const DEFAULT_LOG_TAIL_BYTES = 256 * 1024;

export interface ReadLogTailResult {
  /** Absolute path that was read. */
  path: string;
  /** Whether the file existed and was read. */
  exists: boolean;
  /** The tail content. Empty when the file is missing or unreadable. */
  content: string;
  /** Bytes read (content.length in UTF-8 bytes). 0 when missing. */
  bytes: number;
  /** Error message when the file existed but could not be read. */
  error?: string;
}

/**
 * Read up to `maxBytes` from the END of a file, as a UTF-8 string. Returns
 * { exists: false } when the file is absent. Never throws — read failures
 * (permissions, vanished mid-read) surface as { exists, error }.
 *
 * The tail is read by seeking to (size - maxBytes) and reading forward, so a
 * multi-MB log is not loaded in full.
 */
export function readLogTail(
  path: string,
  maxBytes: number = DEFAULT_LOG_TAIL_BYTES,
): ReadLogTailResult {
  if (!existsSync(path)) {
    return { path, exists: false, content: "", bytes: 0 };
  }
  let fd;
  try {
    fd = openSync(path, "r");
    const stat = fstatSync(fd);
    const size = stat.size;
    const readLen = Math.min(size, Math.max(0, maxBytes));
    const start = size - readLen;
    const buf = Buffer.alloc(readLen);
    // readSync may return fewer bytes than requested if the file is being
    // written concurrently; loop until the buffer is filled or we hit EOF.
    let read = 0;
    while (read < readLen) {
      // openSync's positional read overload (offset) is used so we don't rely
      // on the file pointer's current position.
      const n = readSync(fd, buf, read, readLen - read, start + read);
      if (n === 0) break;
      read += n;
    }
    return {
      path,
      exists: true,
      content: buf.subarray(0, read).toString("utf8"),
      bytes: read,
    };
  } catch (err) {
    return {
      path,
      exists: true,
      content: "",
      bytes: 0,
      error: err instanceof Error ? err.message : String(err),
    };
  } finally {
    if (fd !== undefined) {
      try {
        closeSync(fd);
      } catch {
        // best-effort
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Stale-log detection (specs/feedback.md 2026-07-05 entry).
//
// read_compile_errors reads a tail of Editor.log. When an assembly is in a
// failed-compile state, AssetDatabase.Refresh performs an incremental no-op
// (Unity sees no import change) and never rewrites the log's error block, so
// the most recent CSxxxx block in Editor.log can be STALE — referencing an
// on-disk namespace/symbol that has already been fixed. The agent then trusts
// the stale errors as if they were current and burns cycles chasing a
// problem it already solved.
//
// Mitigation: compare the mtime of the source files cited by the parsed
// compiler errors against the mtime of Editor.log itself. If ANY cited
// source file is newer than the log (the file was edited after the log's
// most recent write), the log's most-recent error block predates the latest
// fix and the errors may no longer apply. The router attaches a
// `staleLogSuspected` flag + a recovery hint so the agent knows to force a
// recompile via reimport_package / compile_check before trusting the result.
// ---------------------------------------------------------------------------

export interface StaleLogResult {
  /** True when at least one cited source file is newer than Editor.log. */
  staleLogSuspected: boolean;
  /** Editor.log mtime in epoch ms, when readable. */
  logMtimeMs?: number;
  /**
   * Cited source files (project-relative) whose mtime is newer than the log.
   * Bounded — the agent does not need every offender, just enough to confirm
   * the diagnosis and start a recompile.
   */
  newerFiles: string[];
  /** One-line agent-facing recovery hint. Empty when not stale. */
  hint: string;
}

/** Upper bound on the number of newer files reported. The list is evidence,
 *  not an exhaustive roster — keeping it small avoids a giant payload when
 *  many files were touched together (e.g. a solution-wide rename). */
const MAX_NEWER_FILES = 5;

/**
 * Decide whether the Editor.log at `logPath` is likely stale relative to the
 * on-disk source files cited by the parsed compiler errors.
 *
 * The decision is conservative — it only flags staleness when ALL of:
 *   - the log file exists and has a readable mtime,
 *   - at least one cited source file resolves under `projectRoot` and exists,
 *   - that source file's mtime is strictly newer than the log's mtime.
 *
 * Returns `staleLogSuspected: false` (no hint) whenever the comparison cannot
 * be made (no project root, no log, no cited files, all cited files outside
 * the project). Never throws.
 *
 * Paths in `citedFiles` may be Unity-style asset locators
 * (`Assets/Foo.cs(10,14)`) or already-stripped paths (`Assets/Foo.cs`); this
 * helper strips a trailing `(...)` locator and accepts both forms.
 */
export function detectStaleLog(
  logPath: string,
  citedFiles: ReadonlyArray<string>,
  projectRoot: string | null | undefined,
): StaleLogResult {
  const empty: StaleLogResult = {
    staleLogSuspected: false,
    newerFiles: [],
    hint: "",
  };
  if (!projectRoot) return empty;

  let logMtimeMs: number | undefined;
  try {
    if (!existsSync(logPath)) return empty;
    logMtimeMs = statSync(logPath).mtimeMs;
  } catch {
    return empty;
  }
  if (logMtimeMs === undefined) return empty;

  const newerFiles: string[] = [];
  for (const raw of citedFiles) {
    if (newerFiles.length >= MAX_NEWER_FILES) break;
    if (!raw) continue;
    // Strip a trailing Unity asset locator `(line,col)` if present. The
    // structured compiler-error extractor already separates file from line,
    // but defensive stripping keeps this helper callable on raw strings too.
    const paren = raw.lastIndexOf("(");
    const rel = (paren >= 0 ? raw.slice(0, paren) : raw).trim();
    if (!rel) continue;
    // Only inspect paths that resolve inside the project root. Editor.log
    // sometimes cites files under Library/Temp or a package cache; their
    // mtimes are not under the agent's control and would produce noise.
    const abs = join(projectRoot, rel);
    if (!abs.startsWith(projectRoot)) continue;
    let fileMtimeMs: number;
    try {
      if (!existsSync(abs)) continue;
      fileMtimeMs = statSync(abs).mtimeMs;
    } catch {
      continue;
    }
    // Strictly newer: an equal mtime means the log was rewritten at the same
    // instant the file was saved (a fresh compile just finished writing both)
    // — that is the OPPOSITE of stale, so the > keeps it out of the flag.
    if (fileMtimeMs > logMtimeMs) {
      newerFiles.push(rel);
    }
  }

  if (newerFiles.length === 0) {
    return { staleLogSuspected: false, logMtimeMs, newerFiles: [], hint: "" };
  }
  return {
    staleLogSuspected: true,
    logMtimeMs,
    newerFiles,
    hint:
      "Editor.log appears stale — at least one cited source file was edited " +
      "after the log's most recent write (Unity's incremental compiler likely " +
      "no-op'd a recompile of the broken assembly, so the error block may no " +
      "longer apply). Force a genuine recompile before trusting these errors: " +
      "call unity_open_mcp_reimport_package on the affected local package, or " +
      "unity_open_mcp_compile_check to spawn a fresh headless recompile.",
  };
}

// ---------------------------------------------------------------------------
// feedback-01-08-glm §5b — stale-ASSEMBLY detection (distinct from stale-log).
//
// The staleLog heuristic compares source-file mtimes against Editor.log's mtime
// and only runs when there are errors to cite. The field report's deeper
// complaint is that read_compile_errors reports `no_errors_found` against a
// STALE assembly: after a C# edit, AssetDatabase.Refresh no-op'd, the DLL was
// never rebuilt, and the log carries no fresh CSxxxx — so the agent believes
// the code is healthy when the running assembly predates the fix.
//
// Mitigation: compare the newest Library/ScriptAssemblies/*.dll mtime against
// every Assets/**/*.cs source mtime. If ANY source is newer than the newest
// DLL, the running assembly is stale and the healthy signal cannot be trusted.
// This runs unconditionally (even on no_errors_found), bounded by a source-scan
// cap so a huge project does not stall the read.
// ---------------------------------------------------------------------------

export interface StaleAssemblyResult {
  /** True when at least one source .cs is newer than the newest built DLL. */
  staleAssembly: boolean;
  /** Newest Library/ScriptAssemblies/*.dll mtime in epoch ms, when readable. */
  dllMtimeMs?: number;
  /** Project-relative source files newer than the newest DLL (bounded). */
  newerSources: string[];
  /** One-line agent-facing recovery hint. Empty when not stale. */
  hint: string;
}

/** Cap on the number of newer sources reported (evidence, not a roster). */
const MAX_NEWER_SOURCES = 5;
/** Cap on how many .cs files to stat before giving up the scan. A project that
 *  exceeds this likely has a fresh DLL anyway (compile times track source
 *  count); the cap protects pathological trees from stalling the read. */
const MAX_CS_SCAN = 4000;

/**
 * Decide whether the built assembly set under `<project>/Library/ScriptAssemblies`
 * is stale relative to the on-disk C# sources under `<project>/Assets`.
 *
 * Conservative: flags staleness only when ALL of:
 *   - the ScriptAssemblies dir exists and holds at least one .dll,
 *   - at least one C# source under Assets is strictly newer than the newest
 *     .dll mtime.
 * Never throws; returns a clean "not stale" on any resolution failure.
 */
export function detectStaleAssembly(projectRoot: string | null | undefined): StaleAssemblyResult {
  const empty: StaleAssemblyResult = { staleAssembly: false, newerSources: [], hint: "" };
  if (!projectRoot) return empty;

  const dllDir = join(projectRoot, "Library", "ScriptAssemblies");
  let dllMtimeMs: number | undefined;
  try {
    if (!existsSync(dllDir)) return empty;
    let newest = 0;
    for (const entry of readdirSync(dllDir)) {
      if (!entry.endsWith(".dll")) continue;
      const m = statSync(join(dllDir, entry)).mtimeMs;
      if (m > newest) newest = m;
    }
    if (newest === 0) return empty; // no DLLs built yet
    dllMtimeMs = newest;
  } catch {
    return empty;
  }

  // Scan Assets/ for .cs sources newer than the newest DLL. Walk top-down,
  // skipping Library/Temp/obj etc. (Assets/ has none of those, but be safe).
  const newerSources: string[] = [];
  let scanned = 0;
  const visit = (dir: string): void => {
    if (newerSources.length >= MAX_NEWER_SOURCES || scanned >= MAX_CS_SCAN) return;
    let entries: Dirent[];
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const ent of entries) {
      if (newerSources.length >= MAX_NEWER_SOURCES || scanned >= MAX_CS_SCAN) return;
      const full = join(dir, ent.name);
      if (ent.isDirectory()) {
        visit(full);
      } else if (ent.isFile() && ent.name.endsWith(".cs")) {
        scanned++;
        try {
          const m = statSync(full).mtimeMs;
          if (m > (dllMtimeMs as number)) {
            const rel = full.slice((projectRoot as string).length).replace(/^[\\/]+/, "");
            newerSources.push(rel);
          }
        } catch {
          /* file vanished mid-scan */
        }
      }
    }
  };
  try {
    visit(join(projectRoot, "Assets"));
  } catch {
    return empty;
  }

  if (newerSources.length === 0) {
    return { staleAssembly: false, dllMtimeMs, newerSources: [], hint: "" };
  }
  return {
    staleAssembly: true,
    dllMtimeMs,
    newerSources,
    hint:
      "The built assembly appears stale — at least one Assets/**/*.cs source is " +
      "newer than the newest Library/ScriptAssemblies/*.dll (Unity's incremental " +
      "compiler likely no-op'd a recompile, so the running assembly predates the " +
      "latest source). Do NOT trust a no_errors_found signal until the assembly is " +
      "rebuilt: call unity_open_mcp_recompile_scripts to force a deterministic " +
      "recompile, then re-read compile errors.",
  };
}

// ---------------------------------------------------------------------------
// feedback-04-08-opus §2 — log-authorship detection (distinct from stale-log
// and stale-assembly). The two existing heuristics cover "my editor wrote the
// log but the errors may be stale" (staleLogSuspected) and "my editor's DLL
// predates my source" (staleAssembly). Neither covers the inverse, more
// dangerous case: the log was written by a DIFFERENT Unity — a batch-mode run
// of a newer Unity version whose errors (e.g. an API deprecation that is only
// an error on that version) cannot occur in the live editor.
//
// Unity prints a header block at the top of Editor.log whose first lines are:
//
//   Unity Editor version:    6000.5.5f1 (d16e074b49fd)
//   Batch mode:              YES
//   Date:                    2026-08-04T08:39:08Z
//   COMMAND LINE ARGUMENTS: ...
//
// We parse that header from the tail (the tail always begins mid-log unless the
// editor just launched, but a -batchmode run writes its header near its end too
// because a batch run is short — and even when the header scrolled past the
// tail, the absence of a parseable header is itself the benign signal "this is
// a live-editor log we cannot fingerprint", so no false mismatch fires).
// ---------------------------------------------------------------------------

export interface LogAuthorship {
  /** Unity version parsed from the log header's "Unity Editor version:" line,
   *  e.g. "6000.5.5f1". null when the header was not present in the tail. */
  logUnityVersion: string | null;
  /** True when the header's "Batch mode:" line is "YES". null when absent. */
  logBatchMode: boolean | null;
  /** ISO timestamp parsed from the header's "Date:" line, when present. */
  logDate: string | null;
}

/**
 * Parse the Unity Editor.log header fields that identify WHO wrote the log.
 * Scans only the tail content already read by the tool (no extra disk I/O).
 *
 * Returns nulls (not throws) when the header lines are absent — a long-running
 * live editor's tail starts mid-log and won't contain the header, which is the
 * benign case (no version to compare against, so no false mismatch).
 */
export function parseLogAuthorship(tail: string): LogAuthorship {
  const empty: LogAuthorship = {
    logUnityVersion: null,
    logBatchMode: null,
    logDate: null,
  };
  if (!tail) return empty;

  // The header is written once per editor launch near the TOP of the log. The
  // tool reads only the tail, so for a long session the header is gone — that
  // is fine (return nulls). For a short -batchmode run the whole log fits in
  // the tail and the header is present. Match the first occurrence only.
  const versionMatch = tail.match(
    /Unity Editor version:\s*([0-9][^\s)]*)/,
  );
  const batchMatch = tail.match(/Batch mode:\s*(YES|NO)/i);
  const dateMatch = tail.match(/Date:\s*([^\r\n]+)/);

  return {
    logUnityVersion: versionMatch ? versionMatch[1].trim() : null,
    logBatchMode: batchMatch
      ? batchMatch[1].trim().toUpperCase() === "YES"
      : null,
    logDate: dateMatch ? dateMatch[1].trim() : null,
  };
}

export interface LogAuthorshipMismatch {
  /** True when the log's Unity version differs from the live bridge's. */
  versionMismatch: boolean;
  /** The version that wrote the log (from the header). */
  logUnityVersion: string | null;
  /** The live bridge's version, when known. */
  liveUnityVersion: string | null;
  /** True when the log was written by a -batchmode run (not the live editor). */
  logBatchMode: boolean | null;
  /** One-line agent-facing warning. Empty when not a mismatch. */
  hint: string;
}

/**
 * Compare the log's authorship against the live bridge's Unity version. This is
 * the cross-check feedback-04-08-opus §2 asks for: a batch run of a NEWER Unity
 * leaves a log whose errors (e.g. a deprecation that is only an error on that
 * version) cannot occur in the live editor, so surfacing them as "current"
 * sends the agent chasing ghosts (or refusing to continue) for minutes.
 *
 * Two cheap signals, both from data already at hand:
 *   - the log's "Unity Editor version:" vs the live bridge unityVersion
 *   - the log's "Batch mode:" flag (a batch run is never the live editor)
 *
 * Conservative: only flags a mismatch when BOTH the log version parsed AND a
 * live version is known, AND they differ. A null log version (header scrolled
 * past the tail) never fires — there is nothing to compare. When the log is a
 * batch run but the versions agree, we surface the batch-mode flag WITHOUT
 * calling it a mismatch (the agent still benefits from knowing it is a batch
 * log). The router decides how to fold this into the response.
 */
export function compareLogAuthorship(
  authorship: LogAuthorship,
  liveUnityVersion: string | null | undefined,
): LogAuthorshipMismatch {
  const logUnityVersion = authorship.logUnityVersion;
  const logBatchMode = authorship.logBatchMode;
  const live = liveUnityVersion ?? null;

  const versionMismatch =
    logUnityVersion !== null && live !== null && logUnityVersion !== live;

  if (versionMismatch) {
    return {
      versionMismatch: true,
      logUnityVersion,
      liveUnityVersion: live,
      logBatchMode,
      hint:
        `Editor.log was written by Unity ${logUnityVersion}` +
        (logBatchMode ? " (batch mode)" : "") +
        ` but the live editor is ${live}. The errors below CANNOT all occur ` +
        `in the live editor — do NOT treat a compile_failed signal from this ` +
        `log as current. A different Unity wrote it` +
        (logBatchMode ? " (likely a headless -batchmode run)" : "") +
        `. If the live editor is healthy, these errors do not apply.`,
    };
  }

  // No version mismatch, but still surface a batch-mode log so the agent knows
  // the errors are from a headless run, not the interactive editor. This is
  // informational (hint present, versionMismatch false) — the router emits the
  // fields without the "do not trust" framing.
  if (logBatchMode === true) {
    return {
      versionMismatch: false,
      logUnityVersion,
      liveUnityVersion: live,
      logBatchMode,
      hint:
        `Editor.log was written by a -batchmode Unity run` +
        (logUnityVersion ? ` (${logUnityVersion})` : "") +
        (live ? `, not the live editor (${live})` : "") +
        `. The errors are real for that run; confirm they still apply to the ` +
        `live editor before acting.`,
    };
  }

  return {
    versionMismatch: false,
    logUnityVersion,
    liveUnityVersion: live,
    logBatchMode,
    hint: "",
  };
}
