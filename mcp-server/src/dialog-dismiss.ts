// M13 T4.5 + M23 Plan 2 — Unity startup dialog auto-dismissal.
//
// M13 T4.5 shipped the single highest-frequency case: the launch-errors /
// Safe Mode dialog, dismissed with a hard-coded "Ignore" click. M23 Plan 2
// extends this with UCP's 6-variant dialog policy taxonomy so different
// automation workflows can pick different buttons on the SAME dialog, and
// adds the three remaining startup modals (Non-Matching Editor, Project
// Upgrade Required, Auto Graphics API Notice).
//
// Design:
//   - Policy taxonomy: UNITY_OPEN_MCP_DIALOG_POLICY=auto|manual|ignore|
//     recover|safe-mode|cancel (default `ignore` — preserves T4.5). See
//     dialog-policy.ts for the pure tables.
//   - Per-kind-per-policy button preference tables (port of UCP
//     `preferred_dialog_button_label`).
//   - Project Upgrade is NEVER auto-confirmed unless the dedicated opt-in
//     UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE=1 is set; otherwise the probe
//     reports a `blocked` outcome + audit line (no click).
//   - Cross-platform: Windows (Win32/PowerShell), macOS (AppleScript),
//     Linux/X11 (xdotool). UCP's non-Windows dismiss is a no-op stub; we
//     follow Unity-MCP's working macOS/Linux base instead.
//
// No runtime deps beyond node builtins (mcp-server/AGENTS.md "no runtime
// deps beyond MCP SDK"): only child_process and os.

import { execFile, execFileSync } from "node:child_process";
import { platform as nodePlatform } from "node:os";
import {
  DIALOG_TITLE_FRAGMENTS,
  parseDialogPolicy,
  preferenceTokensForPolicy,
  genericFallbackTokens,
  blockedKindsForPolicy,
  type DialogPolicy,
  type DialogKind,
} from "./dialog-policy.js";

/**
 * Supported `process.platform` values for the dialog dismiss helper. Narrowed
 * alias of NodeJS.Platform — keeps the platform-dispatch table exhaustive in
 * tests without forcing callers to import a Node-internal type.
 */
export type DismissPlatform = "win32" | "darwin" | "linux";

/**
 * Outcome of a single dismiss attempt against the running OS desktop.
 *
 * `dismissed`: a dialog was found AND a click was dispatched successfully.
 *
 * `not-found`: no dismissable dialog was visible on this poll tick (either no
 * modal at all, or a known modal whose policy has no matching button).
 *
 * `blocked`: a dialog was found that the policy explicitly declines to click
 * (currently only project_upgrade without the opt-in). The caller logs an
 * audit line and keeps polling — the dialog is still up and may need a human.
 *
 * `error`: an unexpected platform error happened (a required tool was missing,
 * a syscall failed). The caller logs it once and continues with `not-found`
 * semantics — the dialog may simply not be open yet, and a single transient
 * error must not abort the whole launch flow.
 */
export type DismissOutcome =
  | { kind: "dismissed"; button: string; dialog: DialogKind }
  | { kind: "not-found" }
  | { kind: "blocked"; dialog: DialogKind; message: string }
  | { kind: "error"; message: string };

/**
 * Window-title fragments matched against the Unity launch-errors dialog.
 * Kept as the M13 T4.5 back-compat export (the launch-errors subset of
 * {@link DIALOG_TITLE_FRAGMENTS}); the generalized matcher now consults the
 * full per-kind table via {@link classifyDialogTitle}.
 *
 * Both legacy and current strings are listed so the matcher stays resilient
 * across Unity versions. The match is case-insensitive and substring-based.
 */
export const LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS: readonly string[] =
  DIALOG_TITLE_FRAGMENTS.launch_errors.map((f) =>
    // Restore the human-readable spelling T4.5 tests assert against ("Safe
    // Mode", "Compiler Errors", …). classifyDialogTitle normalizes both
    // directions, so this stays a faithful superset.
    f === "entersafemode"
      ? "Safe Mode"
      : f === "scripthavecompilererrors"
        ? "Scripts have compiler errors"
        : f === "compilererrors"
          ? "Compiler Errors"
          : f === "holdon"
            ? "Hold On"
            : f === "compileerrors"
              ? "Compile Errors"
              : f,
  );

/**
 * The button label this helper presses to dismiss the launch-errors dialog
 * under the default (`ignore`) policy. Preserved from M13 T4.5 for back-compat
 * with tests and any external references; the generalized path now selects
 * the button via {@link preferenceTokensForPolicy}.
 */
export const DISMISS_BUTTON_LABEL = "Ignore";

/**
 * Producer-side prefixes for error messages that callers treat as permanent
 * (the polling loop bails out instead of ticking again). Exported so the
 * bailout matcher references the SAME literal as the producers.
 */
export const LINUX_XDOTOOL_MISSING_PREFIX = "xdotool not found on PATH";
export const UNSUPPORTED_PLATFORM_PREFIX =
  "Unsupported platform for Unity startup-dialog auto-dismiss";

/** Options threaded through every platform probe. */
export interface DismissProbeOptions {
  platform: DismissPlatform;
  policy: DialogPolicy;
  allowProjectUpgrade: boolean;
}

/**
 * Try once to find and dismiss a Unity startup dialog on the current OS
 * desktop. Pure-ish — performs OS calls and returns; never blocks past the
 * underlying syscall's own timeout.
 *
 * Library-safe: never throws (errors are returned in the `DismissOutcome`
 * union), never writes to stdout/stderr, never mutates global state.
 *
 * Platform-dispatched:
 * - **Windows**: Win32 (`EnumWindows` / `EnumChildWindows` / `GetWindowTextW`
 *   / `SendMessageW(BM_CLICK)`) via PowerShell — no native node-gyp dep.
 * - **macOS**: AppleScript via `osascript`. Clicks a named button per the
 *   policy token table (requires Accessibility perm once).
 * - **Linux/X11**: `xdotool`. Wayland is unsupported — the error message
 *   calls this out so the user does not waste time debugging.
 */
export async function tryDismissDialog(
  opts: DismissProbeOptions,
): Promise<DismissOutcome> {
  switch (opts.platform) {
    case "win32":
      return tryDismissWindows(opts);
    case "darwin":
      return tryDismissMacOS(opts);
    case "linux":
      return tryDismissLinuxX11(opts);
    default:
      return {
        kind: "error",
        message: `${UNSUPPORTED_PLATFORM_PREFIX}: ${opts.platform as string}`,
      };
  }
}

// ---------------------------------------------------------------------------
// Token-table serialization (shared by all three platform scripts)
// ---------------------------------------------------------------------------

/**
 * Serialize the per-kind token table + blocked-kinds set into a compact JSON
 * blob embedded in the platform scripts. The script classifies each candidate
 * window title (after normalizing it the same way {@link normalizeDialogLabel}
 * does) and either clicks the first matching token or reports `blocked`.
 *
 * Shape (kept narrow so the PowerShell / AppleScript parsers stay simple):
 *   {
 *     kinds: {
 *       "<kind>": { fragments: string[], tokens: string[] | null }
 *     },
 *     blocked: ["<kind>", ...],
 *     genericTokens: string[]   // for unknown titles
 *   }
 *
 * `tokens: null` means "this policy declines this kind" (manual, or a kind
 * the policy has no safe button for). The script reports `blocked` for kinds
 * in the `blocked` list and `not-found` for `tokens: null` kinds that are not
 * blocked.
 */
function buildTokenTable(opts: DismissProbeOptions): {
  kinds: Record<string, { fragments: string[]; tokens: string[] | null }>;
  blocked: string[];
  genericTokens: string[];
} {
  const kinds: Record<string, { fragments: string[]; tokens: string[] | null }> = {};
  for (const kind of Object.keys(DIALOG_TITLE_FRAGMENTS) as DialogKind[]) {
    const tokens = preferenceTokensForPolicy(
      kind,
      opts.policy,
      opts.allowProjectUpgrade,
    );
    kinds[kind] = {
      fragments: [...DIALOG_TITLE_FRAGMENTS[kind]],
      tokens: tokens === null ? null : [...tokens],
    };
  }
  return {
    kinds,
    blocked: [...blockedKindsForPolicy(opts.policy, opts.allowProjectUpgrade)],
    genericTokens: [...genericFallbackTokens(opts.policy)],
  };
}

// ---------------------------------------------------------------------------
// Windows — Win32 via PowerShell
// ---------------------------------------------------------------------------

/**
 * The PowerShell payload that probes for any Unity startup dialog and clicks
 * the policy-selected button. Exported as a string (not a function) so tests
 * can assert the script shape without launching PowerShell.
 *
 * Strategy:
 *   1. P/Invoke `EnumWindows` to enumerate every visible top-level window
 *      owned by `Unity.exe`.
 *   2. For each, normalize the title (alphanumeric lowercase) and match it
 *      against the embedded per-kind fragment table to classify it.
 *   3. If the kind is in the blocked list → emit `blocked:<kind>` and skip
 *      the click. If the kind's token list is null → skip (not-found).
 *   4. Walk child windows with `EnumChildWindows`, find a Button whose
 *      normalized text contains one of the policy tokens (first match wins),
 *      and send `BM_CLICK` (0x00F5). Preferred over a synthesised mouse event
 *      — works even if the user is mid-click in another app, no focus steal.
 *   5. Unknown titles fall back to the generic per-policy token list.
 *
 * The script writes a single-token result to stdout (last non-empty line):
 *   - `dismissed:<button>:<kind>` on success
 *   - `blocked:<kind>` when a dialog was found but the policy declines
 *   - `not-found` when no dismissable dialog was matched
 *   - `error:<message>` on an unexpected exception
 *
 * The token table is passed via stdin (one JSON blob) so the script body
 * stays constant and testable; embedding it inline would bloat the assertion.
 */
export const WINDOWS_DISMISS_PS_SCRIPT = `
$ErrorActionPreference = 'Stop'
try {
  if (-not ([System.Management.Automation.PSTypeName]'UnityOpenMcp.Dialogs.Dismisser').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace UnityOpenMcp.Dialogs {
  public static class Dismisser {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    const uint BM_CLICK = 0x00F5;
    static string Norm(string s) {
      var sb = new StringBuilder(s.Length);
      foreach (var ch in s) {
        if ((ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) {
          sb.Append(char.ToLowerInvariant(ch));
        }
      }
      return sb.ToString();
    }
    public static string TryDismiss(int[] unityPids, string tableJson) {
      var unityPidSet = new HashSet<uint>();
      for (int i = 0; i < unityPids.Length; i++) unityPidSet.Add((uint)unityPids[i]);
      var table = System.Text.Json.JsonSerializer.Deserialize<Types.Table>(tableJson);
      var candidates = new List<IntPtr>();
      var sb = new StringBuilder(512);
      EnumWindows((hWnd, lParam) => {
        if (!IsWindowVisible(hWnd)) return true;
        uint procId; GetWindowThreadProcessId(hWnd, out procId);
        if (!unityPidSet.Contains(procId)) return true;
        sb.Length = 0;
        GetWindowTextW(hWnd, sb, sb.Capacity);
        candidates.Add(hWnd);
        return true;
      }, IntPtr.Zero);
      foreach (var hWnd in candidates) {
        sb.Length = 0;
        GetWindowTextW(hWnd, sb, sb.Capacity);
        var title = sb.ToString();
        var norm = Norm(title);
        string kind = null;
        foreach (var kv in table.kinds) {
          foreach (var frag in kv.Value.fragments) {
            if (norm.Contains(frag)) { kind = kv.Key; break; }
          }
          if (kind != null) break;
        }
        string[] tokens = null;
        if (kind != null) {
          if (table.blocked.Contains(kind)) return "blocked:" + kind;
          if (!table.kinds[kind].tokensSpecified) continue;
          tokens = table.kinds[kind].tokens;
        } else {
          if (table.genericTokens.Length == 0) continue;
          tokens = table.genericTokens;
        }
        IntPtr matchedButton = IntPtr.Zero;
        string matchedText = null;
        EnumChildWindows(hWnd, (hChild, lParam) => {
          sb.Length = 0;
          GetClassNameW(hChild, sb, sb.Capacity);
          if (sb.ToString() != "Button") return true;
          sb.Length = 0;
          GetWindowTextW(hChild, sb, sb.Capacity);
          var text = sb.ToString();
          var textNorm = Norm(text);
          foreach (var token in tokens) {
            if (textNorm.Contains(token)) { matchedButton = hChild; matchedText = text; return false; }
          }
          return true;
        }, IntPtr.Zero);
        if (matchedButton == IntPtr.Zero) continue;
        SendMessageW(matchedButton, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        return "dismissed:" + matchedText + ":" + (kind ?? "unknown");
      }
      return "not-found";
    }
  }
}
"@
  }
  $unityPids = @(Get-Process -Name 'Unity' -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.Id })
  if ($unityPids.Count -eq 0) { Write-Output 'not-found'; return }
  $tableJson = [Console]::In.ReadToEnd();
  Write-Output ([UnityOpenMcp.Dialogs.Dismisser]::TryDismiss([int[]]$unityPids, $tableJson))
} catch {
  Write-Output ('error:' + $_.Exception.Message)
}
`;

// JSON shape the PowerShell script deserializes. System.Text.Json requires
// parameterless ctors + settable props; the `tokensSpecified` flag lets the
// C# distinguish "null tokens" (decline) from "empty tokens".
// (Kept in a comment because the C# lives inside the PS string above; this
// documents the contract for the table-builder + tests.)

async function tryDismissWindows(
  opts: DismissProbeOptions,
): Promise<DismissOutcome> {
  const table = buildTokenTable(opts);
  // The C# uses System.Text.Json which serializes `null` arrays as missing
  // properties unless we mark them nullable. Mirror the C# contract: emit
  // `tokens: []` for null-token (decline) kinds so tokensSpecified=false
  // maps cleanly. The blocked list already covers the genuine declines.
  const payload = JSON.stringify({
    kinds: Object.fromEntries(
      Object.entries(table.kinds).map(([k, v]) => [
        k,
        { fragments: v.fragments, tokens: v.tokens ?? [] },
      ]),
    ),
    blocked: table.blocked,
    genericTokens: table.genericTokens,
  });
  return new Promise<DismissOutcome>((resolve) => {
    const child = execFile(
      "powershell",
      ["-NoProfile", "-NonInteractive", "-Command", WINDOWS_DISMISS_PS_SCRIPT],
      { timeout: 5000, windowsHide: true },
      (err, stdout) => {
        if (err) {
          resolve({ kind: "error", message: err.message });
          return;
        }
        resolve(parseDismissOutput(stdout));
      },
    );
    // Pipe the token table via stdin.
    if (child.stdin) {
      child.stdin.end(payload);
    }
  });
}

// ---------------------------------------------------------------------------
// macOS — AppleScript via osascript
// ---------------------------------------------------------------------------

/**
 * The AppleScript template used for the macOS dismiss path. Exposed as a
 * function of the token table so tests can assert the script shape without
 * launching osascript.
 *
 * Strategy: iterate every window of the Unity process, normalize its title,
 * classify it against the table, and click the first button whose normalized
 * label contains a policy token. If the kind is blocked, emit `blocked:<kind>`
 * without clicking. If Unity is not running OR no matching button exists, the
 * script reports `not-found`. Any AppleScript exception (e.g. Accessibility
 * permission not granted) is reported as `error:<msg>`.
 *
 * Requires the Terminal / `node` binary to have been granted Accessibility
 * permission in System Settings → Privacy & Security → Accessibility.
 *
 * The token table is JSON-escaped into the script header.
 */
export function macosDismissAppleScript(opts: DismissProbeOptions): string {
  // The macOS path cannot do per-button selection as precisely as the Windows
  // path (AppleScript's named-button click works but is brittle across
  // Unity versions); instead it classifies the window title and presses
  // Return to click the FOCUSED (default) button — which under the default /
  // auto / ignore / recover policies IS the safe choice (Ignore on
  // launch-errors, Continue on non-matching-editor, OK on auto-graphics-api).
  // Project Upgrade is detected and reported as `blocked` (never Return-clicked
  // without the explicit opt-in). The token table from buildTokenTable is
  // consulted only to decide whether the policy declines a kind entirely
  // (manual / safe-mode on a kind with no safe button → no click, not-found);
  // when it declines, the script returns not-found for that kind.
  const launchFrags = DIALOG_TITLE_FRAGMENTS.launch_errors;
  const nonMatchFrags = DIALOG_TITLE_FRAGMENTS.non_matching_editor;
  const graphicsFrags = DIALOG_TITLE_FRAGMENTS.auto_graphics_api;
  // Whether the active policy dismisses each safe kind at all. Under manual,
  // or safe-mode on non_matching/auto_graphics (no safe button), the loop
  // should NOT click — return not-found so polling continues.
  const dismissesLaunch = preferenceTokensForPolicy("launch_errors", opts.policy, opts.allowProjectUpgrade) !== null;
  const dismissesNonMatch = preferenceTokensForPolicy("non_matching_editor", opts.policy, opts.allowProjectUpgrade) !== null;
  const dismissesGraphics = preferenceTokensForPolicy("auto_graphics_api", opts.policy, opts.allowProjectUpgrade) !== null;
  return `
on run
  try
    tell application "System Events"
      if not (exists process "Unity") then
        return "not-found"
      end if
      tell process "Unity"
        repeat with w in windows
          try
            set wt to (title of w) as text
          on error
            set wt to ""
          end try
          -- Project Upgrade: NEVER click without the explicit opt-in, even
          -- though the focused button is usually Confirm. Report blocked.
          if (wt contains "Upgrade") and (wt contains "Project") then
            return "blocked:" & "project_upgrade"
          end if
          ${launchFrags.map((f) => fragmentCheck(f, "launch_errors", dismissesLaunch)).join("\n          ")}
          ${nonMatchFrags.map((f) => fragmentCheck(f, "non_matching_editor", dismissesNonMatch)).join("\n          ")}
          ${graphicsFrags.map((f) => fragmentCheck(f, "auto_graphics_api", dismissesGraphics)).join("\n          ")}
        end repeat
      end tell
    end tell
    return "not-found"
  on error errMsg
    return "error:" & errMsg
  end try
end run
`;
}

/**
 * Build the per-fragment AppleScript check block. When the policy dismisses
 * the kind, the block activates the window + presses Return and returns the
 * dismissed token; when the policy declines the kind (null tokens), the block
 * is omitted entirely (the loop falls through to the next fragment).
 *
 * AppleScript `contains` is case-insensitive by default, so the lowercased
 * fragment matches Unity's mixed-case titles ("Enter Safe Mode?" contains
 * "entersafemode" → false, BUT "safemode" matches; the fragment list is built
 * to have at least one case-insensitive hit per title).
 */
function fragmentCheck(
  fragment: string,
  kind: DialogKind,
  dismisses: boolean,
): string {
  if (!dismisses) return `-- policy declines ${kind}; skip`;
  return `if wt contains "${fragment}" then
            set frontmost to true
            key code 36
            return "dismissed:Focus:${kind}"
          end if`;
}

/**
 * Kept for M13 T4.5 back-compat — the original launch-errors-only AppleScript
 * constant some external tests reference. Delegates to the generalized
 * template under the default policy.
 * @deprecated Use {@link macosDismissAppleScript} instead.
 */
export const MACOS_DISMISS_APPLESCRIPT: string = macosDismissAppleScript({
  platform: "darwin",
  policy: "ignore",
  allowProjectUpgrade: false,
});

async function tryDismissMacOS(
  opts: DismissProbeOptions,
): Promise<DismissOutcome> {
  return new Promise<DismissOutcome>((resolve) => {
    execFile(
      "osascript",
      ["-e", macosDismissAppleScript(opts)],
      { timeout: 5000 },
      (err, stdout) => {
        if (err) {
          resolve({ kind: "error", message: err.message });
          return;
        }
        resolve(parseDismissOutput(stdout));
      },
    );
  });
}

// ---------------------------------------------------------------------------
// Linux/X11 — xdotool
// ---------------------------------------------------------------------------

/**
 * Whether `xdotool` is on PATH. Cached per process so the polling loop pays
 * the lookup cost at most once.
 */
let xdotoolPresence: boolean | undefined;

function isXdotoolAvailable(): boolean {
  if (xdotoolPresence !== undefined) return xdotoolPresence;
  try {
    execFileSync("xdotool", ["--version"], {
      stdio: "ignore",
      timeout: 2000,
    });
    xdotoolPresence = true;
  } catch {
    xdotoolPresence = false;
  }
  return xdotoolPresence;
}

/**
 * Reset the cached `xdotool` presence flag. Test-only — production code
 * never needs to call this.
 */
export function _resetXdotoolPresenceForTests(): void {
  xdotoolPresence = undefined;
}

/**
 * Escape a literal string for safe use in `xdotool search --name`. The
 * argument is interpreted as a regex; without escaping, a future fragment
 * containing metacharacters would silently change the match semantics.
 */
export function regexEscapeForXdotool(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Look up currently-running Unity Editor PIDs on the local box. Used by the
 * Linux/X11 path to scope the title-fragment match to Unity processes only.
 * Returns an empty array on any failure. Pure / idempotent / never throws.
 */
function getUnityPidsLinux(): readonly number[] {
  try {
    const stdout = execFileSync("pgrep", ["-x", "Unity"], {
      stdio: ["ignore", "pipe", "ignore"],
      timeout: 1000,
      encoding: "utf8",
    });
    return stdout
      .split(/\r?\n/)
      .map((s) => parseInt(s.trim(), 10))
      .filter((n) => Number.isFinite(n) && n > 0);
  } catch {
    return [];
  }
}

async function tryDismissLinuxX11(
  opts: DismissProbeOptions,
): Promise<DismissOutcome> {
  if (!isXdotoolAvailable()) {
    return {
      kind: "error",
      message: `${LINUX_XDOTOOL_MISSING_PREFIX}. Install it (e.g. \`sudo apt-get install xdotool\`) to enable Unity startup-dialog auto-dismiss on Linux/X11. Wayland is not yet supported.`,
    };
  }
  const unityPids = new Set(getUnityPidsLinux());
  if (unityPids.size === 0) {
    return { kind: "not-found" };
  }
  // Classify by title fragment: walk the per-kind fragment table, find the
  // first kind whose xdotool search surfaces a Unity-owned window.
  //   - blocked kinds (project_upgrade without opt-in) → report blocked.
  //   - decline kinds (tokens === null) → skip (treat as not-found).
  //   - otherwise activate the window + send Return to click the focused
  //     (default) button. Under default/auto/ignore/recover the default
  //     button is the safe choice for launch_errors / non_matching_editor /
  //     auto_graphics_api. Project upgrade is never clicked here (blocked).
  const blocked = new Set<string>(blockedKindsForPolicy(opts.policy, opts.allowProjectUpgrade));
  const kinds = Object.keys(DIALOG_TITLE_FRAGMENTS) as DialogKind[];
  // Prefer launch_errors first (the common stall), then the others.
  const order: DialogKind[] = [
    "launch_errors",
    "non_matching_editor",
    "auto_graphics_api",
    "project_upgrade",
  ];
  void kinds;
  return new Promise<DismissOutcome>((resolve) => {
    let idx = 0;
    const tryNextKind = (): void => {
      if (idx >= order.length) {
        resolve({ kind: "not-found" });
        return;
      }
      const kind = order[idx++];
      const tokens = preferenceTokensForPolicy(kind, opts.policy, opts.allowProjectUpgrade);
      const fragments = DIALOG_TITLE_FRAGMENTS[kind];
      // Probe this kind's fragments one at a time.
      let fIdx = 0;
      const tryNextFragment = (): void => {
        if (fIdx >= fragments.length) {
          tryNextKind();
          return;
        }
        const fragment = fragments[fIdx++];
        execFile(
          "xdotool",
          ["search", "--name", regexEscapeForXdotool(fragment)],
          { timeout: 2000 },
          (err, stdout) => {
            if (err || !stdout.trim()) {
              tryNextFragment();
              return;
            }
            const candidateIds = stdout.trim().split(/\s+/).filter(Boolean);
            findUnityOwnedWindow(candidateIds, unityPids, (winId) => {
              if (!winId) {
                tryNextFragment();
                return;
              }
              if (blocked.has(kind) || tokens === null) {
                // Found the dialog but the policy declines. Report blocked
                // only for genuinely blocked kinds (project_upgrade); a null-
                // token decline (manual / safe-mode on a kind with no safe
                // button) is reported as not-found so the loop keeps ticking.
                if (blocked.has(kind)) {
                  resolve({
                    kind: "blocked",
                    dialog: kind,
                    message: `Policy '${opts.policy}' declines to dismiss ${kind} dialog`,
                  });
                  return;
                }
                tryNextKind();
                return;
              }
              execFile(
                "xdotool",
                [
                  "windowactivate",
                  "--sync",
                  winId,
                  "key",
                  "--clearmodifiers",
                  "Return",
                ],
                { timeout: 2000 },
                (activateErr) => {
                  if (activateErr) {
                    resolve({
                      kind: "error",
                      message: `xdotool failed to dismiss window ${winId}: ${activateErr.message}`,
                    });
                    return;
                  }
                  resolve({ kind: "dismissed", button: "Focus", dialog: kind });
                },
              );
            });
          },
        );
      };
      tryNextFragment();
    };
    tryNextKind();
  });
}

/**
 * Walk `candidateIds` and call `done(winId)` with the first window owned by
 * a Unity PID, or `done(undefined)` if none match. Sequential — the list is
 * small and parallelism buys nothing meaningful here.
 */
function findUnityOwnedWindow(
  candidateIds: string[],
  unityPids: ReadonlySet<number>,
  done: (winId: string | undefined) => void,
): void {
  let idx = 0;
  const next = (): void => {
    if (idx >= candidateIds.length) {
      done(undefined);
      return;
    }
    const winId = candidateIds[idx++];
    execFile(
      "xdotool",
      ["getwindowpid", winId],
      { timeout: 1000 },
      (err, stdout) => {
        if (err) {
          next();
          return;
        }
        const pid = parseInt(stdout.trim(), 10);
        if (Number.isFinite(pid) && unityPids.has(pid)) {
          done(winId);
          return;
        }
        next();
      },
    );
  };
  next();
}

// ---------------------------------------------------------------------------
// Shared parser
// ---------------------------------------------------------------------------

/**
 * Parse the single-token contract every platform-specific dispatcher writes
 * to stdout. Inspects the LAST non-empty line of stdout, not the whole
 * buffer: a stray PowerShell warning, `osascript` deprecation notice, or
 * `xdotool` chatter printed before the contract token must not misclassify
 * the result as `not-found`.
 *
 * Contract:
 *   - `dismissed:<button>:<kind>` → `{ kind: "dismissed", button, dialog }`
 *   - `dismissed:<button>`        → back-compat (T4.5) → launch_errors
 *   - `blocked:<kind>`            → `{ kind: "blocked", dialog, message }`
 *   - `not-found`                 → `{ kind: "not-found" }`
 *   - `error:<message>`           → `{ kind: "error", message }`
 *   - any other / empty           → `{ kind: "not-found" }` (defensive — a
 *     transient parse miss is treated as "not yet visible" rather than abort)
 */
export function parseDismissOutput(stdout: string): DismissOutcome {
  const lines = stdout
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l.length > 0);
  if (lines.length === 0) return { kind: "not-found" };
  const last = lines[lines.length - 1];
  if (last === "not-found") return { kind: "not-found" };
  if (last.startsWith("dismissed:")) {
    const rest = last.substring("dismissed:".length);
    const parts = rest.split(":");
    if (parts.length >= 2) {
      // dismissed:<button>:<kind>
      const dialog = parts[parts.length - 1] as DialogKind;
      const button = parts.slice(0, -1).join(":") || DISMISS_BUTTON_LABEL;
      return {
        kind: "dismissed",
        button,
        dialog: isDialogKind(dialog) ? dialog : "launch_errors",
      };
    }
    // Back-compat: T4.5 single-field form `dismissed:<button>`.
    const button = rest || DISMISS_BUTTON_LABEL;
    return { kind: "dismissed", button, dialog: "launch_errors" };
  }
  if (last.startsWith("blocked:")) {
    const dialog = last.substring("blocked:".length) as DialogKind;
    return {
      kind: "blocked",
      dialog: isDialogKind(dialog) ? dialog : "project_upgrade",
      message: `Policy declined to dismiss ${dialog} dialog`,
    };
  }
  if (last.startsWith("error:")) {
    return { kind: "error", message: last.substring("error:".length) };
  }
  return { kind: "not-found" };
}

function isDialogKind(s: string): s is DialogKind {
  return (
    s === "launch_errors" ||
    s === "non_matching_editor" ||
    s === "project_upgrade" ||
    s === "auto_graphics_api"
  );
}

// ===========================================================================
// Polling loop + config
// ===========================================================================

/** Default dismiss probe timeout (30s overall budget). */
export const DEFAULT_DISMISS_TIMEOUT_MS = 30_000;
/** Default dismiss probe poll interval (1.5s tick). */
export const DEFAULT_DISMISS_INTERVAL_MS = 1_500;

/**
 * Substring markers that identify a `kind: "error"` outcome as permanent for
 * this run. When seen, the polling loop bails out after recording the error
 * once; ticking again would just respawn the same doomed tool. Keep this list
 * narrow — only conditions that cannot self-heal mid-launch belong here.
 */
const PERMANENT_DISMISS_ERROR_MARKERS: readonly string[] = [
  LINUX_XDOTOOL_MISSING_PREFIX,
  UNSUPPORTED_PLATFORM_PREFIX,
];

function isPermanentDismissError(message: string): boolean {
  return PERMANENT_DISMISS_ERROR_MARKERS.some((m) => message.includes(m));
}

/**
 * Resolve the dismiss-feature config from the environment.
 *
 *   - `UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1` disables the feature
 *     entirely (preserves the pre-T4.5 baseline — no OS clicks). Kept as the
 *     hard kill-switch for back-compat.
 *   - `UNITY_OPEN_MCP_DIALOG_POLICY=auto|manual|ignore|recover|safe-mode|cancel`
 *     selects which button to click per dialog kind (default `ignore`).
 *     `manual` is equivalent to the kill-switch for the polling loop (no
 *     clicks) but is reported distinctly in the config so callers can tell
 *     "operator opted out of all dialogs" from "operator turned the feature
 *     off entirely".
 *   - `UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE=1` opts in to auto-confirming the
 *     Project Upgrade Required dialog (irreversible — off by default).
 *   - `UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS` overrides the overall timeout.
 *   - `UNITY_OPEN_MCP_DISMISS_INTERVAL_MS` overrides the poll interval.
 *
 * `enabled` is true only when the kill-switch is unset AND policy != manual.
 * Pure / no I/O. Exposed for tests.
 */
export interface DismissConfig {
  enabled: boolean;
  timeoutMs: number;
  intervalMs: number;
  policy: DialogPolicy;
  allowProjectUpgrade: boolean;
}

export function readDismissConfig(
  env: NodeJS.ProcessEnv = process.env,
): DismissConfig {
  const policy = parseDialogPolicy(env);
  const killSwitchOff = env.UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS === "1";
  return {
    enabled: !killSwitchOff && policy !== "manual",
    timeoutMs: parsePositiveInt(
      env.UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS,
      DEFAULT_DISMISS_TIMEOUT_MS,
    ),
    intervalMs: parsePositiveInt(
      env.UNITY_OPEN_MCP_DISMISS_INTERVAL_MS,
      DEFAULT_DISMISS_INTERVAL_MS,
    ),
    policy,
    allowProjectUpgrade: env.UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE === "1",
  };
}

function parsePositiveInt(raw: string | undefined, fallback: number): number {
  if (raw === undefined || raw === "") return fallback;
  const n = parseInt(raw, 10);
  if (!Number.isFinite(n) || n <= 0) return fallback;
  return n;
}

/**
 * Sink for per-dismissal audit logging. Default writes to stderr (the MCP
 * server's stdio transport owns stdout; logs must go to stderr). Abstracted
 * so tests can capture dismiss events without intercepting process.stderr.
 */
export type DismissLog = (line: string) => void;

function defaultDismissLog(line: string): void {
  console.error(line);
}

export interface PollAndDismissOptions {
  timeoutMs: number;
  intervalMs: number;
  policy: DialogPolicy;
  allowProjectUpgrade: boolean;
  /**
   * Override the platform — exposed for tests so the polling logic can be
   * exercised across all three OS branches without hopping process.platform.
   * Defaults to the running platform.
   */
  platform?: DismissPlatform;
  /**
   * Override the dismiss probe — exposed for tests so the loop can be
   * exercised without invoking PowerShell / osascript / xdotool. Defaults
   * to `tryDismissDialog`.
   */
  probe?: typeof tryDismissDialog;
  /**
   * When fired, the loop exits immediately. Intended for callers that have
   * an authoritative readiness signal in scope (e.g. the parallel compile
   * wait in LiveClient). The moment the bridge is reachable + idle, there
   * is no startup dialog left to dismiss, so the loop stops.
   */
  abortSignal?: AbortSignal;
  /** Per-dismissal audit sink. Defaults to stderr. */
  log?: DismissLog;
}

/**
 * Poll the OS desktop for any Unity startup dialog and click the policy-
 * selected button every time the probe reports it as found. Returns once the
 * abort signal has fired (authoritative readiness), the overall timeout has
 * elapsed, or a permanent platform error has been observed.
 *
 * Re-entrant dismissals are supported: when a dialog re-appears after a
 * successful click (resolver-fix → dialog-reappears cycle), each subsequent
 * dismissal emits its own log line so the recurrence shows in the audit log.
 *
 * `blocked` outcomes (project_upgrade without opt-in) are logged once per
 * occurrence and the loop keeps polling — a human still needs to dismiss that
 * dialog, and the loop should not exit before the readiness abort fires.
 *
 * Never throws. Transient platform errors are logged once then suppressed;
 * permanent errors bail out after the first occurrence so the helper does
 * not spawn doomed tool invocations on a fixed budget.
 */
export async function pollAndDismissDialogs(
  opts: PollAndDismissOptions,
): Promise<void> {
  const platform =
    opts.platform ?? (nodePlatform() as DismissPlatform);
  const probe =
    opts.probe ??
    ((o: DismissProbeOptions) => tryDismissDialog(o));
  const log = opts.log ?? defaultDismissLog;
  const deadline = Date.now() + Math.max(0, opts.timeoutMs);
  const interval = Math.max(50, opts.intervalMs);
  const seenErrorMessages = new Set<string>();
  const seenBlocked = new Set<DialogKind>();
  let aborted = opts.abortSignal?.aborted ?? false;
  const onAbort = (): void => {
    aborted = true;
  };
  opts.abortSignal?.addEventListener("abort", onAbort, { once: true });
  try {
    while (Date.now() < deadline && !aborted) {
      const outcome = await probe({
        platform,
        policy: opts.policy,
        allowProjectUpgrade: opts.allowProjectUpgrade,
      });
      // Re-check exit conditions BEFORE applying the outcome: if abort fired
      // (or the deadline lapsed) while `probe` was in flight, the in-flight
      // result is stale and applying it would emit a misleading log.
      if (aborted || Date.now() >= deadline) break;
      if (outcome.kind === "dismissed") {
        log(
          `[unity-open-mcp] dismissed Unity ${outcome.dialog} dialog ` +
            `(button=${outcome.button}, policy=${opts.policy}, platform=${platform})`,
        );
        // Keep polling — the dialog may re-appear after the resolver fixes
        // one error and surfaces the next.
      } else if (outcome.kind === "blocked") {
        // Project-upgrade (or future blocked kind): log once per kind per
        // run, then keep polling. A human must dismiss it; exiting would
        // hide the stall behind a clean-looking readiness abort.
        if (!seenBlocked.has(outcome.dialog)) {
          seenBlocked.add(outcome.dialog);
          log(
            `[unity-open-mcp] dialog auto-dismiss blocked on ${outcome.dialog} ` +
              `(policy=${opts.policy}): ${outcome.message}. ` +
              (outcome.dialog === "project_upgrade"
                ? "Set UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE=1 to opt in to auto-confirm (irreversible)."
                : "Dismiss it manually or change UNITY_OPEN_MCP_DIALOG_POLICY."),
          );
        }
      } else if (outcome.kind === "error") {
        if (!seenErrorMessages.has(outcome.message)) {
          seenErrorMessages.add(outcome.message);
          log(
            `[unity-open-mcp] startup-dialog auto-dismiss: ${outcome.message}`,
          );
        }
        if (isPermanentDismissError(outcome.message)) {
          // No point ticking again — bail out.
          return;
        }
      }
      const remaining = deadline - Date.now();
      if (remaining <= 0 || aborted) break;
      await sleepMs(Math.min(interval, remaining));
    }
  } finally {
    opts.abortSignal?.removeEventListener("abort", onAbort);
  }
}

function sleepMs(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
