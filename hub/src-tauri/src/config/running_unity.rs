//! Running-Unity auto-detection (M1.5-10).
//!
//! Scans the host OS for live Unity processes and returns a list of
//! `{pid, projectPath?}` records. The frontend Projects tab uses this to
//! tag the matching `projects.json` row with a `running` status chip
//! (see `hub/src/lib/tabs/ProjectsTab.svelte` and
//! `hub-ui.md` §"Status chips").
//!
//! ## Detection rules
//!
//! 1. **Process filter** — by executable name. macOS: `Unity` (the binary
//!    at `Unity.app/Contents/MacOS/Unity`). Windows: `Unity.exe`. Linux is
//!    out of scope; the scan returns an empty list on every other target.
//! 2. **Argument match** — each surviving process is parsed for
//!    `-projectPath "<path>"` (or the `=` form). The path is canonicalised
//!    to its parent so the project root matches `projects.json` rows
//!    without the trailing slash.
//! 3. **PID fallback** — a row whose `lastLaunchPid` matches a scanned
//!    PID is also tagged `running`, even if the `-projectPath` argument
//!    could not be parsed. This protects against future Unity command-line
//!    tweaks and against Windows quoting edge cases. The frontend owns the
//!    matching loop; the backend just returns the scanned set.
//!
//! ## Why shell out to `ps` / PowerShell?
//!
//! The task spec lists "`ps`, `tasklist`, or `sysinfo` crate" as
//! acceptable options. We use the OS-native commands to keep the binary
//! dependency surface small and to avoid the `sysinfo` crate's runtime
//! cost on the idle Hub. The scan is rate-limited from the frontend via
//! `settings.discovery.scanIntervalSeconds` (default 30s), so the cost of
//! spawning a short-lived process on each tick is negligible.
//!
//! ## Killing is intentionally not implemented here
//!
//! The scan is **read-only**. Terminating a process is `M1 Plan 3 Task 4`
//! (Kill Unity) and lives in `config::process::kill_unity` — see
//! `hub/src-tauri/src/config/process.rs`. Nothing in this module sends
//! signals.

use std::path::PathBuf;
use std::process::Command;

use serde::{Deserialize, Serialize};

/// One live Unity process discovered by the scanner. `project_path` is
/// `None` when the process command line could not be parsed for
/// `-projectPath` — the frontend still uses the PID for matching against
/// `projects.json` `lastLaunchPid`.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RunningUnity {
    pub pid: u32,
    pub project_path: Option<String>,
}

/// Pure `-projectPath` argument parser. Returns the project root path if
/// the flag is present, `None` otherwise. Accepts both `-projectPath
/// <value>` and `-projectPath=<value>` (and the long `--projectPath`
/// forms, matching `config::cli::parse_argv`). Surrounding double quotes
/// on the value are stripped.
///
/// The first match wins (matching the rest of the Hub CLI surface — see
/// `config::cli` for the rationale).
///
/// This function is the unit-testable core of the feature: the OS-specific
/// scanners in `scan_macos` / `scan_windows` simply extract argv slices
/// from `ps` / PowerShell output and feed them in.
pub fn parse_project_path_arg<I, S>(args: I) -> Option<String>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut iter = args.into_iter();
    while let Some(arg) = iter.next() {
        let arg = arg.as_ref();
        if let Some(value) = arg
            .strip_prefix("-projectPath=")
            .or_else(|| arg.strip_prefix("--projectPath="))
        {
            return Some(unquote(value));
        }
        if arg == "-projectPath" || arg == "--projectPath" {
            match iter.next() {
                Some(next) => return Some(unquote(next.as_ref())),
                // Flag at end of argv with no value: not a valid project
                // path; let the scanner fall back to PID-only matching.
                None => return None,
            }
        }
    }
    None
}

/// Strip a single layer of surrounding double or single quotes from a
/// flag value. Hub-launched Unity always passes the path unquoted on
/// macOS (the OS handles spaces natively) and quoted on Windows when the
/// path contains spaces. We accept both because the flag may have been
/// supplied by a third-party tool.
fn unquote(value: &str) -> String {
    let trimmed = value.trim();
    if trimmed.len() >= 2 {
        let bytes = trimmed.as_bytes();
        let first = bytes[0];
        let last = bytes[trimmed.len() - 1];
        if (first == b'"' && last == b'"') || (first == b'\'' && last == b'\'') {
            return trimmed[1..trimmed.len() - 1].to_string();
        }
    }
    trimmed.to_string()
}

/// Canonicalise a scanned path so it can be compared against
/// `ProjectEntry.path` without trailing-slash / symlink noise. Falls back
/// to the input on canonicalize failure (broken symlink, deleted
/// directory — the row will still match while Unity holds it open).
fn normalize_path(path: &str) -> String {
    let pb = PathBuf::from(path);
    match pb.canonicalize() {
        Ok(c) => c.to_string_lossy().to_string(),
        Err(_) => path.to_string(),
    }
}

/// Platform-dispatched scan of live Unity processes. Returns one
/// `RunningUnity` per running Unity editor on macOS / Windows; other
/// targets get an empty list. Shared by the async `scan_running_unity`
/// command (frontend poll) and the sync `launch_project` double-launch
/// guard. Kept as a plain sync fn so internal callers that already
/// hold a blocking context (e.g. inside another command body) can use
/// it directly.
pub fn detect_running_unity() -> Vec<RunningUnity> {
    #[cfg(target_os = "macos")]
    {
        scan_macos()
    }
    #[cfg(target_os = "windows")]
    {
        scan_windows()
    }
    #[cfg(not(any(target_os = "macos", target_os = "windows")))]
    {
        Vec::new()
    }
}

/// Public entry point for the frontend's 5-second running-Unity poll.
/// `async` + `spawn_blocking` so the underlying `ps` (macOS) /
/// PowerShell (Windows) subprocess — which can take hundreds of ms to
/// seconds on a busy machine or under a slow Windows cold-start —
/// never blocks the webview thread. The first poll fires immediately
/// on Projects-tab mount, so keeping it off the main thread is what
/// stops an extra freeze on top of the other launch-path commands.
#[tauri::command]
pub async fn scan_running_unity() -> Vec<RunningUnity> {
    tauri::async_runtime::spawn_blocking(detect_running_unity)
        .await
        .unwrap_or_default()
}

#[cfg(target_os = "macos")]
fn scan_macos() -> Vec<RunningUnity> {
    // `ps -axww -o pid=,command=` prints one line per process: the PID
    // (padded), then the full command with args. We deliberately use
    // `command=` (not `args=`) because on macOS `args=` includes the
    // re-exec'd `Contents/MacOS/Unity` text that we already see in
    // `command=`, and the field widths differ between BSD and GNU `ps`.
    // `-ww` widens the output so we never truncate long `-projectPath`
    // values. The leading "=" in the format spec suppresses the header.
    //
    // The `Unity` filter is applied after the fact so we can also inspect
    // the executable path: a bare `Unity` in the process list (e.g. a
    // user manually launching the editor) is still relevant.
    let output = match Command::new("ps")
        .arg("-axww")
        .arg("-o")
        .arg("pid=,command=")
        .output()
    {
        Ok(o) => o,
        Err(e) => {
            log::warn!("running_unity: ps failed: {}", e);
            return Vec::new();
        }
    };
    if !output.status.success() {
        log::warn!(
            "running_unity: ps exited with {:?}",
            output.status.code()
        );
        return Vec::new();
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    parse_ps_output(&stdout)
}

/// Shared parser for `ps` output. macOS `ps -axww -o pid=,command=` emits
/// one line per process with the PID in the first whitespace-delimited
/// token; the rest of the line is the full command. We accept any argv
/// that has a final path component whose basename is `Unity` (matching
/// both the macOS `Unity.app/Contents/MacOS/Unity` binary and a bare
/// `Unity` on PATH). The PID is parsed as decimal `u32`; lines with
/// non-numeric PIDs are skipped silently.
fn parse_ps_output(stdout: &str) -> Vec<RunningUnity> {
    let mut out = Vec::new();
    for line in stdout.lines() {
        let trimmed = line.trim_start();
        if trimmed.is_empty() {
            continue;
        }
        // Find the first whitespace; everything before is the PID.
        let (pid_str, rest) = match trimmed.split_once(char::is_whitespace) {
            Some(parts) => parts,
            None => continue,
        };
        let pid: u32 = match pid_str.parse() {
            Ok(p) => p,
            Err(_) => continue,
        };
        let rest = rest.trim_start();
        if !is_unity_command_line(rest) {
            continue;
        }
        let project_path = parse_project_path_arg(split_args(rest)).map(|p| normalize_path(&p));
        out.push(RunningUnity { pid, project_path });
    }
    out
}

/// `true` when the command line's executable basename is `Unity`
/// (case-sensitive — `unityhub://` URL handlers and the `Unity Hub` GUI
/// do not match). Windows is filtered by the PowerShell
/// `Name='Unity.exe'` selector upstream, so this helper is only used
/// for the macOS / `ps` path.
///
/// Extracting the executable path from a `ps` command line is harder
/// than it looks: the Hub GUI binary lives at
/// `/Applications/Unity Hub.app/Contents/MacOS/Unity Hub` — i.e. the
/// executable name itself contains a space, and the parent directory
/// contains the word "Unity". A naive `command_line.split_whitespace().next()`
/// would return `/Applications/Unity`, which basename-matches `Unity`
/// and (incorrectly) tags the Hub GUI as a running editor. We solve
/// this by extending the executable prefix past any tokens that don't
/// look like flags: the Unity editor is virtually always launched with
/// `-projectPath` (or some other `-flag`), so the first `-flag` token
/// is a reliable end-of-path marker. The Hub GUI's command line has
/// no such flag, so the helper consumes the entire line and the
/// basename (`Hub`) is correctly rejected.
fn is_unity_command_line(command_line: &str) -> bool {
    let executable = first_executable_path(command_line);
    let unquoted = executable.trim_matches(|c| c == '"' || c == '\'');
    let basename = match std::path::Path::new(unquoted).file_name() {
        Some(b) => b,
        None => return false,
    };
    basename == "Unity"
}

/// Extract the executable path prefix from a `ps` command line. If the
/// line starts with a `"`, consume the matching closing quote. Otherwise
/// extend the prefix through any tokens that don't start with `-`,
/// stopping at the first flag (or end of line).
fn first_executable_path(command_line: &str) -> &str {
    let bytes = command_line.as_bytes();
    if bytes.is_empty() {
        return command_line;
    }
    if bytes[0] == b'"' {
        if let Some(end) = command_line[1..].find('"') {
            return &command_line[..end + 2];
        }
    }
    let mut pos = 0usize;
    for token in command_line.split(char::is_whitespace) {
        if token.starts_with('-') && token != "--" {
            return command_line[..pos].trim_end();
        }
        pos += token.len() + 1;
    }
    command_line.trim_end()
}

/// Naive argv splitter for a `ps`-formatted command line. Honours double
/// and single quotes, and treats backslashes as literal characters (the
/// macOS `ps` output never escapes them, so a real `\` in a path would
/// round-trip unchanged). This is intentionally not a full shell parser
/// — the input is a single command line produced by the OS, not a
/// user-supplied string.
fn split_args(line: &str) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    let mut current = String::new();
    let mut in_single = false;
    let mut in_double = false;
    for ch in line.chars() {
        match ch {
            '\'' if !in_double => {
                in_single = !in_single;
            }
            '"' if !in_single => {
                in_double = !in_double;
            }
            c if c.is_whitespace() && !in_single && !in_double => {
                if !current.is_empty() {
                    out.push(std::mem::take(&mut current));
                }
            }
            c => current.push(c),
        }
    }
    if !current.is_empty() {
        out.push(current);
    }
    out
}

#[cfg(target_os = "windows")]
fn scan_windows() -> Vec<RunningUnity> {
    // `wmic process where "name='Unity.exe'" get ProcessId,CommandLine /format:list`
    // was the obvious tool but `wmic` is removed on Windows 11 24H2+. We use
    // PowerShell's `Get-CimInstance Win32_Process` which is the modern,
    // supported CIM path. We invoke `powershell.exe` with `-NoProfile` to
    // skip the user's profile scripts (faster, more hermetic) and pipe a
    // single line per process: `PID|commandline`. The frontend never sees
    // this intermediate format — it is consumed entirely by
    // `parse_powershell_lines` below.
    //
    // PowerShell is present on every supported Windows install by
    // default. If it is somehow missing, the scan returns an empty list
    // and the user keeps the previous `running` snapshot — the spec
    // explicitly excludes a kill path here, so a missed scan is a stale
    // chip at worst, never a stuck process.
    let script = "Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | ForEach-Object { Write-Output ($_.ProcessId.ToString() + '|' + $_.CommandLine) }";
    let output = match Command::new("powershell")
        .arg("-NoProfile")
        .arg("-NonInteractive")
        .arg("-Command")
        .arg(script)
        .output()
    {
        Ok(o) => o,
        Err(e) => {
            log::warn!("running_unity: powershell failed: {}", e);
            return Vec::new();
        }
    };
    if !output.status.success() {
        log::warn!(
            "running_unity: powershell exited with {:?}",
            output.status.code()
        );
        return Vec::new();
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    parse_powershell_lines(&stdout)
}

/// Parse the `PID|commandline` lines emitted by the PowerShell scan.
/// `CommandLine` is `null` for system-owned processes; we treat that as
/// "no project path" but still record the PID so the frontend can do its
/// PID-only fallback match.
/// Parse the `PID|commandline` lines emitted by the PowerShell scan.
/// `CommandLine` is `null` for system-owned processes; we treat that as
/// "no project path" but still record the PID so the frontend can do its
/// PID-only fallback match.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
fn parse_powershell_lines(stdout: &str) -> Vec<RunningUnity> {
    let mut out = Vec::new();
    for line in stdout.lines() {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        // Skip PowerShell noise (errors / verbose records land on stderr
        // but belt-and-braces: ignore non-`\d+|…` lines here too).
        let (pid_str, rest) = match line.split_once('|') {
            Some(parts) => parts,
            None => continue,
        };
        let pid: u32 = match pid_str.parse() {
            Ok(p) => p,
            Err(_) => continue,
        };
        // `null` or empty rest means we know the PID but not the args.
        let project_path = if rest.is_empty() || rest == "null" {
            None
        } else {
            parse_project_path_arg(split_args_windows(rest)).map(|p| normalize_path(&p))
        };
        out.push(RunningUnity { pid, project_path });
    }
    out
}

/// Windows argv splitter. Same rules as `split_args` but treats `/` and
/// `\` as ordinary characters (Windows paths use both). Honours `\"` as
/// an embedded literal quote inside a double-quoted string (matching the
/// `CommandLine` quoting convention emitted by `Get-CimInstance
/// Win32_Process`).
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
fn split_args_windows(line: &str) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    let mut current = String::new();
    let mut in_double = false;
    let mut chars = line.chars().peekable();
    while let Some(ch) = chars.next() {
        if in_double && ch == '\\' {
            if let Some(&next) = chars.peek() {
                if next == '"' || next == '\\' {
                    chars.next();
                    current.push(next);
                    continue;
                }
            }
        }
        match ch {
            '"' => {
                in_double = !in_double;
            }
            c if c.is_whitespace() && !in_double => {
                if !current.is_empty() {
                    out.push(std::mem::take(&mut current));
                }
            }
            c => current.push(c),
        }
    }
    if !current.is_empty() {
        out.push(current);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_project_path_arg_handles_separate_value() {
        let args = vec!["Unity", "-projectPath", "/Users/me/Projects/MyGame"];
        assert_eq!(
            parse_project_path_arg(args),
            Some("/Users/me/Projects/MyGame".to_string())
        );
    }

    #[test]
    fn parse_project_path_arg_handles_equals_form() {
        let args = vec!["Unity", "-projectPath=/Users/me/Projects/MyGame"];
        assert_eq!(
            parse_project_path_arg(args),
            Some("/Users/me/Projects/MyGame".to_string())
        );
    }

    #[test]
    fn parse_project_path_arg_handles_long_form() {
        let args = vec!["Unity", "--projectPath", "/p"];
        assert_eq!(
            parse_project_path_arg(args),
            Some("/p".to_string())
        );
    }

    #[test]
    fn parse_project_path_arg_handles_long_form_equals() {
        let args = vec!["Unity", "--projectPath=/p"];
        assert_eq!(parse_project_path_arg(args), Some("/p".to_string()));
    }

    #[test]
    fn parse_project_path_arg_strips_surrounding_double_quotes() {
        // The Hub-launched command on Windows uses `"…path with spaces…"`
        // because `CommandLine` round-trips through a single string.
        let args = vec!["Unity.exe", "-projectPath", "\"C:\\Users\\me\\My Game\""];
        assert_eq!(
            parse_project_path_arg(args),
            Some("C:\\Users\\me\\My Game".to_string())
        );
    }

    #[test]
    fn parse_project_path_arg_strips_surrounding_single_quotes() {
        let args = vec!["Unity", "-projectPath", "'/Users/me/Has Space'"];
        assert_eq!(
            parse_project_path_arg(args),
            Some("/Users/me/Has Space".to_string())
        );
    }

    #[test]
    fn parse_project_path_arg_returns_none_when_missing() {
        let args = vec!["Unity", "-batchmode", "-nographics"];
        assert_eq!(parse_project_path_arg(args), None);
    }

    #[test]
    fn parse_project_path_arg_returns_none_for_flag_at_end_without_value() {
        // `-projectPath` at the end of argv (no value) — same handling as
        // the CLI parser in `config::cli::parse_argv`: we surface "no
        // value" via the scanner returning None so the PID-only fallback
        // can still match.
        let args = vec!["Unity", "-batchmode", "-projectPath"];
        assert_eq!(parse_project_path_arg(args), None);
    }

    #[test]
    fn parse_project_path_arg_takes_first_match_only() {
        let args = vec![
            "Unity",
            "-projectPath",
            "/first",
            "-projectPath",
            "/second",
        ];
        assert_eq!(parse_project_path_arg(args), Some("/first".to_string()));
    }

    #[test]
    fn parse_project_path_arg_skips_preceding_flags() {
        // Hub-launched Windows command line commonly embeds `-batchmode`
        // or `-quit` before the project path; the parser must skip them
        // to find the flag.
        let args = vec![
            "Unity.exe",
            "-batchmode",
            "-nographics",
            "-projectPath",
            "/p",
            "-quit",
        ];
        assert_eq!(parse_project_path_arg(args), Some("/p".to_string()));
    }

    #[test]
    fn is_unity_command_line_matches_unity_app_binary() {
        let line = "/Applications/Unity/Hub/Editor/6000.0.1f1/Unity.app/Contents/MacOS/Unity -projectPath /p";
        assert!(is_unity_command_line(line));
    }

    #[test]
    fn is_unity_command_line_matches_bare_unity() {
        assert!(is_unity_command_line("Unity -projectPath /p"));
    }

    #[test]
    fn is_unity_command_line_rejects_unityhub() {
        // The Hub GUI and the `unityhub://` URL handler are separate
        // executables; they must never trigger a `running` chip on a
        // user project row.
        assert!(!is_unity_command_line("/Applications/Unity Hub.app/Contents/MacOS/Unity Hub -projectPath /p"));
    }

    #[test]
    fn is_unity_command_line_rejects_quoted_path_with_wrong_basename() {
        assert!(!is_unity_command_line("\"/Applications/Unity Helper\" -projectPath /p"));
    }

    #[test]
    fn split_args_handles_quoted_path() {
        let parts = split_args("Unity -projectPath \"/Users/me/Has Space\" -quit");
        assert_eq!(parts, vec!["Unity", "-projectPath", "/Users/me/Has Space", "-quit"]);
    }

    #[test]
    fn split_args_handles_unquoted_args() {
        let parts = split_args("Unity -projectPath /p -quit");
        assert_eq!(parts, vec!["Unity", "-projectPath", "/p", "-quit"]);
    }

    #[test]
    fn split_args_handles_empty() {
        assert!(split_args("").is_empty());
        assert!(split_args("   ").is_empty());
    }

    #[test]
    fn split_args_windows_handles_escaped_quote() {
        // PowerShell's CommandLine uses \" to embed a literal quote.
        let parts = split_args_windows("Unity.exe -projectPath \"C:\\Users\\me\\My \\\"Quoted\\\" Game\" -quit");
        assert_eq!(parts[0], "Unity.exe");
        assert_eq!(parts[1], "-projectPath");
        assert_eq!(parts[2], "C:\\Users\\me\\My \"Quoted\" Game");
        assert_eq!(parts[3], "-quit");
    }

    #[test]
    fn split_args_windows_handles_simple_quoted() {
        let parts = split_args_windows("Unity.exe -projectPath \"C:\\Users\\me\\My Game\" -quit");
        assert_eq!(
            parts,
            vec!["Unity.exe", "-projectPath", "C:\\Users\\me\\My Game", "-quit"]
        );
    }

    #[test]
    fn parse_ps_output_extracts_pid_and_path() {
        let sample = "\
        1 /sbin/launchd\n\
        42 /Applications/Unity/Hub/Editor/6000.0.1f1/Unity.app/Contents/MacOS/Unity -projectPath /Users/me/MyGame -batchmode\n\
        100 /Applications/Unity Hub.app/Contents/MacOS/Unity Hub\n\
        200 /usr/bin/some-tool -projectPath /not/unity\n";
        let found = parse_ps_output(sample);
        // Only the actual Unity binary should match; the Hub GUI and the
        // unrelated tool both fall through the executable-name filter.
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].pid, 42);
        assert_eq!(
            found[0].project_path.as_deref(),
            Some("/Users/me/MyGame")
        );
    }

    #[test]
    fn parse_ps_output_keeps_pid_when_path_unparseable() {
        // Unity launched without -projectPath (e.g. via the Hub's "Open
        // Editor" button). The scanner still records the PID so the
        // frontend can match via `lastLaunchPid` (M1.5-10 acceptance
        // checklist: "A row with `lastLaunchPid === scannedPid` is
        // `running` even if the `-projectPath` argument cannot be
        // parsed").
        let sample = "5 /Applications/Unity/Hub/Editor/6000.0.1f1/Unity.app/Contents/MacOS/Unity\n";
        let found = parse_ps_output(sample);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].pid, 5);
        assert!(found[0].project_path.is_none());
    }

    #[test]
    fn parse_ps_output_ignores_blank_and_unparseable_lines() {
        let sample = "\n   \nnot-a-pid /Applications/Unity/Unity\n";
        assert!(parse_ps_output(sample).is_empty());
    }

    #[test]
    fn parse_powershell_lines_extracts_pid_and_path() {
        let sample = "1234|Unity.exe -projectPath \"C:\\Users\\me\\My Game\" -quit\n";
        let found = parse_powershell_lines(sample);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].pid, 1234);
        // normalize_path falls back to the input when canonicalize fails
        // (the path does not exist on the test machine), so we compare
        // against the stripped, un-quoted form.
        let path = found[0].project_path.as_deref().unwrap();
        assert!(path.starts_with("C:\\Users\\me\\My Game"));
    }

    #[test]
    fn parse_powershell_lines_keeps_pid_when_null() {
        let sample = "9999|null\n";
        let found = parse_powershell_lines(sample);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].pid, 9999);
        assert!(found[0].project_path.is_none());
    }

    #[test]
    fn parse_powershell_lines_skips_blank_and_unparseable() {
        let sample = "\n   \nnot-a-pid|Unity.exe\n";
        assert!(parse_powershell_lines(sample).is_empty());
    }

    #[test]
    fn unquote_strips_matched_quotes_only() {
        assert_eq!(unquote("\"hello\""), "hello");
        assert_eq!(unquote("'hello'"), "hello");
        assert_eq!(unquote("hello"), "hello");
        assert_eq!(unquote("\"hello'"), "\"hello'");
    }
}
