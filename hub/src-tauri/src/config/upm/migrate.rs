//! Replace-only-by-name file migration for packages.
//!
//! The Package settings popup has a "Migrate" tab where the user picks
//! a source folder and overwrites files in the package **only when a
//! file with the same relative path exists on both sides**. Files that
//! exist only in the source are *not* copied (no new files are
//! created); files that exist only in the package are *not* deleted or
//! touched. Both groups are still reported so the user can see what
//! differed. Optionally, `.meta` files can be left untouched even when
//! they match (toggle, off by default). A per-package source folder is
//! persisted on `ProjectEntry.migrate_source_folder`.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::persistence;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MigrateEntry {
    /// Path relative to the package root (forward slashes).
    pub rel_path: String,
    /// One of `replaced`, `skipped-meta`, `skipped-new`, `untouched`.
    pub action: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MigrateResult {
    /// Every acted-on and reported file, in walk order.
    pub entries: Vec<MigrateEntry>,
    /// Files that existed on both sides and were overwritten.
    pub replaced: u32,
    /// Matched `.meta` files left untouched because `skip_meta` was on.
    pub skipped_meta: u32,
    /// Files present only in the source (not copied — replace-only mode).
    pub skipped_new: u32,
    /// Files present only in the package (untouched, informational).
    pub untouched: u32,
    /// The saved source folder, persisted on the project entry so the
    /// next Migrate open pre-fills the field.
    pub saved_source_folder: String,
    /// Echo of the flag the migration ran with.
    pub skip_meta: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum MigrateError {
    #[serde(rename_all = "camelCase")]
    ProjectNotFound { project_id: String },
    #[serde(rename_all = "camelCase")]
    SourceNotADirectory { path: String },
    #[serde(rename_all = "camelCase")]
    PersistFailed { message: String },
}

/// Returns true when `rel_path` points inside a `~`-suffixed folder
/// (Unity ignores these — `Samples~`, `Documentation~` — so their
/// contents are excluded from migration just like `.meta` generation
/// does).
fn is_inside_tilde_folder(rel_path: &str) -> bool {
    rel_path.split('/').any(|seg| seg.ends_with('~'))
}

/// Directory and file names to skip during the walk — the same set
/// used by `.meta` generation in `meta.rs` so the two features stay
/// consistent about what counts as "package content".
fn is_skipped_name(name: &str) -> bool {
    name.starts_with('.')
        || matches!(
            name,
            "node_modules" | "vendor" | "dist" | "build" | "target"
        )
}

/// Collects every file under `root` keyed by its forward-slashed path
/// relative to `root`. Skips dot-dirs, the standard dependency folders,
/// and anything inside a `~`-suffixed folder. Directories themselves
/// are not entries — only files are — because replace-only mode never
/// creates or removes directories.
fn collect_source_files(root: &Path) -> BTreeMap<String, PathBuf> {
    let mut map: BTreeMap<String, PathBuf> = BTreeMap::new();
    let mut stack: Vec<PathBuf> = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let iter = match fs::read_dir(&dir) {
            Ok(i) => i,
            Err(_) => continue,
        };
        for entry in iter.flatten() {
            let path = entry.path();
            let name = entry.file_name();
            let name = name.to_string_lossy().to_string();
            if is_skipped_name(&name) {
                continue;
            }
            let ft = match entry.file_type() {
                Ok(ft) => ft,
                Err(_) => continue,
            };
            if ft.is_dir() {
                stack.push(path);
            } else if ft.is_file() {
                let rel = path
                    .strip_prefix(root)
                    .map(|p| p.to_string_lossy().replace('\\', "/").to_string())
                    .unwrap_or_else(|_| name.clone());
                if is_inside_tilde_folder(&rel) {
                    continue;
                }
                map.insert(rel, path);
            }
            // Symlinks and other special files are skipped (avoids loops
            // and cross-device surprises).
        }
    }
    map
}

/// Performs the replace-only migration. Walks the package (destination)
/// tree, and for each destination file checks whether the source has a
/// file at the same relative path:
///   - match (file↔file): overwrite unless it is a `.meta` file and
///     `skip_meta` is on (then record `skipped-meta`);
///   - destination-only: record `untouched` (informational);
/// Source-only files (new in source) are not copied — they are recorded
/// as `skipped-new` after the destination walk. Directories are walked
/// but never created or deleted.
fn migrate_replace_only(
    src_files: &BTreeMap<String, PathBuf>,
    dst: &Path,
    skip_meta: bool,
    entries: &mut Vec<MigrateEntry>,
    errors: &mut Vec<String>,
    replaced: &mut u32,
    skipped_meta: &mut u32,
    untouched: &mut u32,
) {
    let mut matched: std::collections::HashSet<String> = std::collections::HashSet::new();
    let mut stack: Vec<PathBuf> = vec![dst.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let iter = match fs::read_dir(&dir) {
            Ok(i) => i,
            Err(_) => continue,
        };
        for entry in iter.flatten() {
            let path = entry.path();
            let name = entry.file_name();
            let name = name.to_string_lossy().to_string();
            if is_skipped_name(&name) {
                continue;
            }
            let ft = match entry.file_type() {
                Ok(ft) => ft,
                Err(_) => continue,
            };
            if ft.is_dir() {
                stack.push(path);
            } else if ft.is_file() {
                let rel = path
                    .strip_prefix(dst)
                    .map(|p| p.to_string_lossy().replace('\\', "/").to_string())
                    .unwrap_or_else(|_| name.clone());
                if is_inside_tilde_folder(&rel) {
                    continue;
                }
                match src_files.get(&rel) {
                    Some(src_path) => {
                        matched.insert(rel.clone());
                        if skip_meta && name.ends_with(".meta") {
                            *skipped_meta += 1;
                            entries.push(MigrateEntry {
                                rel_path: rel,
                                action: "skipped-meta".into(),
                            });
                        } else if let Err(e) = fs::copy(src_path, &path) {
                            errors.push(format!("copy {} → {}: {}", src_path.display(), path.display(), e));
                        } else {
                            *replaced += 1;
                            entries.push(MigrateEntry {
                                rel_path: rel,
                                action: "replaced".into(),
                            });
                        }
                    }
                    None => {
                        *untouched += 1;
                        entries.push(MigrateEntry {
                            rel_path: rel,
                            action: "untouched".into(),
                        });
                    }
                }
            }
        }
    }

    // Anything in the source that never matched a destination file is a
    // new-in-source file we deliberately did not copy.
    let mut skipped_new: Vec<String> = src_files
        .keys()
        .filter(|rel| !matched.contains(rel.as_str()))
        .cloned()
        .collect();
    skipped_new.sort();
    for rel in skipped_new {
        entries.push(MigrateEntry {
            rel_path: rel,
            action: "skipped-new".into(),
        });
    }
}

/// Migrates files from `source_folder` into the package root,
/// replace-only-by-name mode. Persists `source_folder` on the project
/// entry so it pre-fills next time.
#[tauri::command]
pub fn migrate_package_files(
    state: State<AppState>,
    project_id: String,
    source_folder: String,
    skip_meta: bool,
) -> Result<MigrateResult, MigrateError> {
    let (entry, projects) = {
        let guard = state.projects.lock().unwrap();
        let entry = guard
            .projects
            .iter()
            .find(|p| p.id == project_id)
            .cloned()
            .ok_or_else(|| MigrateError::ProjectNotFound {
                project_id: project_id.clone(),
            })?;
        (entry, guard.clone())
    };

    let src = PathBuf::from(&source_folder);
    if !src.is_dir() {
        return Err(MigrateError::SourceNotADirectory {
            path: source_folder,
        });
    }
    let dst = PathBuf::from(&entry.path);

    let src_files = collect_source_files(&src);

    let mut entries: Vec<MigrateEntry> = Vec::new();
    let mut errors: Vec<String> = Vec::new();
    let mut replaced: u32 = 0;
    let mut skipped_meta: u32 = 0;
    let mut untouched: u32 = 0;
    migrate_replace_only(
        &src_files,
        &dst,
        skip_meta,
        &mut entries,
        &mut errors,
        &mut replaced,
        &mut skipped_meta,
        &mut untouched,
    );
    let skipped_new = entries
        .iter()
        .filter(|e| e.action == "skipped-new")
        .count() as u32;

    // Persist the source folder on the entry + bump mtime.
    let mut updated = projects;
    let now = chrono::Utc::now().to_rfc3339();
    for p in updated.projects.iter_mut() {
        if p.id == project_id {
            p.migrate_source_folder = Some(source_folder.clone());
            p.last_modified_at = Some(now);
            break;
        }
    }
    if let Err(e) = persistence::save_projects(&updated) {
        log::error!("Failed to persist migrate source folder: {}", e);
        return Err(MigrateError::PersistFailed {
            message: e.to_string(),
        });
    }
    {
        let mut guard = state.projects.lock().unwrap();
        *guard = updated;
    }

    if !errors.is_empty() {
        // Log the per-file errors but do not fail the whole migration
        // — a partial copy is still useful and the log shows what was
        // missed.
        for e in &errors {
            log::warn!("migrate error: {}", e);
        }
    }

    Ok(MigrateResult {
        entries,
        replaced,
        skipped_meta,
        skipped_new,
        untouched,
        saved_source_folder: source_folder,
        skip_meta,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    /// Drives the core walk without going through the Tauri command
    /// (which needs an `AppState`). Mirrors what the command does for
    /// the small fixture built by each test.
    fn run(
        src: &Path,
        dst: &Path,
        skip_meta: bool,
    ) -> (
        Vec<MigrateEntry>,
        u32,
        u32,
        u32,
        u32,
    ) {
        let src_files = collect_source_files(src);
        let mut entries = Vec::new();
        let mut errors = Vec::new();
        let mut replaced = 0;
        let mut skipped_meta = 0;
        let mut untouched = 0;
        migrate_replace_only(
            &src_files,
            dst,
            skip_meta,
            &mut entries,
            &mut errors,
            &mut replaced,
            &mut skipped_meta,
            &mut untouched,
        );
        let skipped_new = entries
            .iter()
            .filter(|e| e.action == "skipped-new")
            .count() as u32;
        assert!(errors.is_empty(), "unexpected errors: {:?}", errors);
        (entries, replaced, skipped_meta, skipped_new, untouched)
    }

    #[test]
    fn replaces_matched_files_and_leaves_others_alone() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dst).unwrap();

        // matched — should be overwritten
        fs::write(dst.join("existing.txt"), "old").unwrap();
        fs::write(src.join("existing.txt"), "new").unwrap();
        // package-only — must survive untouched (not deleted, not reported as replaced)
        fs::write(dst.join("keep-me.txt"), "untouched").unwrap();
        // source-only — must NOT be copied
        fs::write(src.join("brand-new.txt"), "src only").unwrap();

        let (entries, replaced, skipped_meta, skipped_new, untouched) =
            run(&src, &dst, false);

        assert_eq!(replaced, 1);
        assert_eq!(skipped_meta, 0);
        assert_eq!(skipped_new, 1);
        assert_eq!(untouched, 1);
        assert_eq!(
            fs::read_to_string(dst.join("existing.txt")).unwrap(),
            "new"
        );
        // Package-only file is untouched on disk...
        assert_eq!(
            fs::read_to_string(dst.join("keep-me.txt")).unwrap(),
            "untouched"
        );
        // ...and the source-only file was never created in the package.
        assert!(!dst.join("brand-new.txt").exists());

        let by_action: std::collections::HashMap<&str, usize> =
            entries
                .iter()
                .fold(std::collections::HashMap::new(), |mut acc, e| {
                    *acc.entry(e.action.as_str()).or_insert(0) += 1;
                    acc
                });
        assert_eq!(by_action["replaced"], 1);
        assert_eq!(by_action["untouched"], 1);
        assert_eq!(by_action["skipped-new"], 1);
    }

    #[test]
    fn nested_paths_match_by_relative_path() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        fs::create_dir_all(src.join("Editor/Sub")).unwrap();
        fs::create_dir_all(dst.join("Editor/Sub")).unwrap();
        fs::write(dst.join("Editor/Sub/Foo.cs"), "old").unwrap();
        fs::write(src.join("Editor/Sub/Foo.cs"), "new").unwrap();
        // Different name in the same dir — source-only.
        fs::write(src.join("Editor/Sub/Bar.cs"), "src only").unwrap();

        let (entries, replaced, _, skipped_new, untouched) = run(&src, &dst, false);

        assert_eq!(replaced, 1);
        assert_eq!(skipped_new, 1);
        assert_eq!(untouched, 0);
        assert_eq!(
            fs::read_to_string(dst.join("Editor/Sub/Foo.cs")).unwrap(),
            "new"
        );
        assert!(!dst.join("Editor/Sub/Bar.cs").exists());
        let replaced_entry = entries
            .iter()
            .find(|e| e.action == "replaced")
            .unwrap();
        assert_eq!(replaced_entry.rel_path, "Editor/Sub/Foo.cs");
    }

    #[test]
    fn skip_meta_leaves_meta_files_untouched_but_reports_them() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dst).unwrap();
        fs::write(dst.join("Foo.cs"), "old cs").unwrap();
        fs::write(dst.join("Foo.cs.meta"), "old meta").unwrap();
        fs::write(src.join("Foo.cs"), "new cs").unwrap();
        fs::write(src.join("Foo.cs.meta"), "new meta").unwrap();

        // skip_meta on: the .cs is replaced, the .meta matches but is left alone.
        let (entries, replaced, skipped_meta, _, _) = run(&src, &dst, true);
        assert_eq!(replaced, 1);
        assert_eq!(skipped_meta, 1);
        assert_eq!(
            fs::read_to_string(dst.join("Foo.cs")).unwrap(),
            "new cs"
        );
        assert_eq!(
            fs::read_to_string(dst.join("Foo.cs.meta")).unwrap(),
            "old meta"
        );
        assert_eq!(
            entries
                .iter()
                .find(|e| e.action == "skipped-meta")
                .unwrap()
                .rel_path,
            "Foo.cs.meta"
        );
    }

    #[test]
    fn skip_meta_off_replaces_meta_files_normally() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dst).unwrap();
        fs::write(dst.join("Foo.cs.meta"), "old").unwrap();
        fs::write(src.join("Foo.cs.meta"), "new").unwrap();

        let (_, replaced, skipped_meta, _, _) = run(&src, &dst, false);
        assert_eq!(replaced, 1);
        assert_eq!(skipped_meta, 0);
        assert_eq!(
            fs::read_to_string(dst.join("Foo.cs.meta")).unwrap(),
            "new"
        );
    }

    #[test]
    fn tilde_folders_are_excluded() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        fs::create_dir_all(src.join("Samples~/Demo")).unwrap();
        fs::create_dir_all(dst.join("Samples~/Demo")).unwrap();
        // Files inside Samples~ should be ignored on both sides.
        fs::write(src.join("Samples~/Demo/Bar.cs"), "src").unwrap();
        fs::write(dst.join("Samples~/Demo/Bar.cs"), "dst").unwrap();
        // A normal matched file outside ~ to prove the walk still runs.
        fs::write(src.join("Top.cs"), "new").unwrap();
        fs::write(dst.join("Top.cs"), "old").unwrap();

        let (entries, replaced, _, skipped_new, untouched) = run(&src, &dst, false);
        assert_eq!(replaced, 1); // only Top.cs
        assert_eq!(skipped_new, 0);
        assert_eq!(untouched, 0);
        assert!(entries.iter().all(|e| !e.rel_path.contains("Samples~")));
    }
}
