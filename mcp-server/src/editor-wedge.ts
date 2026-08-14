// specs/feedback.md 2026-08-14 — "wedged editor" detection shared by
// `bridge_status` and the `execute_csharp` / `invoke_method` stale-domain
// guard.
//
// Two failure modes wedge a Unity Editor while every signal the health
// classifier trusts (live process + fresh heartbeat + a reachable `/ping`)
// keeps saying "healthy":
//
//   1. `editor_fd_exhaustion` — the Bee build driver hit Mono's internal fd
//      ceiling. The Editor answers HTTP and keeps running the OLD assembly,
//      but `Library/ScriptAssemblies/*.dll` freezes: edits land on disk and
//      are never compiled. `execute_csharp` then executes the STALE assembly
//      and returns a successful-looking result computed from pre-edit code.
//      In the reported session that stale output was believed, acted on, and
//      written into a commit message before the mismatch was noticed by
//      accident.
//
//   2. `main_thread_wedged` — an Editor modal dialog (a material/scene
//      converter, some `ExecuteMenuItem` paths) is blocking Unity's main
//      thread. `/ping` still answers (it is served off the listener thread)
//      while the heartbeat — written from `EditorApplication.update` — goes
//      stale, so the lock classifier reports `dead_bridge` and an agent
//      chases a compile failure that does not exist.
//
// Both are cheap to detect from evidence the server already has at hand: a
// regex over the freshest Editor.log tail for (1), and the *combination* of a
// stale heartbeat with a REACHABLE listener for (2) — a genuinely dead bridge
// assembly has no listener at all, so a reachable `/ping` rules it out.

import { resolveEditorLogPath, readLogTail } from "./unity-log.js";
import { extractProjectHealthIssues } from "./project-health.js";

/**
 * Tail size for the fd-exhaustion scan. Deliberately smaller than
 * `DEFAULT_LOG_TAIL_BYTES` (256 KB): this scan runs on the frequently-called
 * `bridge_status` path and on every annotated `execute_csharp`, and the
 * signature it looks for is a HANG — nothing is appended to the log after it,
 * so it always sits at the very end.
 */
export const WEDGE_SCAN_TAIL_BYTES = 64 * 1024;

/** Freshness window for a cached scan result. See {@link EditorWedgeCache}. */
export const DEFAULT_WEDGE_SCAN_TTL_MS = 5_000;

export interface FdExhaustionScan {
  /** True when the freshest Editor.log tail carries the hang signature. */
  present: boolean;
  /** The log file the scan read (for the response's evidence trail). */
  logPath: string | null;
  /** The matched exception line, when present. */
  raw: string | null;
}

const CLEAN: FdExhaustionScan = { present: false, logPath: null, raw: null };

/**
 * Scan the freshest Editor.log tail for the `editor_fd_exhaustion` signature.
 *
 * Reuses the same log resolver + health extractor as `read_compile_errors`, so
 * a caller can never disagree with what that tool reports. Never throws: an
 * unreadable/absent log degrades to `present: false` (fail open — a health
 * probe must not start erroring because a log file moved).
 *
 * `livePid` is the live Editor PID from the instance lock when known; it feeds
 * the resolver's log-rotation fallback (a batch spawn can rotate `Editor.log`
 * to `Editor-prev.log` while the live editor keeps writing to the rotated
 * file).
 */
export function scanForFdExhaustion(
  projectPath: string | null | undefined,
  livePid?: number,
  globalLogPathOverride?: string,
): FdExhaustionScan {
  if (!projectPath) return CLEAN;
  try {
    const resolved = resolveEditorLogPath(
      projectPath,
      undefined,
      livePid,
      globalLogPathOverride,
    );
    // Provenance guard. `prev_log_fallback` is the resolver's last resort: the
    // log it wanted does not exist at all, so it reads the GLOBAL
    // `Editor-prev.log` with no evidence tying it to this project or this
    // editor. That is fine for "show me whatever compiler errors you can find"
    // (read_compile_errors, where the user asked for the log) but not for
    // declaring a live editor wedged — an unrelated project's rotated log
    // would condemn a perfectly healthy Editor and, via editor_build_wedged,
    // start failing its calls. Every other reason is anchored to this project
    // (`project_log`, `global_log*`) or to this editor's live PID
    // (`prev_log_live_editor`).
    if (resolved.reason === "prev_log_fallback") return CLEAN;
    const logPath = resolved.path;
    const tail = readLogTail(logPath, WEDGE_SCAN_TAIL_BYTES);
    if (!tail.exists || tail.error) return CLEAN;
    const issue = extractProjectHealthIssues(tail.content).find(
      (i) => i.kind === "editor_fd_exhaustion",
    );
    if (!issue) return { present: false, logPath, raw: null };
    return { present: true, logPath, raw: issue.raw };
  } catch {
    return CLEAN;
  }
}

/**
 * Short-TTL in-memory cache for {@link scanForFdExhaustion}. Mirrors
 * {@link StaleAssemblyCache}: session-scoped, no disk cache (root AGENTS.md),
 * `0` TTL disables it. The signature is terminal — once present it stays
 * present until the operator restarts the Editor — so a short window is ample
 * and collapses the repeated probes of one agent turn into a single read.
 */
export class EditorWedgeCache {
  private entry: { result: FdExhaustionScan; asOfMs: number } | null = null;

  record(result: FdExhaustionScan): void {
    this.entry = { result, asOfMs: Date.now() };
  }

  get(ttlMs: number = DEFAULT_WEDGE_SCAN_TTL_MS): FdExhaustionScan | null {
    if (ttlMs <= 0) return null;
    const e = this.entry;
    if (e === null) return null;
    if (Date.now() - e.asOfMs > ttlMs) return null;
    return e.result;
  }

  invalidate(): void {
    this.entry = null;
  }
}
