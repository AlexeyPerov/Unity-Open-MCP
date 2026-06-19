//! Line counter — a direct port of the LineWalker algorithm
//! (`AI-toolbox/LineWalker/internal/lineswalker/walker.go`).
//!
//! Counts newline bytes in files whose extension is in the code
//! allowlist, prunes dot-dirs and the standard dependency/build output
//! folders, and optionally respects a root `.gitignore`. The output
//! mirrors LineWalker's four-section report so it can be appended to
//! the app logs verbatim.
//!
//! Two entry points:
//!   - [`scan`] / [`scan_with_options`] — the full result for the
//!     manual "Run line count" button (returns the per-file breakdown
//!     + the rendered report).
//!   - [`sum_total_bytes`] — a cheap size probe used by the git-popup
//!     auto-calc path to decide whether to run the full count (the
//!     threshold lives in app settings, default 30 MiB).

use std::fs;
use std::io::Read;
use std::path::Path;

use serde::{Deserialize, Serialize};

/// Code extensions counted by the LineWalker allowlist. Kept in sync
/// with `DefaultCodeExtensions` in the Go original so line counts
/// match across the two tools. Markdown / JSON / YAML / TOML are
/// intentionally excluded (they are data/docs, not source).
///
/// Lookups are case-insensitive (extensions are lowercased before the
/// check), matching the Go behaviour.
pub fn is_counted_extension(ext: &str) -> bool {
    matches!(
        ext.to_ascii_lowercase().as_str(),
        "c"
            | "cc"
            | "cpp"
            | "cxx"
            | "h"
            | "hpp"
            | "hh"
            | "go"
            | "rs"
            | "py"
            | "pyw"
            | "js"
            | "mjs"
            | "cjs"
            | "ts"
            | "tsx"
            | "jsx"
            | "java"
            | "kt"
            | "kts"
            | "swift"
            | "rb"
            | "php"
            | "cs"
            | "sql"
            | "sh"
            | "bash"
            | "zsh"
            | "ps1"
            | "html"
            | "htm"
            | "css"
            | "scss"
            | "sass"
            | "less"
            | "vue"
            | "svelte"
            | "mdx"
            | "scala"
            | "clj"
            | "cljs"
            | "ex"
            | "exs"
            | "erl"
            | "hrl"
            | "dart"
            | "lua"
            | "r"
            | "pl"
            | "pm"
            | "vim"
            | "cls"
            | "zig"
            | "nim"
            | "ml"
            | "mli"
            | "fs"
            | "fsi"
            | "asm"
            | "s"
            | "proto"
            | "graphql"
            | "gql"
    )
}

/// Directory basenames that are pruned unconditionally (their subtree
/// is never walked). Mirrors LineWalker's `skipDirBasenames`.
fn is_pruned_dir(basename: &str) -> bool {
    matches!(
        basename,
        "node_modules" | "vendor" | "dist" | "build" | "target" | "__pycache__"
    )
}

/// Dot-directories (`.git`, `.idea`, `.vscode`, …) are pruned
/// unconditionally — LineWalker skips any basename starting with `.`.
fn is_dot_dir(basename: &str) -> bool {
    basename.starts_with('.') && basename != "."
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScanOptions {
    /// When true, paths matched by the root `<root>/.gitignore` are
    /// skipped. Only the root file is consulted (matching LineWalker);
    /// nested `.gitignore` files are ignored. Defaults to `false`.
    #[serde(default)]
    pub use_gitignore: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CodeFile {
    pub rel_path: String,
    pub ext: String,
    pub lines: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IgnoredFile {
    pub rel_path: String,
    pub reason: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScanResult {
    pub total_lines: u64,
    pub code_files: Vec<CodeFile>,
    pub ignored_files: Vec<IgnoredFile>,
    pub skipped_dirs: Vec<String>,
    pub read_errors: Vec<String>,
}

impl ScanResult {
    /// Renders the four-section report identical to LineWalker's CLI
    /// output, suitable for appending to the app logs. Sections with
    /// no entries are still emitted (with a header only) so the log
    /// shape is stable.
    pub fn render_report(&self) -> String {
        let mut out = String::new();
        out.push_str("Code files (path, extension, lines):\n");
        for f in &self.code_files {
            out.push_str(&format!("{}\t{}\t{}\n", f.rel_path, f.ext, f.lines));
        }
        out.push_str("\nIgnored (not counted):\n");
        for f in &self.ignored_files {
            out.push_str(&format!("{}\t({})\n", f.rel_path, f.reason));
        }
        out.push_str("\nSkipped directories (subtree not walked):\n");
        for d in &self.skipped_dirs {
            out.push_str(&format!("{}\n", d));
        }
        out.push_str(&format!("\nTotal lines: {}\n", self.total_lines));
        out
    }
}

/// Counts newline bytes in `path` using a 64 KiB buffer. A file
/// without a trailing newline still counts its last line (newline-
/// semantics, matching `wc -l` + 1 for the missing terminator —
/// actually `wc -l` counts the byte; LineWalker counts the byte and
/// the report matches `wc -l` for newline-terminated files). Errors
/// are surfaced via the result's `read_errors` list by the caller.
fn count_lines_in_file(path: &Path) -> std::io::Result<u64> {
    let mut f = fs::File::open(path)?;
    let mut buf = [0u8; 64 * 1024];
    let mut n: u64 = 0;
    loop {
        let read = f.read(&mut buf)?;
        if read == 0 {
            break;
        }
        for &b in &buf[..read] {
            if b == b'\n' {
                n += 1;
            }
        }
    }
    Ok(n)
}

/// Returns the extension (lowercased, without the leading dot) or an
/// empty string when there is no extension. Matches LineWalker's
/// `strings.ToLower(strings.TrimPrefix(filepath.Ext(path), "."))`.
fn extension_of(path: &Path) -> String {
    path.extension()
        .map(|e| e.to_string_lossy().to_ascii_lowercase())
        .unwrap_or_default()
}

/// A minimal gitignore matcher. We do not pull in the `ignore` crate
/// (kept the dependency footprint small); instead we implement just
/// enough of the syntax to cover the common patterns a project root
/// `.gitignore` uses: exact paths, `dir/` suffix, `*` globs, leading
/// `/` anchoring, and `!` negation. Comment (`#`) and blank lines are
/// skipped. Patterns are matched against the relative path (forward
/// slashes) from the root; directory patterns also match any path
/// inside them.
///
/// This deliberately mirrors the *subset* of gitignore that the Go
/// `go-gitignore` library (LineWalker's dependency) implements for
/// the patterns real projects actually use; full gitignore compliance
/// (character classes, double-`**`, etc.) is out of scope for a line
/// counter.
struct GitignoreMatcher {
    patterns: Vec<GitignorePattern>,
}

struct GitignorePattern {
    /// The pattern with any leading `!` stripped.
    body: String,
    /// True for negation (`!`) patterns.
    negate: bool,
    /// True when the body ends with `/` (directory-only).
    dir_only: bool,
    /// True when the body starts with `/` (anchored to root).
    anchored: bool,
}

impl GitignoreMatcher {
    fn load(root: &Path) -> Option<Self> {
        let gi = root.join(".gitignore");
        let content = fs::read_to_string(&gi).ok()?;
        let mut patterns = Vec::new();
        for raw in content.lines() {
            let line = raw.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            let (negate, body) = if let Some(rest) = line.strip_prefix('!') {
                (true, rest.to_string())
            } else {
                (false, line.to_string())
            };
            let dir_only = body.ends_with('/');
            let anchored = body.starts_with('/');
            let body = body
                .trim_start_matches('/')
                .trim_end_matches('/')
                .to_string();
            patterns.push(GitignorePattern {
                body,
                negate,
                dir_only,
                anchored,
            });
        }
        Some(GitignoreMatcher { patterns })
    }

    /// Returns true when `rel_path` (forward-slash relative) should be
    /// ignored. `is_dir` selects the directory vs file rule branch.
    fn is_ignored(&self, rel_path: &str, is_dir: bool) -> bool {
        let mut ignored = false;
        for p in &self.patterns {
            if p.dir_only && !is_dir {
                continue;
            }
            let matched = pattern_matches(&p.body, p.anchored, rel_path, is_dir);
            if matched {
                ignored = !p.negate;
            }
        }
        ignored
    }
}

/// Matches a single gitignore body against a relative path. Supports
/// `*` as a wildcard within a path segment and matches either the
/// full path or any trailing path component for non-anchored patterns
/// (so `node_modules` matches `foo/node_modules`). For directory
/// patterns a match also covers everything beneath.
fn pattern_matches(body: &str, anchored: bool, rel_path: &str, is_dir: bool) -> bool {
    // Direct exact match on the whole rel_path.
    if glob_match(body, rel_path) {
        return true;
    }
    // Anchored patterns only match from the root, so no further checks.
    if anchored {
        return false;
    }
    // Non-anchored: also match if any parent path segment equals the
    // body (directory pattern covering nested files), or if the body
    // matches the final segment.
    let segments: Vec<&str> = rel_path.split('/').collect();
    for (i, seg) in segments.iter().enumerate() {
        if glob_match(body, seg) {
            // For a directory match, everything under it is ignored.
            if is_dir || i < segments.len() - 1 {
                return true;
            }
            // File segment match: only ignore if the body has no slash
            // (a body with a slash implies a path, handled above).
            if !body.contains('/') {
                return true;
            }
        }
    }
    false
}

/// Shell-style glob with `*` matching any run of characters (except,
/// like gitignore, we keep it simple and let `*` cross `/` — this is
/// the same simplification LineWalker's dependency makes for the
/// common case). Case-sensitive, matching gitignore on case-sensitive
/// filesystems.
fn glob_match(pattern: &str, text: &str) -> bool {
    // Recursive `*` matcher.
    let p: Vec<char> = pattern.chars().collect();
    let t: Vec<char> = text.chars().collect();
    glob_rec(&p, 0, &t, 0)
}

fn glob_rec(p: &[char], pi: usize, t: &[char], ti: usize) -> bool {
    if pi == p.len() {
        return ti == t.len();
    }
    if p[pi] == '*' {
        // Try matching zero or more characters.
        for skip in ti..=t.len() {
            if glob_rec(p, pi + 1, t, skip) {
                return true;
            }
        }
        return false;
    }
    if ti < t.len() && p[pi] == t[ti] {
        return glob_rec(p, pi + 1, t, ti + 1);
    }
    false
}

/// Sums the byte size of every regular file under `root`, respecting
/// the same directory-pruning rules as [`scan`]. Used by the git-popup
/// auto-calc path to cheaply decide whether a full count is worth
/// running. Symlinks are skipped (matching the scan walker).
pub fn sum_total_bytes(root: &Path) -> u64 {
    fn walk(dir: &Path, total: &mut u64) {
        let entries = match fs::read_dir(dir) {
            Ok(e) => e,
            Err(_) => return,
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let name = entry.file_name();
            let name = name.to_string_lossy();
            if is_dot_dir(&name) || is_pruned_dir(&name) {
                continue;
            }
            let ft = match entry.file_type() {
                Ok(ft) => ft,
                Err(_) => continue,
            };
            if ft.is_symlink() {
                continue;
            }
            if ft.is_dir() {
                walk(&path, total);
            } else if ft.is_file() {
                if let Ok(meta) = entry.metadata() {
                    *total += meta.len();
                }
            }
        }
    }
    let mut total: u64 = 0;
    walk(root, &mut total);
    total
}

/// Full scan with default options (`.gitignore` disabled). Equivalent
/// to LineWalker's `Scan`.
pub fn scan(root: &Path) -> ScanResult {
    scan_with_options(root, ScanOptions::default())
}

/// Full scan with the given options. Walks `root`, pruning dot-dirs
/// and dependency folders, optionally consulting a root `.gitignore`,
/// and counts newline bytes in every file whose extension is in the
/// allowlist.
pub fn scan_with_options(root: &Path, opts: ScanOptions) -> ScanResult {
    let mut result = ScanResult::default();
    let gi = if opts.use_gitignore {
        GitignoreMatcher::load(root)
    } else {
        None
    };
    let mut seen_skipped = std::collections::BTreeSet::new();
    walk_dir(
        root,
        root,
        gi.as_ref(),
        &mut result,
        &mut seen_skipped,
    );
    // Stable ordering matching LineWalker: alphabetical by rel_path.
    result.code_files.sort_by(|a, b| a.rel_path.cmp(&b.rel_path));
    result.ignored_files.sort_by(|a, b| a.rel_path.cmp(&b.rel_path));
    result.skipped_dirs.sort();
    result
}

fn walk_dir(
    root: &Path,
    dir: &Path,
    gi: Option<&GitignoreMatcher>,
    result: &mut ScanResult,
    seen_skipped: &mut std::collections::BTreeSet<String>,
) {
    let entries = match fs::read_dir(dir) {
        Ok(e) => e,
        Err(e) => {
            result
                .read_errors
                .push(format!("{}: {}", dir.display(), e));
            return;
        }
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let name = entry.file_name();
        let name = name.to_string_lossy().to_string();

        // Prune dot-dirs and the standard dependency/build folders
        // unconditionally (LineWalker never walks them).
        let is_dir = match entry.file_type() {
            Ok(ft) => ft.is_dir(),
            Err(_) => continue,
        };
        if is_dir && (is_dot_dir(&name) || is_pruned_dir(&name)) {
            if let Ok(rel) = path.strip_prefix(root) {
                let rel = rel.to_string_lossy().replace('\\', "/");
                if seen_skipped.insert(rel.clone()) {
                    result.skipped_dirs.push(rel);
                }
            }
            continue;
        }

        let ft = match entry.file_type() {
            Ok(ft) => ft,
            Err(_) => continue,
        };
        // Skip symlinks (LineWalker does; avoids loops).
        if ft.is_symlink() {
            continue;
        }

        let rel = path
            .strip_prefix(root)
            .map(|p| p.to_string_lossy().replace('\\', "/").to_string())
            .unwrap_or_else(|_| name.clone());

        // gitignore check (root patterns only).
        if let Some(gi) = gi {
            if gi.is_ignored(&rel, is_dir) {
                if is_dir {
                    if seen_skipped.insert(rel.clone()) {
                        result.skipped_dirs.push(rel);
                    }
                    continue;
                }
                result.ignored_files.push(IgnoredFile {
                    rel_path: rel,
                    reason: "gitignored".to_string(),
                });
                continue;
            }
        }

        if is_dir {
            walk_dir(root, &path, gi, result, seen_skipped);
            continue;
        }

        if !ft.is_file() {
            continue;
        }

        // Extension allowlist.
        let ext = extension_of(&path);
        if ext.is_empty() {
            result.ignored_files.push(IgnoredFile {
                rel_path: rel,
                reason: "no extension".to_string(),
            });
            continue;
        }
        if !is_counted_extension(&ext) {
            result.ignored_files.push(IgnoredFile {
                rel_path: rel,
                reason: "non-code extension".to_string(),
            });
            continue;
        }

        match count_lines_in_file(&path) {
            Ok(n) => {
                result.code_files.push(CodeFile {
                    rel_path: rel,
                    ext,
                    lines: n,
                });
                result.total_lines += n;
            }
            Err(e) => {
                result
                    .read_errors
                    .push(format!("{}: {}", path.display(), e));
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Tauri commands
// ---------------------------------------------------------------------------

use tauri::State;

use crate::config::commands::AppState;
use crate::config::persistence;
use crate::config::schemas::LineCountStats;

/// Result of the manual "Run line count" button. Carries the full
/// per-file breakdown plus the rendered report for the app logs and a
/// compact stats summary that the UI caches on the project entry.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CountLinesResult {
    pub scan: ScanResult,
    pub report: String,
    pub stats: LineCountStats,
}

/// Manual line-count command invoked from any project type's settings
/// popup footer. Runs a full scan (`.gitignore` respected), caches the
/// summary on the project entry so the UI can show a stale "scanned
/// at" timestamp, and returns the detailed breakdown + report so the
/// frontend can append the report to the app logs.
#[tauri::command]
pub fn count_lines(
    state: State<AppState>,
    project_id: String,
    use_gitignore: Option<bool>,
) -> Result<CountLinesResult, String> {
    // Clone-out + locate the entry under the lock; release before the
    // potentially long scan so the lock is not held during file I/O.
    let (entry, projects) = {
        let guard = state.projects.lock().unwrap();
        let projects = guard.clone();
        let entry = projects
            .projects
            .iter()
            .find(|p| p.id == project_id)
            .cloned()
            .ok_or_else(|| format!("project not found: {}", project_id))?;
        (entry, projects)
    };

    let root = std::path::PathBuf::from(&entry.path);
    let scan = scan_with_options(&root, ScanOptions {
        use_gitignore: use_gitignore.unwrap_or(true),
    });
    let report = scan.render_report();
    let now = chrono::Utc::now().to_rfc3339();
    let stats = LineCountStats {
        total_lines: scan.total_lines,
        code_files: scan.code_files.len() as u32,
        ignored_files: scan.ignored_files.len() as u32,
        skipped_dirs: scan.skipped_dirs.len() as u32,
        scanned_at: now,
        details: report.clone(),
    };

    // Persist the cached stats back onto the entry.
    let mut updated_projects = projects;
    for p in updated_projects.projects.iter_mut() {
        if p.id == project_id {
            p.line_count_stats = Some(stats.clone());
            break;
        }
    }
    if let Err(e) = persistence::save_projects(&updated_projects) {
        log::error!("Failed to persist line-count stats: {}", e);
    }
    {
        let mut guard = state.projects.lock().unwrap();
        *guard = updated_projects.clone();
    }

    Ok(CountLinesResult { scan, report, stats })
}

/// Cheap auto-calc path used by the git popup. When the project's
/// on-disk size is below the configured threshold (default 30 MiB,
/// stored in app settings), this runs a full scan and caches the
/// result; above the threshold it returns `None` so the UI shows a
/// "Run line count for details" hint. Never blocks for long on large
/// projects.
#[tauri::command]
pub fn count_lines_cached(
    state: State<AppState>,
    project_id: String,
) -> Result<Option<LineCountStats>, String> {
    let (entry, projects) = {
        let guard = state.projects.lock().unwrap();
        let projects = guard.clone();
        let entry = projects
            .projects
            .iter()
            .find(|p| p.id == project_id)
            .cloned()
            .ok_or_else(|| format!("project not found: {}", project_id))?;
        (entry, projects)
    };

    // If we already have a cached result, return it immediately — the
    // manual button is the explicit refresh path.
    if let Some(stats) = &entry.line_count_stats {
        return Ok(Some(stats.clone()));
    }

    let threshold_mib = state.settings.lock().unwrap().line_count_auto_calc_threshold_mb as u64;
    let threshold_bytes = threshold_mib.saturating_mul(1024 * 1024);
    let root = std::path::PathBuf::from(&entry.path);
    let size = sum_total_bytes(&root);
    if size > threshold_bytes {
        return Ok(None);
    }

    let scan = scan_with_options(&root, ScanOptions { use_gitignore: true });
    let now = chrono::Utc::now().to_rfc3339();
    let stats = LineCountStats {
        total_lines: scan.total_lines,
        code_files: scan.code_files.len() as u32,
        ignored_files: scan.ignored_files.len() as u32,
        skipped_dirs: scan.skipped_dirs.len() as u32,
        scanned_at: now,
        details: scan.render_report(),
    };

    let mut updated_projects = projects;
    for p in updated_projects.projects.iter_mut() {
        if p.id == project_id {
            p.line_count_stats = Some(stats.clone());
            break;
        }
    }
    if let Err(e) = persistence::save_projects(&updated_projects) {
        log::error!("Failed to persist line-count stats: {}", e);
    }
    {
        let mut guard = state.projects.lock().unwrap();
        *guard = updated_projects.clone();
    }

    Ok(Some(stats))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn write(p: &Path, contents: &str) {
        if let Some(parent) = p.parent() {
            fs::create_dir_all(parent).unwrap();
        }
        fs::write(p, contents).unwrap();
    }

    #[test]
    fn counts_newline_terminated_lines() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join("a.rs"), "line1\nline2\nline3\n");
        let r = scan(dir.path());
        assert_eq!(r.total_lines, 3);
        assert_eq!(r.code_files.len(), 1);
        assert_eq!(r.code_files[0].ext, "rs");
        assert_eq!(r.code_files[0].lines, 3);
    }

    #[test]
    fn counts_file_without_trailing_newline_as_one_less() {
        // newline-semantics: a file with N lines and no trailing
        // newline has N-1 newline bytes. This matches `wc -l` and
        // LineWalker exactly.
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join("a.ts"), "a\nb\nc");
        let r = scan(dir.path());
        assert_eq!(r.total_lines, 2);
    }

    #[test]
    fn non_code_extension_is_ignored_with_reason() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join("data.json"), "{}\n");
        write(&dir.path().join("readme.md"), "# hi\n");
        let r = scan(dir.path());
        assert_eq!(r.total_lines, 0);
        assert_eq!(r.code_files.len(), 0);
        let reasons: Vec<_> = r.ignored_files.iter().map(|f| f.reason.clone()).collect();
        assert_eq!(reasons.len(), 2);
        assert!(reasons.iter().all(|x| x == "non-code extension"));
    }

    #[test]
    fn no_extension_is_ignored() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join("Makefile"), "all:\n\techo hi\n");
        let r = scan(dir.path());
        assert_eq!(r.total_lines, 0);
        assert_eq!(r.ignored_files[0].reason, "no extension");
    }

    #[test]
    fn dot_dirs_are_pruned() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join(".git/a.rs"), "x\n");
        write(&dir.path().join(".hidden/b.ts"), "y\n");
        write(&dir.path().join("real.rs"), "z\n");
        let r = scan(dir.path());
        assert_eq!(r.total_lines, 1);
        assert!(r.skipped_dirs.iter().any(|d| d == ".git"));
        assert!(r.skipped_dirs.iter().any(|d| d == ".hidden"));
    }

    #[test]
    fn dependency_dirs_are_pruned() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join("node_modules/pkg/a.js"), "x\n");
        write(&dir.path().join("vendor/lib.go"), "y\n");
        write(&dir.path().join("src/main.rs"), "z\n");
        let r = scan(dir.path());
        assert_eq!(r.total_lines, 1);
        assert!(r.skipped_dirs.iter().any(|d| d == "node_modules"));
        assert!(r.skipped_dirs.iter().any(|d| d == "vendor"));
    }

    #[test]
    fn gitignore_skips_matched_files_when_enabled() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join(".gitignore"), "ignored.rs\n");
        write(&dir.path().join("ignored.rs"), "x\n");
        write(&dir.path().join("kept.rs"), "y\n");
        let r = scan_with_options(dir.path(), ScanOptions { use_gitignore: true });
        assert_eq!(r.total_lines, 1);
        assert!(r.ignored_files.iter().any(|f| f.rel_path == "ignored.rs"));
    }

    #[test]
    fn gitignore_disabled_by_default() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join(".gitignore"), "*.rs\n");
        write(&dir.path().join("a.rs"), "x\n");
        // Without use_gitignore, the .rs file is still counted.
        let r = scan(dir.path());
        assert_eq!(r.total_lines, 1);
    }

    #[test]
    fn render_report_has_all_four_sections() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join("a.rs"), "x\n");
        write(&dir.path().join("b.json"), "{}\n");
        let r = scan(dir.path());
        let report = r.render_report();
        assert!(report.contains("Code files (path, extension, lines):"));
        assert!(report.contains("Ignored (not counted):"));
        assert!(report.contains("Skipped directories (subtree not walked):"));
        assert!(report.contains("Total lines: 1"));
    }

    #[test]
    fn sum_total_bytes_respects_pruning() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join(".git/big.bin"), &"x".repeat(10_000));
        write(&dir.path().join("node_modules/pkg.js"), &"y".repeat(5_000));
        write(&dir.path().join("src/main.rs"), "z");
        let total = sum_total_bytes(dir.path());
        // Only src/main.rs (1 byte) is counted; pruned dirs excluded.
        assert_eq!(total, 1);
    }

    #[test]
    fn extension_matching_is_case_insensitive() {
        assert!(is_counted_extension("RS"));
        assert!(is_counted_extension("Ts"));
        assert!(!is_counted_extension("JSON"));
    }

    #[test]
    fn glob_match_handles_star() {
        assert!(glob_match("*.rs", "a.rs"));
        assert!(glob_match("*.rs", "main.rs"));
        assert!(!glob_match("*.rs", "main.go"));
        assert!(glob_match("build", "build"));
    }
}
