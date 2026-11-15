// Why the bridge stopped answering, when there is evidence of it.
//
// A stopped heartbeat with a live Unity process cannot tell a dead bridge from
// a slow one: a domain reload longer than the stale threshold (a large project,
// a play-mode reload, a reload a test run deferred to its end) freezes the
// heartbeat exactly like a bridge assembly that never came back. Reading that
// signature alone as `bridge_compile_failed` sent agents to hunt compile errors
// that did not exist. This module reads the evidence that does tell them apart
// — the Editor's own log — and `bridge_compile_failed` is reported only when it
// finds some:
//
//   - `safe_mode`: Unity logged "Safe Mode: …" after the bridge last started —
//     compile errors keep the bridge assembly from loading;
//   - `bridge_not_loaded`: the last domain reload finished and the bridge did
//     not start in it (its assembly failed to compile or load, or it was
//     removed); current compiler errors, the bridge's own first, ride along;
//   - `startup_compile_errors` (no instance lock yet): the Editor's startup
//     compile failed, so it is in Safe Mode or asking about it.
//
// Compiler errors alone are not evidence while a reload runs: Unity does not
// reload after a failed compile, so a running reload loads the last good
// assemblies and the bridge comes back with them.
//
// Without evidence a lost bridge is a reload, a busy main thread or a blip:
// callers answer `editor_reloading` / `response_lost` (retryable) instead.

import type { CompilerError } from "./compiler-errors.js";
import {
  classifyLogCompileErrors,
  DEFAULT_LOG_TAIL_BYTES,
  newestScriptAssemblyMtimeMs,
  readLogTail,
  resolveEditorLogPath,
  statMtimeMs,
} from "./unity-log.js";
import { statSync } from "node:fs";

export type BridgeLossKind = "safe_mode" | "bridge_not_loaded" | "startup_compile_errors";

export interface BridgeLossEvidence {
  kind: BridgeLossKind;
  /** One sentence: what the log shows. */
  detail: string;
  /** The log the evidence was read from. */
  logPath?: string;
  /** The first compiler errors behind it, when there are any. */
  errors?: Array<Pick<CompilerError, "file" | "line" | "code" | "message">>;
}

/** How many errors the evidence carries; read_compile_errors owns the full list. */
const EVIDENCE_ERRORS = 3;

const RELOAD_BEGIN_RE = /^Begin MonoManager ReloadAssembly\b/gm;
const RELOAD_END_RE = /^(?:Mono: successfully reloaded assembly|- Finished resetting the current domain|Domain Reload Profiling:)/gm;
// Logged by BridgeHttpServer.TryStartListener once the listener is bound (and
// the instance lock written), from the bridge's [InitializeOnLoad] — inside
// the reload that loaded it.
const BRIDGE_STARTED_RE = /^\[Unity Open MCP Bridge\] Listening on /gm;
const SAFE_MODE_RE = /^Safe Mode: [^\r\n]*/gm;
// The bridge and the verify package it depends on, as Unity cites them
// (Packages/<name>/…, a PackageCache copy, or a file: package's own path).
const BRIDGE_SOURCE_RE = /com\.alexeyperov\.unity-open-mcp-(?:bridge|verify)\b|[\\/]packages[\\/](?:bridge|verify)[\\/](?:Editor|Runtime)[\\/]/i;

function lastIndex(re: RegExp, text: string, from = 0): number {
  re.lastIndex = from;
  let last = -1;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) last = m.index;
  return last;
}

function lastMatch(re: RegExp, text: string): { index: number; text: string } | null {
  re.lastIndex = 0;
  let last: { index: number; text: string } | null = null;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) last = { index: m.index, text: m[0].trim() };
  return last;
}

function firstErrors(errors: CompilerError[]): BridgeLossEvidence["errors"] {
  return errors.slice(0, EVIDENCE_ERRORS).map(e => ({ file: e.file, line: e.line, code: e.code, message: e.message }));
}

function cite(e: CompilerError): string {
  return `${e.file}(${e.line}): ${e.code} ${e.message}`.slice(0, 240);
}

export interface BridgeLossLogOptions {
  /**
   * No instance lock: the bridge never started in this Editor session. Then a
   * current compile error anywhere is evidence (the startup compile failed).
   */
  cold?: boolean;
  projectRoot?: string | null;
  /** Editor.log mtime / newest built assembly mtime, for the current-vs-historical split. */
  logMtimeMs?: number;
  dllMtimeMs?: number;
}

/**
 * The evidence in an Editor.log tail that the bridge cannot come back on its
 * own, or null when the tail shows none. Pure over the tail (plus the bounded
 * source reads classifyLogCompileErrors does). The caller has already seen the
 * listener gone and the heartbeat stopped (or no lock at all).
 */
export function bridgeLossFromLog(tail: string, opts: BridgeLossLogOptions = {}): Omit<BridgeLossEvidence, "logPath"> | null {
  if (!tail) return null;
  const lastStart = lastIndex(BRIDGE_STARTED_RE, tail);

  const safeMode = lastMatch(SAFE_MODE_RE, tail);
  if (safeMode && safeMode.index > lastStart) {
    return {
      kind: "safe_mode",
      detail: `Editor.log shows Unity in Safe Mode ("${safeMode.text}") after the bridge last started: compile errors keep the bridge assembly from loading.`,
    };
  }

  let classified: ReturnType<typeof classifyLogCompileErrors> | undefined;
  const current = (): CompilerError[] => (classified ??= classifyLogCompileErrors(tail, {
    projectRoot: opts.projectRoot,
    logMtimeMs: opts.logMtimeMs,
    dllMtimeMs: opts.dllMtimeMs,
  })).current;
  // The bridge's own errors first: they are the ones that keep it down.
  const ranked = (): CompilerError[] => {
    const all = current();
    return [...all.filter(e => BRIDGE_SOURCE_RE.test(e.file)), ...all.filter(e => !BRIDGE_SOURCE_RE.test(e.file))];
  };

  // A reload that finished without the bridge starting. A reload still in
  // progress is not evidence, whatever errors the log holds: Unity does not
  // reload after a failed compile, so a running reload loads the last good
  // assemblies and the bridge comes back with them.
  const lastBegin = lastIndex(RELOAD_BEGIN_RE, tail);
  if (lastBegin >= 0 && lastIndex(RELOAD_END_RE, tail, lastBegin) > lastBegin && lastStart < lastBegin) {
    const errors = ranked();
    return {
      kind: "bridge_not_loaded",
      detail: "Editor.log shows the last domain reload finished without the bridge starting its listener: " +
        "the bridge assembly did not load (a compile or load failure), or the bridge package was removed" +
        (errors.length > 0 ? ` (current compiler errors: ${errors.length}; first: ${cite(errors[0])}).` : "."),
      ...(errors.length > 0 ? { errors: firstErrors(errors) } : {}),
    };
  }

  // No lock at all: the bridge never started in this Editor session. A
  // current compile error is then why — Unity stops at the Safe Mode prompt
  // (or in Safe Mode) before it loads any script assembly.
  if (opts.cold) {
    const errors = ranked();
    if (errors.length > 0) {
      return {
        kind: "startup_compile_errors",
        detail: `Unity is running for this project but its bridge never started, and Editor.log holds ${errors.length} ` +
          `current compiler error(s) (first: ${cite(errors[0])}): the startup compile failed, so Unity is in Safe Mode or asking about it.`,
        errors: firstErrors(errors),
      };
    }
  }
  return null;
}

export interface BridgeLossReadOptions {
  /** The Editor's pid from the lock (picks Editor-prev.log after a log rotation). */
  livePid?: number;
  cold?: boolean;
  /** Test seam: the global Editor.log path. */
  globalLogPathOverride?: string;
}

// One-entry memo: the waits poll every 250 ms while the heartbeat is stopped,
// and the log rarely changes between two polls. Keyed by the log's identity
// (path, size, mtime) and the options; in memory only.
let memo: { key: string; value: BridgeLossEvidence | null } | null = null;

/**
 * Read the Editor.log that belongs to this project and look for evidence the
 * bridge cannot come back on its own. Never throws; null when the log is
 * missing, unreadable or shows none.
 */
export function readBridgeLossEvidence(projectPath: string | undefined, opts: BridgeLossReadOptions = {}): BridgeLossEvidence | null {
  if (!projectPath) return null;
  try {
    const logPath = resolveEditorLogPath(projectPath, undefined, opts.livePid, opts.globalLogPathOverride).path;
    let size = -1;
    let mtimeMs = 0;
    try {
      const st = statSync(logPath);
      size = st.size;
      mtimeMs = st.mtimeMs;
    } catch {
      return null;
    }
    const key = `${logPath}|${size}|${mtimeMs}|${opts.cold === true}|${projectPath}`;
    if (memo?.key === key) return memo.value;
    const tail = readLogTail(logPath, DEFAULT_LOG_TAIL_BYTES);
    const found = tail.error ? null : bridgeLossFromLog(tail.content, {
      cold: opts.cold,
      projectRoot: projectPath,
      logMtimeMs: statMtimeMs(logPath) || undefined,
      dllMtimeMs: newestScriptAssemblyMtimeMs(projectPath),
    });
    const value = found ? { ...found, logPath } : null;
    memo = { key, value };
    return value;
  } catch {
    return null;
  }
}

/** Test seam: forget the memoized read. */
export function resetBridgeLossMemoForTest(): void {
  memo = null;
}

/**
 * The `bridge_compile_failed` body: the evidence, and the one channel that
 * works while the bridge is down.
 */
export function bridgeCompileFailedBody(evidence: BridgeLossEvidence, extra: Record<string, unknown> = {}): Record<string, unknown> {
  const safeMode = evidence.kind === "safe_mode" || evidence.kind === "startup_compile_errors";
  return {
    error: {
      code: "bridge_compile_failed",
      message:
        `The bridge is down and will not come back on its own: ${evidence.detail} ` +
        (safeMode ? "Unity is in Safe Mode (or asking about it) until the compile errors are fixed. " : "") +
        "Call unity_open_mcp_read_compile_errors (it reads Editor.log without the bridge), fix the cited " +
        "file/line, then let Unity recompile (focus the Editor, or unity_open_mcp_compile_check once the source compiles). " +
        "If the log shows no compile error, check that the bridge package is installed and its toolbar toggle " +
        "(Tools/Unity Open MCP Bridge) is on.",
    },
    evidence,
    ...extra,
    agentNextSteps: [
      "Call unity_open_mcp_read_compile_errors for the diagnostics, fix them, then recompile.",
    ],
  };
}
