//! Run-summary export persistence (phase-5 deliverable: export).
//!
//! Exports are sign-off markdown files written under the project's
//! `exportsDir` (`UserSettings/ValidationSuite/exports/`). The markdown
//! body is built by the frontend (`packages/core/src/export.ts`); the
//! backend only owns the atomic disk write + a timestamped filename so
//! the export travels with the project and survives app restarts.
//!
//! Reuses [`persistence::atomic_write`] for crash-safe writes (mirrors
//! the state-file pattern). Filenames are timestamped + sanitized so
//! repeated exports stack instead of clobbering.

use std::path::{Path, PathBuf};

use crate::persistence::atomic_write;

/// Resolve the absolute exports dir for a project, creating the leaf if
/// missing: `<root>/<exportsDir>`.
fn ensure_exports_dir(project_root: &Path, exports_dir: &str) -> PathBuf {
    let dir = project_root.join(exports_dir);
    let _ = std::fs::create_dir_all(&dir);
    dir
}

/// Build a timestamped, path-safe export filename.
///
/// `stem` is a short label (e.g. `m9` or `run`); the timestamp makes
/// repeated exports stack. The result is `<stem>-<utc-ts>.md`.
pub fn export_filename(stem: &str, generated_at: &str) -> String {
    let safe_stem = sanitize(stem);
    let safe_ts = sanitize_ts(generated_at);
    format!("{safe_stem}-{safe_ts}.md")
}

/// Write an export markdown body to `<exportsDir>/<filename>` and return
/// the project-relative path (so the UI can show where it landed).
pub fn save(
    project_root: &Path,
    exports_dir: &str,
    filename: &str,
    body: &str,
) -> std::io::Result<String> {
    let dir = ensure_exports_dir(project_root, exports_dir);
    let path = dir.join(filename);
    atomic_write(&path, body)?;
    // Return the project-relative path for display.
    Ok(relative_to(project_root, &path))
}

/// Express `path` relative to `base` using forward slashes (portable).
fn relative_to(base: &Path, path: &Path) -> String {
    path.strip_prefix(base)
        .map(|p| p.to_string_lossy().replace('\\', "/"))
        .unwrap_or_else(|_| path.to_string_lossy().replace('\\', "/"))
}

fn sanitize(s: &str) -> String {
    s.chars()
        .map(|c| if c.is_ascii_alphanumeric() || c == '-' { c } else { '-' })
        .collect()
}

/// Collapse an ISO-8601 timestamp into path-safe characters
/// (`2026-06-25T12:34:56.789Z` → `2026-06-25T12-34-56-789Z`).
fn sanitize_ts(ts: &str) -> String {
    ts.chars()
        .map(|c| match c {
            ':' => '-',
            _ => c,
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn save_writes_under_exports_dir_and_returns_relative_path() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path();
        let rel = save(root, "UserSettings/ValidationSuite/exports/", "m9-2026-06-25T00-00-00-000Z.md", "# export").unwrap();
        assert_eq!(rel, "UserSettings/ValidationSuite/exports/m9-2026-06-25T00-00-00-000Z.md");
        let written = std::fs::read_to_string(
            root.join("UserSettings/ValidationSuite/exports/m9-2026-06-25T00-00-00-000Z.md"),
        )
        .unwrap();
        assert_eq!(written, "# export");
    }

    #[test]
    fn save_creates_exports_dir_if_missing() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path();
        assert!(!root.join("UserSettings/ValidationSuite/exports").exists());
        save(root, "UserSettings/ValidationSuite/exports/", "x.md", "body").unwrap();
        assert!(root.join("UserSettings/ValidationSuite/exports").exists());
    }

    #[test]
    fn export_filename_sanitize_ts_and_stem() {
        let name = export_filename("M9 run", "2026-06-25T12:34:56.789Z");
        assert_eq!(name, "M9-run-2026-06-25T12-34-56.789Z.md");
    }

    #[test]
    fn repeated_exports_stack_not_clobber() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path();
        let exports_dir = "UserSettings/ValidationSuite/exports/";
        let f1 = export_filename("m9", "2026-06-25T00:00:00.000Z");
        let f2 = export_filename("m9", "2026-06-25T00:00:01.000Z");
        save(root, exports_dir, &f1, "first").unwrap();
        save(root, exports_dir, &f2, "second").unwrap();
        assert_eq!(
            std::fs::read_to_string(root.join(exports_dir).join(&f1)).unwrap(),
            "first"
        );
        assert_eq!(
            std::fs::read_to_string(root.join(exports_dir).join(&f2)).unwrap(),
            "second"
        );
    }
}
