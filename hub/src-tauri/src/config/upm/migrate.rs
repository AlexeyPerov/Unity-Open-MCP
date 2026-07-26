//! Replace-only-by-name file migration for packages.
//!
//! The Package settings popup has a "Migrate" tab where the user picks
//! a source folder and overwrites files in the package **only when a
//! file with the same basename (`filename.ext`) exists on both sides**.
//! Matching is by basename alone — the file's folder is ignored, so a
//! source file at `Editor/Foo.cs` will overwrite a package file at
//! `Runtime/Foo.cs`. If a basename appears more than once on *either*
//! side the match is ambiguous and **all** its occurrences are skipped
//! and reported as duplicates. Files that exist only in the source are
//! *not* copied (no new files are created); files that exist only in
//! the package are *not* deleted or touched. Optionally, `.meta` files
//! can be left untouched even when they match (toggle, off by default).
//! A per-package source folder is persisted on
//! `ProjectEntry.migrate_source_folder`.

use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::persistence;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MigrateEntry {
    /// Path relative to the file's own root (source root for source
    /// files, package root for package files), forward slashes.
    pub rel_path: String,
    /// One of `replaced`, `skipped-meta`, `skipped-new`, `untouched`,
    /// `skipped-duplicate`.
    pub action: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MigrateResult {
    /// Every acted-on and reported file, in basename-sorted order.
    pub entries: Vec<MigrateEntry>,
    /// Files that had a 1:1 basename match and were overwritten.
    pub replaced: u32,
    /// Matched `.meta` files left untouched because `skip_meta` was on.
    pub skipped_meta: u32,
    /// Files present only in the source (not copied — replace-only mode).
    pub skipped_new: u32,
    /// Files present only in the package (untouched, informational).
    pub untouched: u32,
    /// Occurrences of basenames that appeared 2+ times on either side —
    /// ambiguous, so skipped (each occurrence counts once).
    pub skipped_duplicate: u32,
    /// H35: 1:1 basename matches whose `fs::copy` FAILED. `fs::copy`
    /// opens the destination `O_WRONLY|O_CREAT|O_TRUNC` before reading
    /// the source, so a failed copy has already truncated the package
    /// file to 0 bytes — counting these separately (and listing them in
    /// `errors`) is the only way the UI can show that data was lost
    /// rather than presenting a clean "migrated N files".
    pub failed: u32,
    /// H35: human-readable per-file copy failures, mirroring the
    /// `errors` vec collected during classification. Surfaced so the
    /// frontend can render them in the migration report instead of
    /// burying them in the server log.
    pub errors: Vec<String>,
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
    /// The source folder resolves to the package itself, an ancestor of
    /// the package, or a descendant of the package. Any of these would
    /// let a basename match its own file: `fs::copy` opens the src
    /// RDONLY, opens the dst `O_WRONLY|O_CREAT|O_TRUNC`, then reads —
    /// yielding a 0-byte file (silent irreversible source loss). Reject
    /// up-front so the user never reaches the destructive `fs::copy`.
    #[serde(rename_all = "camelCase")]
    SourceOverlapsPackage { source: String, package_path: String },
}

/// Returns true when `rel_path` points inside a `~`-suffixed folder
/// (Unity ignores these — `Samples~`, `Documentation~` — so their
/// contents are excluded from migration just like `.meta` generation
/// does).
fn is_inside_tilde_folder(rel_path: &str) -> bool {
    rel_path.split('/').any(|seg| seg.ends_with('~'))
}

/// `true` when `a` and `b` resolve to the same underlying file (same
/// canonical path or, on unix, the same `(device, inode)` pair after
/// symlink resolution). Used by the migrate copy step to refuse to
/// copy a file onto itself — `fs::copy` has no same-file guard and the
/// open order (`O_TRUNC` before read) produces a 0-byte file. H3.
#[cfg(unix)]
fn same_file(a: &Path, b: &Path) -> bool {
    use std::os::unix::fs::MetadataExt;
    let ma = match fs::metadata(a) {
        Ok(m) => m,
        Err(_) => return a == b,
    };
    let mb = match fs::metadata(b) {
        Ok(m) => m,
        Err(_) => return a == b,
    };
    ma.dev() == mb.dev() && ma.ino() == mb.ino()
}

#[cfg(not(unix))]
fn same_file(a: &Path, b: &Path) -> bool {
    fs::canonicalize(a).ok() == fs::canonicalize(b).ok()
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

/// Collects every file under `root`, grouped by file **basename**
/// (`filename.ext`) — the match key for replace-only mode. Each value
/// is the list of `(rel_path, abs_path)` occurrences sharing that
/// basename, in walk order. Skips dot-dirs, the standard dependency
/// folders, and anything inside a `~`-suffixed folder. Directories
/// themselves are not entries — only files are — because replace-only
/// mode never creates or removes directories.
fn collect_files_by_name(root: &Path) -> BTreeMap<String, Vec<(String, PathBuf)>> {
    let mut map: BTreeMap<String, Vec<(String, PathBuf)>> = BTreeMap::new();
    let mut stack: Vec<PathBuf> = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let iter = match fs::read_dir(&dir) {
            Ok(i) => i,
            Err(_) => continue,
        };
        for entry in iter.flatten() {
            let path = entry.path();
            // `file_name()` is the basename (filename.ext) — the match key.
            let basename = entry.file_name();
            let basename = basename.to_string_lossy().to_string();
            if is_skipped_name(&basename) {
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
                    .unwrap_or_else(|_| basename.clone());
                if is_inside_tilde_folder(&rel) {
                    continue;
                }
                map.entry(basename).or_default().push((rel, path));
            }
            // Symlinks and other special files are skipped (avoids loops
            // and cross-device surprises).
        }
    }
    map
}

/// Performs the replace-only migration by classifying every basename
/// seen on either side:
///   - basename appears 2+ times on either side → **duplicate**: every
///     occurrence (source + package) is recorded as `skipped-duplicate`
///     and nothing is copied;
///   - 1:1 match → `replaced`, or `skipped-meta` when the basename ends
///     with `.meta` and `skip_meta` is on;
///   - source-only → `skipped-new` (not copied);
///   - package-only → `untouched`;
///   - 1:1 match whose `fs::copy` failed → `failed` (the destination is
///     already truncated to 0 bytes by `fs::copy`'s open order, so this
///     is surfaced distinctly from `replaced` — see H35).
fn migrate_replace_only(
    src_by_name: &BTreeMap<String, Vec<(String, PathBuf)>>,
    dst_by_name: &BTreeMap<String, Vec<(String, PathBuf)>>,
    skip_meta: bool,
    entries: &mut Vec<MigrateEntry>,
    errors: &mut Vec<String>,
    replaced: &mut u32,
    skipped_meta: &mut u32,
    skipped_new: &mut u32,
    untouched: &mut u32,
    skipped_duplicate: &mut u32,
    failed: &mut u32,
) {
    // Union of all basenames, iterated in sorted order for deterministic
    // output (BTreeSet of BTreeMap keys).
    let mut all_names: BTreeSet<String> = BTreeSet::new();
    all_names.extend(src_by_name.keys().cloned());
    all_names.extend(dst_by_name.keys().cloned());

    for basename in all_names {
        let srcs = src_by_name.get(&basename);
        let dsts = dst_by_name.get(&basename);
        let src_count = srcs.map(|v| v.len()).unwrap_or(0);
        let dst_count = dsts.map(|v| v.len()).unwrap_or(0);

        // Ambiguous: the basename is not unique on at least one side, so
        // we cannot know which source file to read or which package file
        // to overwrite. Skip every occurrence and report them.
        if src_count > 1 || dst_count > 1 {
            if let Some(srcs) = srcs {
                for (rel, _abs) in srcs {
                    *skipped_duplicate += 1;
                    entries.push(MigrateEntry {
                        rel_path: rel.clone(),
                        action: "skipped-duplicate".into(),
                    });
                }
            }
            if let Some(dsts) = dsts {
                for (rel, _abs) in dsts {
                    *skipped_duplicate += 1;
                    entries.push(MigrateEntry {
                        rel_path: rel.clone(),
                        action: "skipped-duplicate".into(),
                    });
                }
            }
            continue;
        }

        match (srcs.and_then(|v| v.first()), dsts.and_then(|v| v.first())) {
            (Some((_src_rel, src_abs)), Some((dst_rel, dst_abs))) => {
                // 1:1 basename match — overwrite the package file.
                if skip_meta && basename.ends_with(".meta") {
                    *skipped_meta += 1;
                    entries.push(MigrateEntry {
                        rel_path: dst_rel.clone(),
                        action: "skipped-meta".into(),
                    });
                } else if same_file(src_abs, dst_abs) {
                    // H3 defense-in-depth: the top-level overlap guard
                    // catches a source == package tree. This per-pair
                    // check catches the residual case of two distinct
                    // folders that share a file via hardlink/symlink —
                    // `fs::copy` would truncate-then-read the same inode
                    // and emit a 0-byte file. Report and skip rather
                    // than destroy.
                    *skipped_duplicate += 1;
                    entries.push(MigrateEntry {
                        rel_path: dst_rel.clone(),
                        action: "skipped-duplicate".into(),
                    });
                    errors.push(format!(
                        "copy {} → {}: source and destination resolve to the same file",
                        src_abs.display(),
                        dst_abs.display(),
                    ));
                } else if let Err(e) = fs::copy(src_abs, dst_abs) {
                    // H35: `fs::copy` opens dst O_TRUNC before reading
                    // src, so a failure here has already zeroed the
                    // package file. Count it as `failed` and surface a
                    // `failed` entry + error string so the UI can show
                    // the loss instead of a clean "migrated N files".
                    *failed += 1;
                    entries.push(MigrateEntry {
                        rel_path: dst_rel.clone(),
                        action: "failed".into(),
                    });
                    errors.push(format!(
                        "copy {} → {}: {}",
                        src_abs.display(),
                        dst_abs.display(),
                        e
                    ));
                } else {
                    *replaced += 1;
                    entries.push(MigrateEntry {
                        rel_path: dst_rel.clone(),
                        action: "replaced".into(),
                    });
                }
            }
            (Some((src_rel, _src_abs)), None) => {
                *skipped_new += 1;
                entries.push(MigrateEntry {
                    rel_path: src_rel.clone(),
                    action: "skipped-new".into(),
                });
            }
            (None, Some((dst_rel, _dst_abs))) => {
                *untouched += 1;
                entries.push(MigrateEntry {
                    rel_path: dst_rel.clone(),
                    action: "untouched".into(),
                });
            }
            (None, None) => {} // unreachable: name came from one of the maps
        }
    }
}

/// Migrates files from `source_folder` into the package root,
/// replace-only-by-basename mode. Persists `source_folder` on the
/// project entry so it pre-fills next time.
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

    // H3: refuse to migrate when the source folder canonicalizes to the
    // package itself, an ancestor of the package, or a descendant of the
    // package. Pointing the free "Browse…" picker at the package folder
    // (or any ancestor) makes every uniquely-named file match *itself*,
    // and Rust's `fs::copy` has no same-file guard — the open order
    // (src RDONLY, dst `O_TRUNC`, then read) yields a 0-byte file: silent
    // irreversible source loss. `canonicalize` resolves symlinks, so a
    // symlink aliasing the package counts too.
    let src_canon = std::fs::canonicalize(&src).ok();
    let dst_canon = std::fs::canonicalize(&dst).ok();
    if let (Some(s), Some(d)) = (src_canon.as_ref(), dst_canon.as_ref()) {
        if s == d || s.starts_with(d) || d.starts_with(s) {
            return Err(MigrateError::SourceOverlapsPackage {
                source: source_folder,
                package_path: entry.path.clone(),
            });
        }
    }

    let src_by_name = collect_files_by_name(&src);
    let dst_by_name = collect_files_by_name(&dst);

    let mut entries: Vec<MigrateEntry> = Vec::new();
    let mut errors: Vec<String> = Vec::new();
    let mut replaced: u32 = 0;
    let mut skipped_meta: u32 = 0;
    let mut skipped_new: u32 = 0;
    let mut untouched: u32 = 0;
    let mut skipped_duplicate: u32 = 0;
    let mut failed: u32 = 0;
    migrate_replace_only(
        &src_by_name,
        &dst_by_name,
        skip_meta,
        &mut entries,
        &mut errors,
        &mut replaced,
        &mut skipped_meta,
        &mut skipped_new,
        &mut untouched,
        &mut skipped_duplicate,
        &mut failed,
    );

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
        // H35: still log the per-file errors for server-side triage, but
        // they are now ALSO surfaced to the UI via `MigrateResult.errors`
        // + the `failed` counter so a truncated-destination loss is
        // visible in the report rather than presented as a clean run.
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
        skipped_duplicate,
        failed,
        errors,
        saved_source_folder: source_folder,
        skip_meta,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use std::fs;

    /// Drives the core classification without going through the Tauri
    /// command (which needs an `AppState`). Mirrors what the command
    /// does for the small fixture built by each test. Returns
    /// `(entries, replaced, skipped_meta, skipped_new, untouched,
    /// skipped_duplicate, failed, errors)`.
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
        u32,
        u32,
        Vec<String>,
    ) {
        let src_by_name = collect_files_by_name(src);
        let dst_by_name = collect_files_by_name(dst);
        let mut entries = Vec::new();
        let mut errors = Vec::new();
        let mut replaced = 0;
        let mut skipped_meta = 0;
        let mut skipped_new = 0;
        let mut untouched = 0;
        let mut skipped_duplicate = 0;
        let mut failed = 0;
        migrate_replace_only(
            &src_by_name,
            &dst_by_name,
            skip_meta,
            &mut entries,
            &mut errors,
            &mut replaced,
            &mut skipped_meta,
            &mut skipped_new,
            &mut untouched,
            &mut skipped_duplicate,
            &mut failed,
        );
        (entries, replaced, skipped_meta, skipped_new, untouched, skipped_duplicate, failed, errors)
    }

    #[test]
    fn same_file_detects_identical_paths_and_hardlinks() {
        let tmp = tempfile::tempdir().unwrap();
        let a = tmp.path().join("a.txt");
        fs::write(&a, "hello").unwrap();
        // Same path → same file.
        assert!(same_file(&a, &a));
        // Distinct paths, distinct inodes → not same file.
        let b = tmp.path().join("b.txt");
        fs::write(&b, "hello").unwrap();
        assert!(!same_file(&a, &b));
        // Hardlink → same inode.
        #[cfg(unix)]
        {
            let h = tmp.path().join("link.txt");
            fs::hard_link(&a, &h).unwrap();
            assert!(same_file(&a, &h));
        }
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
        // package-only — must survive untouched
        fs::write(dst.join("keep-me.txt"), "untouched").unwrap();
        // source-only — must NOT be copied
        fs::write(src.join("brand-new.txt"), "src only").unwrap();

        let (entries, replaced, skipped_meta, skipped_new, untouched, skipped_dup, failed, errors) =
            run(&src, &dst, false);

        assert_eq!(replaced, 1);
        assert_eq!(skipped_meta, 0);
        assert_eq!(skipped_new, 1);
        assert_eq!(untouched, 1);
        assert_eq!(skipped_dup, 0);
        assert_eq!(failed, 0);
        assert!(errors.is_empty());
        assert_eq!(fs::read_to_string(dst.join("existing.txt")).unwrap(), "new");
        assert_eq!(fs::read_to_string(dst.join("keep-me.txt")).unwrap(), "untouched");
        assert!(!dst.join("brand-new.txt").exists());

        let by_action: HashMap<&str, usize> =
            entries.iter().fold(HashMap::new(), |mut acc, e| {
                *acc.entry(e.action.as_str()).or_insert(0) += 1;
                acc
            });
        assert_eq!(by_action["replaced"], 1);
        assert_eq!(by_action["untouched"], 1);
        assert_eq!(by_action["skipped-new"], 1);
    }

    #[test]
    fn matches_by_basename_even_when_folders_differ() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        // Same basename, different folder on each side.
        fs::create_dir_all(src.join("Editor")).unwrap();
        fs::create_dir_all(dst.join("Runtime")).unwrap();
        fs::write(src.join("Editor/Foo.cs"), "new").unwrap();
        fs::write(dst.join("Runtime/Foo.cs"), "old").unwrap();
        // Different basename in the same source folder — source-only.
        fs::write(src.join("Editor/Bar.cs"), "src only").unwrap();

        let (entries, replaced, _, skipped_new, untouched, _, _, _) = run(&src, &dst, false);

        assert_eq!(replaced, 1);
        assert_eq!(skipped_new, 1);
        assert_eq!(untouched, 0);
        // The package file at Runtime/Foo.cs is the one overwritten.
        assert_eq!(fs::read_to_string(dst.join("Runtime/Foo.cs")).unwrap(), "new");
        assert!(!dst.join("Editor/Bar.cs").exists());
        let replaced_entry = entries
            .iter()
            .find(|e| e.action == "replaced")
            .unwrap();
        assert_eq!(replaced_entry.rel_path, "Runtime/Foo.cs");
    }

    #[test]
    fn duplicate_basename_on_either_side_is_skipped_and_reported() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        // Duplicate on the SOURCE side: two Foo.cs in different folders.
        fs::create_dir_all(src.join("A")).unwrap();
        fs::create_dir_all(src.join("B")).unwrap();
        fs::write(src.join("A/Foo.cs"), "from A").unwrap();
        fs::write(src.join("B/Foo.cs"), "from B").unwrap();
        // One matching Foo.cs in the package — ambiguous which source
        // file to use, so NOTHING is replaced.
        fs::create_dir_all(&dst).unwrap();
        fs::write(dst.join("Foo.cs"), "untouched pkg").unwrap();
        // A clean, unique match that should still go through.
        fs::write(src.join("Unique.cs"), "u").unwrap();
        fs::write(dst.join("Unique.cs"), "old").unwrap();

        let (entries, replaced, _, _, _, skipped_dup, _, _) = run(&src, &dst, false);

        // Unique.cs still migrates; Foo.cs is reported as duplicate.
        assert_eq!(replaced, 1);
        assert_eq!(skipped_dup, 3); // 2 source occurrences + 1 package occurrence
        // Package Foo.cs is NOT overwritten (ambiguous).
        assert_eq!(fs::read_to_string(dst.join("Foo.cs")).unwrap(), "untouched pkg");
        // All three Foo.cs occurrences appear in the duplicate log.
        let dup_paths: Vec<&str> = entries
            .iter()
            .filter(|e| e.action == "skipped-duplicate")
            .map(|e| e.rel_path.as_str())
            .collect();
        assert_eq!(dup_paths.len(), 3);
        assert!(dup_paths.contains(&"A/Foo.cs"));
        assert!(dup_paths.contains(&"B/Foo.cs"));
        assert!(dup_paths.contains(&"Foo.cs"));
    }

    #[test]
    fn duplicate_basename_in_package_only_is_skipped() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        // One source Foo.cs, but TWO package Foo.cs — ambiguous which to
        // overwrite, so the source one is NOT copied.
        fs::create_dir_all(&src).unwrap();
        fs::write(src.join("Foo.cs"), "new").unwrap();
        fs::create_dir_all(dst.join("X")).unwrap();
        fs::create_dir_all(dst.join("Y")).unwrap();
        fs::write(dst.join("X/Foo.cs"), "pkg X").unwrap();
        fs::write(dst.join("Y/Foo.cs"), "pkg Y").unwrap();

        let (_, replaced, _, _, _, skipped_dup, _, _) = run(&src, &dst, false);

        assert_eq!(replaced, 0);
        assert_eq!(skipped_dup, 3); // 1 source + 2 package
        // Neither package file is touched.
        assert_eq!(fs::read_to_string(dst.join("X/Foo.cs")).unwrap(), "pkg X");
        assert_eq!(fs::read_to_string(dst.join("Y/Foo.cs")).unwrap(), "pkg Y");
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
        let (entries, replaced, skipped_meta, _, _, _, _, _) = run(&src, &dst, true);
        assert_eq!(replaced, 1);
        assert_eq!(skipped_meta, 1);
        assert_eq!(fs::read_to_string(dst.join("Foo.cs")).unwrap(), "new cs");
        assert_eq!(fs::read_to_string(dst.join("Foo.cs.meta")).unwrap(), "old meta");
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

        let (_, replaced, skipped_meta, _, _, _, _, _) = run(&src, &dst, false);
        assert_eq!(replaced, 1);
        assert_eq!(skipped_meta, 0);
        assert_eq!(fs::read_to_string(dst.join("Foo.cs.meta")).unwrap(), "new");
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

        let (entries, replaced, _, skipped_new, untouched, skipped_dup, _, _) =
            run(&src, &dst, false);
        assert_eq!(replaced, 1); // only Top.cs
        assert_eq!(skipped_new, 0);
        assert_eq!(untouched, 0);
        assert_eq!(skipped_dup, 0);
        assert!(entries.iter().all(|e| !e.rel_path.contains("Samples~")));
    }

    // H35: a failed `fs::copy` must be counted as `failed` and listed in
    // `errors` + entries, not silently dropped. The previous shape pushed
    // the error onto a local vec that was only `log::warn!`-ed, so the
    // truncated-to-0 destination appeared in NO counter and the UI showed
    // a clean "migrated N files" while data was lost. We make the dst
    // unwritable so `fs::copy`'s `O_WRONLY|O_CREAT|O_TRUNC` open fails;
    // this is reliable on Unix (chmod 0444 → EACCES on the open).
    #[cfg(unix)]
    #[test]
    fn failed_copy_is_counted_and_surfaced() {
        use std::os::unix::fs::PermissionsExt;
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dst).unwrap();
        fs::write(src.join("Shared.txt"), "new contents").unwrap();
        fs::write(dst.join("Shared.txt"), "old contents").unwrap();
        // Make the destination read-only so `fs::copy` cannot open it for
        // writing. The source remains readable.
        let mut perms = fs::metadata(dst.join("Shared.txt")).unwrap().permissions();
        perms.set_mode(0o444);
        fs::set_permissions(dst.join("Shared.txt"), perms).unwrap();

        let (entries, replaced, _, _, _, _, failed, errors) = run(&src, &dst, false);

        // The copy failed: nothing was replaced, but it IS counted.
        assert_eq!(replaced, 0);
        assert_eq!(failed, 1);
        // The error string is surfaced (not just logged).
        assert_eq!(errors.len(), 1);
        assert!(errors[0].contains("Shared.txt"));
        // A `failed` entry is recorded so the UI can list it.
        let failed_entry = entries
            .iter()
            .find(|e| e.action == "failed")
            .expect("a failed entry should be recorded");
        assert_eq!(failed_entry.rel_path, "Shared.txt");

        // Restore perms so tempdir cleanup can remove the file.
        let mut perms = fs::metadata(dst.join("Shared.txt")).unwrap().permissions();
        perms.set_mode(0o644);
        fs::set_permissions(dst.join("Shared.txt"), perms).unwrap();
    }
}
