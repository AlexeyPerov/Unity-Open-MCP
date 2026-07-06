// M27 Plan 1 — Cold Safe Mode / dead-bridge detection (process-presence scan).
//
// When Unity launches directly into Safe Mode (the bridge assembly fails to
// compile from a cold start), the bridge's `[InitializeOnLoad]` static
// constructor never runs, so **no instance lock is ever written**.
// `classifyInstance(null)` returns `gone` — the same classification as "Unity
// is not running." Without a positive signal, the MCP server cannot tell the
// agent to call `read_compile_errors`; it must guess or follow the SKILL's
// manual `ps`/`grep` workaround.
//
// This module ports the Hub's Unity-process scanner
// (`hub/src-tauri/src/config/running_unity.rs`) into the MCP server so the
// cold-start case can be detected: when `classifyInstance` is `gone` AND a
// live Unity process whose command line references this project is found,
// callers (`bridge_status`, `LiveClient`, the CLI `wait-for-ready`) treat the
// state as **cold Safe Mode / bridge never compiled** and surface the same
// recovery path as mid-session `dead_bridge` → `read_compile_errors`.
//
// The MCP helper only needs **single-project lookup** (does any Unity process
// match THIS project path?), not the Hub's full multi-project scan, so the
// public surface is just `findUnityForProject(projectPath)`.
//
// Cross-platform: macOS + Windows (match the Hub). Linux returns null — the
// Hub also returns an empty list on every other target, so this is no
// regression. No new runtime deps: `node:child_process`, `node:path`, and the
// shared `normalizePath` from `instance-discovery.ts` (NOT the Hub's
// `canonicalize` — case-sensitive paths on macOS/Linux must compare verbatim,
// and canonicalize would normalize away the very differences that distinguish
// two projects).

import { execFileSync } from "node:child_process";
import { basename, sep } from "node:path";
import { normalizePath } from "./instance-discovery.js";

/** Supported `process.platform` values for the scanner. */
export type UnityScanPlatform = "win32" | "darwin" | "linux";

/** One Unity process discovered by the scanner. */
export interface RunningUnity {
  pid: number;
  /** Normalized `-projectPath` argument, or null when it could not be parsed
   *  (Unity opened without `-projectPath`, e.g. via the Hub's "Open Editor"). */
  projectPath: string | null;
}

/**
 * Pure `-projectPath` argument parser. Port of Hub
 * `running_unity.rs#parse_project_path_arg`. Accepts both the space-separated
 * (`-projectPath <value>`) and equals (`-projectPath=<value>`) forms, plus
 * the long `--projectPath` variants. Surrounding single or double quotes on
 * the value are stripped. Returns the first match (matching the rest of the
 * Hub CLI surface); null when the flag is absent or sits at the end of argv
 * with no value.
 *
 * This is the unit-testable core: the OS-specific scanners feed it an argv
 * slice extracted from `ps` / PowerShell output.
 */
export function parseProjectPathArg(args: ReadonlyArray<string>): string | null {
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    const eqForm =
      stripPrefix(arg, "-projectPath=") ?? stripPrefix(arg, "--projectPath=");
    if (eqForm !== null) {
      return unquote(eqForm);
    }
    if (arg === "-projectPath" || arg === "--projectPath") {
      const next = args[i + 1];
      if (next === undefined) return null; // flag at end with no value
      return unquote(next);
    }
  }
  return null;
}

/** Return `rest` when `value` starts with `prefix`, else null. */
function stripPrefix(value: string, prefix: string): string | null {
  if (value.startsWith(prefix)) return value.slice(prefix.length);
  return null;
}

/**
 * Strip a single layer of surrounding double or single quotes from a flag
 * value. Hub-launched Unity passes the path unquoted on macOS (the OS handles
 * spaces natively) and quoted on Windows when the path contains spaces. We
 * accept both because the flag may have been supplied by a third-party tool.
 */
function unquote(value: string): string {
  const trimmed = value.trim();
  if (trimmed.length >= 2) {
    const first = trimmed.charCodeAt(0);
    const last = trimmed.charCodeAt(trimmed.length - 1);
    const dq = '"'.charCodeAt(0);
    const sq = "'".charCodeAt(0);
    if ((first === dq && last === dq) || (first === sq && last === sq)) {
      return trimmed.slice(1, -1);
    }
  }
  return trimmed;
}

/**
 * `true` when the command line's executable basename is `Unity`
 * (case-sensitive — `unityhub://` URL handlers and the `Unity Hub` GUI do not
 * match). Windows is filtered by the PowerShell `Name='Unity.exe'` selector
 * upstream, so this helper is only used for the macOS / `ps` path.
 *
 * Extracting the executable path from a `ps` command line is harder than it
 * looks: the Hub GUI binary lives at
 * `/Applications/Unity Hub.app/Contents/MacOS/Unity Hub` — i.e. the executable
 * name itself contains a space, and the parent directory contains the word
 * "Unity". A naive `split_whitespace().next()` would return `/Applications/Unity`,
 * which basename-matches `Unity` and (incorrectly) tags the Hub GUI as a
 * running editor. We solve this by extending the executable prefix past any
 * tokens that don't look like flags: the Unity editor is virtually always
 * launched with `-projectPath` (or some other `-flag`), so the first `-flag`
 * token is a reliable end-of-path marker.
 */
export function isUnityCommandLine(commandLine: string): boolean {
  const executable = firstExecutablePath(commandLine);
  const unquoted = trimMatching(executable, '"', "'");
  const name = basenameSafe(unquoted);
  return name === "Unity";
}

function trimMatching(value: string, ...chars: string[]): string {
  let s = value;
  while (s.length > 0) {
    const c = s[0];
    if (!chars.includes(c)) break;
    // Trim a matching pair only when both ends agree; otherwise stop.
    if (s.length < 2 || s[s.length - 1] !== c) break;
    s = s.slice(1, -1);
  }
  return s;
}

function basenameSafe(path: string): string {
  if (!path) return "";
  // `path.basename` mishandles Windows-style paths on a non-Windows host;
  // split on both separators and take the last non-empty segment.
  const segments = path.split(/[\\/]/).filter((s) => s.length > 0);
  return segments.length > 0 ? segments[segments.length - 1] : "";
}

/**
 * Extract the executable path prefix from a `ps` command line. If the line
 * starts with a `"`, consume the matching closing quote. Otherwise extend the
 * prefix through any tokens that don't start with `-`, stopping at the first
 * flag (or end of line). Port of Hub `first_executable_path`.
 */
function firstExecutablePath(commandLine: string): string {
  if (commandLine.length === 0) return commandLine;
  if (commandLine[0] === '"') {
    const end = commandLine.indexOf('"', 1);
    if (end >= 0) return commandLine.slice(0, end + 1);
  }
  let pos = 0;
  for (const token of commandLine.split(/\s+/)) {
    if (token.startsWith("-") && token !== "--") {
      return commandLine.slice(0, pos).trimEnd();
    }
    // +1 for the whitespace split() consumed.
    pos += token.length + 1;
  }
  return commandLine.trimEnd();
}

/**
 * Naive argv splitter for a `ps`-formatted command line. Honours double and
 * single quotes and treats backslashes as literal (macOS `ps` output never
 * escapes them). Port of Hub `split_args`.
 */
export function splitArgs(line: string): string[] {
  const out: string[] = [];
  let current = "";
  let inSingle = false;
  let inDouble = false;
  for (const ch of line) {
    if (ch === "'" && !inDouble) {
      inSingle = !inSingle;
      continue;
    }
    if (ch === '"' && !inSingle) {
      inDouble = !inDouble;
      continue;
    }
    if (/\s/.test(ch) && !inSingle && !inDouble) {
      if (current.length > 0) {
        out.push(current);
        current = "";
      }
      continue;
    }
    current += ch;
  }
  if (current.length > 0) out.push(current);
  return out;
}

/**
 * Windows argv splitter. Same rules as {@link splitArgs} but treats `/` and
 * `\` as ordinary characters (Windows paths use both). Honours `\"` as an
 * embedded literal quote inside a double-quoted string, matching the
 * `CommandLine` quoting convention emitted by `Get-CimInstance Win32_Process`.
 * Port of Hub `split_args_windows`.
 */
export function splitArgsWindows(line: string): string[] {
  const out: string[] = [];
  let current = "";
  let inDouble = false;
  const chars: string[] = Array.from(line);
  for (let i = 0; i < chars.length; i++) {
    const ch = chars[i];
    if (inDouble && ch === "\\") {
      const next = chars[i + 1];
      if (next === '"' || next === "\\") {
        current += next;
        i += 1;
        continue;
      }
    }
    if (ch === '"') {
      inDouble = !inDouble;
      continue;
    }
    if (/\s/.test(ch) && !inDouble) {
      if (current.length > 0) {
        out.push(current);
        current = "";
      }
      continue;
    }
    current += ch;
  }
  if (current.length > 0) out.push(current);
  return out;
}

/**
 * Parse macOS `ps -axww -o pid=,command=` output. One line per process: the
 * PID is the first whitespace-delimited token, the rest is the full command.
 * Lines whose executable basename is not `Unity` are skipped (rejects the Hub
 * GUI, `unityhub://` URL handlers, unrelated tools). Port of Hub
 * `parse_ps_output`.
 */
export function parsePsOutput(stdout: string): RunningUnity[] {
  const out: RunningUnity[] = [];
  for (const line of stdout.split(/\r?\n/)) {
    const trimmed = line.replace(/^\s+/, "");
    if (trimmed.length === 0) continue;
    const m = trimmed.match(/^(\S+)\s+(.*)$/);
    if (!m) continue;
    const pidStr = m[1];
    const rest = m[2].replace(/^\s+/, "");
    const pid = Number.parseInt(pidStr, 10);
    if (!Number.isInteger(pid) || pid <= 0) continue;
    if (!isUnityCommandLine(rest)) continue;
    const projectPath = parseProjectPathArg(splitArgs(rest));
    out.push({
      pid,
      projectPath: projectPath !== null ? normalizePath(projectPath) : null,
    });
  }
  return out;
}

/**
 * Parse the `PID|commandline` lines emitted by the PowerShell scan.
 * `CommandLine` is `null` for system-owned processes; we record the PID
 * without a project path so the caller can still do its PID-only fallback.
 * Port of Hub `parse_powershell_lines`.
 */
export function parsePowerShellLines(stdout: string): RunningUnity[] {
  const out: RunningUnity[] = [];
  for (const line of stdout.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (trimmed.length === 0) continue;
    const bar = trimmed.indexOf("|");
    if (bar < 0) continue;
    const pidStr = trimmed.slice(0, bar);
    const rest = trimmed.slice(bar + 1);
    const pid = Number.parseInt(pidStr, 10);
    if (!Number.isInteger(pid) || pid <= 0) continue;
    const projectPath =
      rest.length === 0 || rest === "null"
        ? null
        : parseProjectPathArg(splitArgsWindows(rest));
    out.push({
      pid,
      projectPath:
        projectPath !== null ? normalizePath(projectPath) : null,
    });
  }
  return out;
}

// ---------------------------------------------------------------------------
// Injectable OS scan (test seam).
//
// The three mutation sites (`bridge_status`, `LiveClient`, CLI `wait-for-ready`)
// must be unit-testable without spawning a real `ps` / PowerShell process. We
// expose a single `UnityProcessScanner` interface and a module-level mutable
// binding (`currentScanner`) that defaults to the real implementation. Tests
// call `setUnityProcessScannerForTest(fake)` to inject a stub; production code
// never touches it. This mirrors `hub-control.ts`'s `HubCliRunner` pattern.
// ---------------------------------------------------------------------------

/** Read-only Unity process scanner. Returns one record per live Unity editor. */
export interface UnityProcessScanner {
  scan(): RunningUnity[];
}

/** macOS `ps -axww -o pid=,command=` scanner. Port of Hub `scan_macos`. */
function scanMacos(): RunningUnity[] {
  let stdout: string;
  try {
    stdout = execFileSync("ps", ["-axww", "-o", "pid=,command="], {
      encoding: "utf8",
    });
  } catch {
    // ps missing or failed (non-macOS CI container without procps). Return
    // empty — the cold-Safe-Mode branch then falls through to "stopped", the
    // pre-feature behavior, so no regression.
    return [];
  }
  return parsePsOutput(stdout);
}

/** Windows PowerShell `Get-CimInstance Win32_Process` scanner. Port of Hub
 *  `scan_windows`. */
function scanWindows(): RunningUnity[] {
  const script =
    "Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | " +
    "ForEach-Object { Write-Output ($_.ProcessId.ToString() + '|' + $_.CommandLine) }";
  let stdout: string;
  try {
    stdout = execFileSync(
      "powershell",
      ["-NoProfile", "-NonInteractive", "-Command", script],
      { encoding: "utf8", windowsHide: true },
    );
  } catch {
    // PowerShell missing or failed. Return empty.
    return [];
  }
  return parsePowerShellLines(stdout);
}

/** Real scanner — dispatches to the OS-native command. Linux/other → empty. */
const realScanner: UnityProcessScanner = {
  scan(): RunningUnity[] {
    const platform = process.platform as UnityScanPlatform;
    if (platform === "darwin") return scanMacos();
    if (platform === "win32") return scanWindows();
    return []; // Linux + others: no regression (Hub matches).
  },
};

// Mutable binding so tests can swap in a fake without threading a dependency
// through every caller. Default is the real OS scanner.
let currentScanner: UnityProcessScanner = realScanner;

/** Read the active scanner. Callers (`findUnityForProject`) use this. */
export function getUnityProcessScanner(): UnityProcessScanner {
  return currentScanner;
}

/**
 * Install a fake scanner for tests. Returns a restore function that re-binds
 * the previous scanner (so concurrent test files don't leak state). Production
 * code MUST NOT call this.
 */
export function setUnityProcessScannerForTest(
  fake: UnityProcessScanner | null,
): () => void {
  const prev = currentScanner;
  currentScanner = fake ?? realScanner;
  return () => {
    currentScanner = prev;
  };
}

/**
 * Scan for a live Unity process whose command line references `projectPath`.
 *
 * Returns `{ pid }` for the first match (by PID), or null when no Unity
 * process matches. Path comparison uses {@link normalizePath} from
 * `instance-discovery.ts` (case-sensitive on macOS/Linux — `canonicalize`
 * would normalize away the very differences that distinguish two projects,
 * and the Hub does the same on its side).
 *
 * Also matches when Unity is running WITHOUT `-projectPath` (bare editor
 * launch). The Hub covers this via `lastLaunchPid`; the MCP server has no
 * persisted launch PID, so this is a known false negative we accept — the
 * scan simply returns null and the caller falls back to its pre-feature
 * behavior. Documented in tests.
 *
 * Never throws: any scanner failure (ps missing, PowerShell slow, parse
 * error) returns null. A failed scan must not mask the underlying offline
 * state.
 *
 * @param projectPath absolute Unity project root to match against.
 */
export function findUnityForProject(
  projectPath: string | null | undefined,
): { pid: number } | null {
  if (!projectPath) return null;
  const target = normalizePath(projectPath);
  if (target.length === 0) return null;
  let found: RunningUnity[] = [];
  try {
    found = getUnityProcessScanner().scan();
  } catch {
    return null;
  }
  for (const proc of found) {
    if (proc.projectPath !== null && normalizePath(proc.projectPath) === target) {
      return { pid: proc.pid };
    }
  }
  return null;
}
