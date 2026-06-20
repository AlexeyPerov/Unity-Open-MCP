use std::collections::HashMap;
use std::path::{Path, PathBuf};

const GIT_HEAD_FILE: &str = ".git/HEAD";

/// Read the current branch name for a project, if the project lives inside a
/// git working tree. Returns:
///
/// * `Some(short_name)` — a normal branch, e.g. `feature/frecency`. Stored
///   in `projects.json` as the human-friendly short name.
/// * `Some("detached:<full-ref>")` — detached HEAD. The full ref is returned
///   in the value so the UI can show it on hover (the cell shows the short
///   branch name; the chip title carries the full ref). Detached HEAD is
///   detected by reading `ref: ` in `.git/HEAD` (a normal branch is always
///   written as `ref: refs/heads/<name>`).
/// * `None` — the project is not a git working tree, or `.git/HEAD` cannot
///   be read (corrupt repo, permissions, etc.). The Projects tab renders an
///   empty cell for these and never errors out.
///
/// Detection is intentionally minimal: a single `read_to_string` on
/// `.git/HEAD`. We do not invoke `git`, do not walk up the directory tree
/// looking for a parent repo, and do not touch `packed-refs` — those are
/// deferred. The cell renders the resolved value; refresh re-reads it.
pub fn read_git_branch(project_path: &Path) -> Option<String> {
    let head_path = project_path.join(GIT_HEAD_FILE);
    let content = std::fs::read_to_string(&head_path).ok()?;
    parse_git_head(&content)
}

/// Pure parser for the contents of a `.git/HEAD` file. Exposed so unit
/// tests can exercise the ref-resolution logic without touching the
/// filesystem. The rules are:
///
/// * Trim trailing whitespace/newlines (`.git/HEAD` is normally one line).
/// * If the line starts with `ref: `, the remainder is the ref
///   (typically `refs/heads/<branch>`). Strip the `refs/heads/` prefix so
///   the caller stores the short branch name.
/// * If the line is a 40-character hex SHA (detached HEAD), return
///   `detached:<sha>` so the UI can show "detached" with the SHA on hover.
/// * Anything else (corrupt file, pack file pointer, etc.) returns `None`.
pub(crate) fn parse_git_head(content: &str) -> Option<String> {
    let line = content.lines().next()?.trim();
    if line.is_empty() {
        return None;
    }
    if let Some(rest) = line.strip_prefix("ref: ") {
        let rest = rest.trim();
        if rest.is_empty() {
            return None;
        }
        if let Some(short) = rest.strip_prefix("refs/heads/") {
            if short.is_empty() {
                return None;
            }
            return Some(short.to_string());
        }
        // Non-standard ref (e.g. `refs/tags/x`, `refs/remotes/origin/x`).
        // Surface the full ref so the UI can render it; this is a rare
        // edge case (worktrees, weird local layouts) and the safest
        // behavior is to keep whatever was on disk.
        return Some(rest.to_string());
    }
    // Detached HEAD: git writes the raw 40-char SHA. Anything that
    // doesn't look like a SHA is treated as "not a normal branch" and
    // returns None so the UI renders an empty cell instead of a
    // misleading chip.
    if is_detached_sha(line) {
        Some(format!("detached:{}", line))
    } else {
        None
    }
}

fn is_detached_sha(s: &str) -> bool {
    s.len() == 40 && s.chars().all(|c| c.is_ascii_hexdigit())
}

/// Bulk-read branches for a list of project paths. Returns a map keyed by
/// the original input path so the frontend can correlate results back to
/// the project entry. The frontend is expected to call this in the
/// background (per spec) and update `gitBranch` on the in-memory store
/// only — we never persist on this path; the persisted `gitBranch` is
/// refreshed by `refresh_all_projects`.
///
/// Sync inner helper kept so unit tests exercise the per-project read
/// without a Tauri runtime; the command below offloads it to the
/// blocking thread pool.
fn read_git_branches(paths: Vec<String>) -> HashMap<String, Option<String>> {
    let mut result = HashMap::with_capacity(paths.len());
    for path in paths {
        let p = PathBuf::from(&path);
        let value = read_git_branch(&p);
        result.insert(path, value);
    }
    result
}

/// `paths` → branch name (if any) keyed by input path. `async` +
/// `spawn_blocking` so a `.git/HEAD` read against a path on a slow /
/// networked volume does not stall the webview thread at launch.
#[tauri::command]
pub async fn get_git_branches(
    paths: Vec<String>,
) -> HashMap<String, Option<String>> {
    let count = paths.len();
    let start = std::time::Instant::now();
    let result = tauri::async_runtime::spawn_blocking(move || read_git_branches(paths))
        .await
        .unwrap_or_default();
    log::info!(
        "get_git_branches: {} paths in {}ms",
        count,
        start.elapsed().as_millis()
    );
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fresh_dir(name: &str) -> PathBuf {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(name);
        std::fs::create_dir_all(&path).unwrap();
        // Leak the tempdir; tests are short-lived and we want the path
        // to remain valid through every assertion in this scope.
        std::mem::forget(dir);
        path
    }

    fn write_head(project: &Path, body: &str) {
        std::fs::create_dir_all(project.join(".git")).unwrap();
        std::fs::write(project.join(".git/HEAD"), body).unwrap();
    }

    #[test]
    fn read_git_branch_returns_short_name_for_normal_branch() {
        let project = fresh_dir("ProjA");
        write_head(&project, "ref: refs/heads/main\n");
        assert_eq!(read_git_branch(&project), Some("main".to_string()));
    }

    #[test]
    fn read_git_branch_handles_branch_with_slash() {
        let project = fresh_dir("ProjB");
        write_head(&project, "ref: refs/heads/feature/frecency\n");
        assert_eq!(
            read_git_branch(&project),
            Some("feature/frecency".to_string())
        );
    }

    #[test]
    fn read_git_branch_handles_no_trailing_newline() {
        let project = fresh_dir("ProjC");
        write_head(&project, "ref: refs/heads/develop");
        assert_eq!(read_git_branch(&project), Some("develop".to_string()));
    }

    #[test]
    fn read_git_branch_returns_detached_marker_for_raw_sha() {
        let project = fresh_dir("ProjD");
        let sha = "0123456789abcdef0123456789abcdef01234567";
        write_head(&project, format!("{sha}\n").as_str());
        assert_eq!(
            read_git_branch(&project),
            Some(format!("detached:{sha}"))
        );
    }

    #[test]
    fn read_git_branch_returns_none_when_no_git_dir() {
        let project = fresh_dir("ProjE");
        assert_eq!(read_git_branch(&project), None);
    }

    #[test]
    fn read_git_branch_returns_none_when_git_dir_but_no_head() {
        let project = fresh_dir("ProjF");
        std::fs::create_dir_all(project.join(".git")).unwrap();
        assert_eq!(read_git_branch(&project), None);
    }

    #[test]
    fn read_git_branch_returns_none_for_empty_head() {
        let project = fresh_dir("ProjG");
        write_head(&project, "");
        assert_eq!(read_git_branch(&project), None);
    }

    #[test]
    fn read_git_branch_returns_none_for_garbage() {
        let project = fresh_dir("ProjH");
        write_head(&project, "not-a-real-ref\n");
        assert_eq!(read_git_branch(&project), None);
    }

    #[test]
    fn read_git_branch_preserves_non_standard_ref() {
        // Worktrees and weird local layouts can land here. Better to
        // surface the full ref than to silently drop it.
        let project = fresh_dir("ProjI");
        write_head(&project, "ref: refs/tags/v1.2.3\n");
        assert_eq!(
            read_git_branch(&project),
            Some("refs/tags/v1.2.3".to_string())
        );
    }

    #[test]
    fn get_git_branches_returns_map_keyed_by_input_path() {
        let project_a = fresh_dir("ProjJ");
        let project_b = fresh_dir("ProjK");
        write_head(&project_a, "ref: refs/heads/main\n");
        // project_b has no git dir → None

        let paths = vec![
            project_a.to_string_lossy().to_string(),
            project_b.to_string_lossy().to_string(),
        ];
        let result = read_git_branches(paths.clone());
        assert_eq!(result.len(), 2);
        assert_eq!(
            result.get(&paths[0]).cloned().flatten(),
            Some("main".to_string())
        );
        assert_eq!(result.get(&paths[1]), Some(&None));
    }

    #[test]
    fn parse_git_head_strips_crlf_line_endings() {
        // Some Windows git clients write CRLF; the parser must tolerate
        // the trailing \r when stripping prefixes.
        assert_eq!(
            parse_git_head("ref: refs/heads/main\r\n"),
            Some("main".to_string())
        );
    }

    #[test]
    fn parse_git_head_short_sha_is_not_treated_as_detached() {
        // Anything other than 40 hex chars is not a SHA and we don't
        // want to surface misleading `detached:` chips.
        assert_eq!(parse_git_head("deadbeef\n"), None);
    }
}
