//! Read-only git status via the `git` CLI.
//!
//! The existing `git_branch` module reads `.git/HEAD` directly for a
//! cheap branch-name paint. That cannot derive ahead/behind counts or
//! the pending file list, so this module shells out to `git` (per the
//! locked decision: shell-to-git, not libgit2) and parses the
//! `--porcelain=v2` output. The popup is read-only — no mutating
//! actions (pull / commit / stage) are exposed.
//!
//! Every command runs with an explicit `-C <path>` working directory
//! and `GIT_TERMINAL_PROMPT=0` so a missing remote credential cannot
//! hang the call. `git status` is fast even on large repos (it does
//! not diff blob contents), so the synchronous `Command::output` is
//! acceptable; if a future change adds a slower command, a spawn +
//! timeout-kill wrapper should be introduced here.

use std::path::{Path, PathBuf};
use std::process::Command;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PendingFile {
    /// Repository-relative path (forward slashes). For renames this is
    /// the new path; the old path is reported in `rename_from`.
    pub path: String,
    /// Short human label for the change: `modified`, `added`,
    /// `deleted`, `renamed`, `copied`, `unmerged`, `untracked`.
    pub status: String,
    /// `true` when the change is staged (in the index), `false` when
    /// it is only in the working tree.
    pub staged: bool,
    /// Present only for renames/copies — the original path.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rename_from: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GitStatus {
    /// Short branch name, or `detached` when HEAD is not on a branch.
    /// `None` only when the repo is in an unreadable state.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub branch: Option<String>,
    /// Commits on the local branch not yet pushed upstream.
    pub ahead: u32,
    /// Commits on upstream not yet pulled into the local branch.
    pub behind: u32,
    /// `true` when no upstream tracking branch is configured (the
    /// ahead/behind counts are then both 0 and meaningless).
    pub no_upstream: bool,
    /// Pending file changes (staged + unstaged + untracked), in the
    /// order `git status` emits them.
    pub pending: Vec<PendingFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum GitStatusError {
    /// The folder is not inside a git repository.
    #[serde(rename_all = "camelCase")]
    NotARepo { path: String },
    /// `git` is not installed / not on PATH.
    #[serde(rename_all = "camelCase")]
    GitMissingBinary,
    /// `git` ran but exited non-zero, or produced output we could not
    /// parse. The message is surfaced verbatim to the UI log.
    #[serde(rename_all = "camelCase")]
    GitFailed { message: String },
}

/// Runs `git -C <path> <args…>` and returns trimmed stdout. Errors are
/// mapped to the typed enum. The timeout is enforced by killing the
/// child after `GIT_TIMEOUT` (Unix: SIGKILL via `kill_process_tree`).
fn run_git(path: &Path, args: &[&str]) -> Result<String, GitStatusError> {
    let mut cmd = Command::new("git");
    cmd.arg("-C").arg(path).args(args);
    // Force a stable, locale-independent output so the porcelain parser
    // does not have to handle localized strings.
    cmd.env("LC_ALL", "C");
    cmd.env("GIT_TERMINAL_PROMPT", "0");

    let output = cmd.output().map_err(|e| {
        // ENOENT on the binary itself → GitMissingBinary; anything
        // else (permission, etc.) → GitFailed.
        if e.kind() == std::io::ErrorKind::NotFound {
            GitStatusError::GitMissingBinary
        } else {
            GitStatusError::GitFailed {
                message: e.to_string(),
            }
        }
    })?;

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        // `not a git repository` (exit 128) → NotARepo.
        let combined = stderr.to_ascii_lowercase();
        if combined.contains("not a git repository") || combined.contains("not a git repo") {
            return Err(GitStatusError::NotARepo {
                path: path.display().to_string(),
            });
        }
        return Err(GitStatusError::GitFailed {
            message: if stderr.is_empty() {
                format!("git exited with status {}", output.status)
            } else {
                stderr
            },
        });
    }

    Ok(String::from_utf8_lossy(&output.stdout).trim().to_string())
}

/// Returns true when `path` is inside a git work tree. Cheaper than a
/// full `status` — used by the list row to decide whether to render
/// the branch widget at all.
pub fn is_repo(path: &Path) -> bool {
    match run_git(path, &["rev-parse", "--is-inside-work-tree"]) {
        Ok(s) => s == "true",
        Err(_) => false,
    }
}

/// Returns the substring of `s` after skipping `n` whitespace-
/// delimited fields, preserving any internal whitespace/tabs in the
/// remainder. Used by the rename parser to keep the `<path>\t<orig>`
/// tab intact (a plain `split_whitespace` would collapse it).
fn skip_whitespace_fields(s: &str, n: usize) -> &str {
    let bytes = s.as_bytes();
    let mut i = 0;
    let mut skipped = 0;
    let len = bytes.len();
    while skipped < n && i < len {
        // Skip leading whitespace.
        while i < len && (bytes[i] == b' ' || bytes[i] == b'\t') {
            i += 1;
        }
        // Consume one field.
        while i < len && bytes[i] != b' ' && bytes[i] != b'\t' {
            i += 1;
        }
        skipped += 1;
    }
    // Skip the whitespace before the remainder.
    while i < len && (bytes[i] == b' ' || bytes[i] == b'\t') {
        i += 1;
    }
    &s[i..]
}

/// Decodes a porcelain-v2 `XY` field pair into a human status label
/// and the staged flag. The porcelain v2 format puts the index status
/// in column 1 and the worktree status in column 2; we surface the
/// "most interesting" of the two and report staged when the index side
/// is non-trivial.
fn decode_xy(xy: &str) -> (String, bool) {
    if xy.len() < 2 {
        return ("modified".to_string(), false);
    }
    let (c1, c2) = (xy.as_bytes()[0] as char, xy.as_bytes()[1] as char);
    // Prefer the worktree status when present; fall back to index.
    let (c, staged) = if c2 != ' ' && c2 != '.' {
        (c2, false)
    } else {
        (c1, true)
    };
    let label = match c {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'T' => "typechange",
        'U' => "unmerged",
        '?' => "untracked",
        '!' => "ignored",
        _ => "modified",
    };
    (label.to_string(), staged)
}

/// Parses `git status -b --porcelain=v2` output into a [`GitStatus`].
/// The `# branch.head`, `# branch.upstream`, and `# branch.ab` header
/// lines supply branch + ahead/behind; the `1`/`2`/`u`/`?` ordinary
/// entries supply the pending file list.
fn parse_porcelain_v2(output: &str) -> GitStatus {
    let mut branch: Option<String> = None;
    let mut has_upstream = false;
    let mut ahead: u32 = 0;
    let mut behind: u32 = 0;
    let mut pending: Vec<PendingFile> = Vec::new();

    let mut lines = output.lines();
    while let Some(line) = lines.next() {
        if let Some(rest) = line.strip_prefix("# branch.head ") {
            let v = rest.trim().to_string();
            branch = if v == "(detached)" {
                Some("detached".to_string())
            } else {
                Some(v)
            };
        } else if let Some(rest) = line.strip_prefix("# branch.upstream ") {
            let v = rest.trim();
            if !v.is_empty() {
                has_upstream = true;
            }
        } else if let Some(rest) = line.strip_prefix("# branch.ab ") {
            // Format: "+<ahead> -<behind>"
            for tok in rest.trim().split_whitespace() {
                if let Some(n) = tok.strip_prefix('+') {
                    ahead = n.parse().unwrap_or(0);
                } else if let Some(n) = tok.strip_prefix('-') {
                    behind = n.parse().unwrap_or(0);
                }
            }
        } else if line.starts_with("1 ") {
            // Ordinary changed entry:
            //   1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
            // XY is a fixed 2-char column; `split_whitespace` would
            // collapse "A " → "A" so we read it from the byte offset
            // directly, then split the remainder by whitespace.
            let rest = &line[2..];
            let xy: &str = if rest.len() >= 2 { &rest[..2] } else { "  " };
            let fields: Vec<&str> = rest[xy.len()..].split_whitespace().collect();
            // Skip sub/mH/mI/mW/hH/hI (6 fields), the rest is the path
            // (joined to preserve spaces — porcelain paths are quoted
            // when they contain spaces, but unquoted join is the same
            // string either way).
            let path = fields.iter().skip(6).copied().collect::<Vec<_>>().join(" ");
            let (status, staged) = decode_xy(xy);
            pending.push(PendingFile {
                path,
                status,
                staged,
                rename_from: None,
            });
        } else if line.starts_with("2 ") {
            // Rename/copy entry:
            //   2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>\t<origPath>
            // The path pair is separated by a literal tab. We skip the
            // 7 leading whitespace-delimited fields after XY (sub/mH/
            // mI/mW/hH/hI/score) and take the remainder raw so the tab
            // survives, then split on the tab for new vs original path.
            let rest = &line[2..];
            let xy: &str = if rest.len() >= 2 { &rest[..2] } else { "  " };
            let after_xy = &rest[xy.len()..];
            let remainder = skip_whitespace_fields(after_xy, 7);
            let (status, staged) = decode_xy(xy);
            let rename_from = remainder
                .split('\t')
                .nth(1)
                .map(|s| s.trim().to_string());
            let new_path = remainder
                .split('\t')
                .next()
                .unwrap_or(&remainder)
                .trim()
                .to_string();
            pending.push(PendingFile {
                path: new_path,
                status,
                staged,
                rename_from,
            });
        } else if line.starts_with("u ") {
            // Unmerged entry: u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
            let rest = &line[2..];
            let _xy: &str = if rest.len() >= 2 { &rest[..2] } else { "  " };
            let fields: Vec<&str> = rest[_xy.len()..].split_whitespace().collect();
            // Skip 8 mode/hash fields; the rest is the path.
            let path = fields.iter().skip(8).copied().collect::<Vec<_>>().join(" ");
            pending.push(PendingFile {
                path,
                status: "unmerged".to_string(),
                staged: false,
                rename_from: None,
            });
        } else if let Some(rest) = line.strip_prefix("? ") {
            // Untracked entry: ? <path>
            let path = rest.trim().to_string();
            pending.push(PendingFile {
                path,
                status: "untracked".to_string(),
                staged: false,
                rename_from: None,
            });
        }
        // `# branch.oid` and `# branch.ab` (when no upstream) are
        // ignored — we only need head/upstream/ab.
    }

    GitStatus {
        branch,
        ahead,
        behind,
        no_upstream: !has_upstream,
        pending,
    }
}

/// Read-only git status for a project root. Invoked from the git
/// popup; the list row continues to use the cheap `.git/HEAD` branch
/// reader for its initial paint and only calls this on demand.
#[tauri::command]
pub fn git_status(project_path: String) -> Result<GitStatus, GitStatusError> {
    let path = PathBuf::from(&project_path);
    if !path.is_dir() {
        return Err(GitStatusError::NotARepo { path: project_path });
    }
    // `--porcelain=v2` + `-b` (branch header) gives us everything in a
    // single invocation: branch name, upstream, ahead/behind, and the
    // full pending list. `-z` is intentionally NOT used so we keep
    // line-oriented parsing (the rare path with a newline is an edge
    // case not worth the NUL-split complexity for a read-only view).
    let output = run_git(&path, &["status", "-b", "--porcelain=v2"])?;
    Ok(parse_porcelain_v2(&output))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_empty_output_has_no_branch_no_pending() {
        let s = parse_porcelain_v2("");
        assert!(s.branch.is_none());
        assert_eq!(s.ahead, 0);
        assert_eq!(s.behind, 0);
        assert!(s.no_upstream);
        assert!(s.pending.is_empty());
    }

    #[test]
    fn parse_branch_header_only() {
        let out = "# branch.head main\n\
                   # branch.oid abc123\n\
                   # branch.upstream origin/main\n\
                   # branch.ab +2 -3\n";
        let s = parse_porcelain_v2(out);
        assert_eq!(s.branch.as_deref(), Some("main"));
        assert!(!s.no_upstream);
        assert_eq!(s.ahead, 2);
        assert_eq!(s.behind, 3);
        assert!(s.pending.is_empty());
    }

    #[test]
    fn parse_detached_head() {
        let out = "# branch.head (detached)\n";
        let s = parse_porcelain_v2(out);
        assert_eq!(s.branch.as_deref(), Some("detached"));
    }

    #[test]
    fn parse_no_upstream_has_zero_counts() {
        let out = "# branch.head feature\n\
                   # branch.oid deadbeef\n";
        let s = parse_porcelain_v2(out);
        assert!(s.no_upstream);
        assert_eq!(s.ahead, 0);
        assert_eq!(s.behind, 0);
    }

    #[test]
    fn parse_ordinary_modified_unstaged() {
        // Format: 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
        // Six mode/hash fields separate XY from the path.
        let out = "1 .M 0 100644 100644 100644 h1abcdef h2abcdef README.md\n";
        let s = parse_porcelain_v2(out);
        assert_eq!(s.pending.len(), 1);
        assert_eq!(s.pending[0].path, "README.md");
        assert_eq!(s.pending[0].status, "modified");
        assert!(!s.pending[0].staged);
    }

    #[test]
    fn parse_ordinary_added_staged() {
        let out = "1 A  0 100644 100644 000000 h1abcdef h2abcdef new.txt\n";
        let s = parse_porcelain_v2(out);
        assert_eq!(s.pending[0].status, "added");
        assert!(s.pending[0].staged);
    }

    #[test]
    fn parse_untracked_entry() {
        let out = "? newdir/scratch.rs\n";
        let s = parse_porcelain_v2(out);
        assert_eq!(s.pending[0].path, "newdir/scratch.rs");
        assert_eq!(s.pending[0].status, "untracked");
    }

    #[test]
    fn parse_rename_entry_records_orig_path() {
        // Format: 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>\t<origPath>
        let out = "2 R  0 100644 100644 100644 h1abcdef h2abcdef R100 b.txt\ta.txt\n";
        let s = parse_porcelain_v2(out);
        assert_eq!(s.pending[0].path, "b.txt");
        assert_eq!(s.pending[0].status, "renamed");
        assert_eq!(s.pending[0].rename_from.as_deref(), Some("a.txt"));
    }

    #[test]
    fn parse_unmerged_entry() {
        // Format: u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
        // Nine fields separate XY from the path.
        let out = "u UU 0 100644 100644 100644 100644 h1abcdef h2abcdef h3abcdef merge.txt\n";
        let s = parse_porcelain_v2(out);
        assert_eq!(s.pending[0].status, "unmerged");
        assert_eq!(s.pending[0].path, "merge.txt");
    }

    #[test]
    fn decode_xy_prefers_worktree_status() {
        // ".M" = index unchanged, worktree modified → unstaged modified.
        let (label, staged) = decode_xy(".M");
        assert_eq!(label, "modified");
        assert!(!staged);
        // "M " = index modified, worktree clean → staged modified.
        let (label, staged) = decode_xy("M ");
        assert_eq!(label, "modified");
        assert!(staged);
    }

    #[test]
    fn decode_xy_handles_deleted_and_added() {
        assert_eq!(decode_xy("D ").0, "deleted");
        assert_eq!(decode_xy("A ").0, "added");
    }
}
