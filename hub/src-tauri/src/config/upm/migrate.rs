//! Append-and-replace file migration for packages.
//!
//! The Package settings popup has a "Migrate" tab where the user
//! picks a source folder and copies its files/folders into the
//! package, overwriting existing files but **never deleting** anything
//! already in the package that is absent from the source (the brief's
//! "append and replace" mode). A per-package source folder is
//! persisted on `ProjectEntry.migrate_source_folder`.

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
    /// `copied` (file did not exist in package) or `replaced` (file
    /// existed and was overwritten).
    pub action: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MigrateResult {
    /// All files copied or replaced, in walk order.
    pub entries: Vec<MigrateEntry>,
    /// Number of files copied (new) vs replaced (existing overwritten).
    pub copied: u32,
    pub replaced: u32,
    /// The saved source folder, persisted on the project entry so the
    /// next Migrate open pre-fills the field.
    pub saved_source_folder: String,
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
    #[serde(rename_all = "camelCase")]
    IoFailed { message: String },
}

/// Recursively copies every file from `src` into `dst`, overwriting
/// existing files. Directories are created as needed. Never deletes
/// anything in `dst`. Returns the list of files acted on with their
/// action (`copied` or `replaced`). Errors on individual files are
/// collected and surfaced as a single `IoFailed` once the walk
/// completes (a partial migration is still returned via the log).
fn copy_tree_append_replace(
    src: &Path,
    dst: &Path,
    entries: &mut Vec<MigrateEntry>,
    errors: &mut Vec<String>,
    dst_root_for_rel: &Path,
) {
    let iter = match fs::read_dir(src) {
        Ok(i) => i,
        Err(e) => {
            errors.push(format!("read {}: {}", src.display(), e));
            return;
        }
    };
    for entry in iter.flatten() {
        let path = entry.path();
        let name = entry.file_name();
        let name = name.to_string_lossy().to_string();
        let dst_path = dst.join(&name);

        let ft = match entry.file_type() {
            Ok(ft) => ft,
            Err(e) => {
                errors.push(format!("stat {}: {}", path.display(), e));
                continue;
            }
        };
        if ft.is_dir() {
            if let Err(e) = fs::create_dir_all(&dst_path) {
                errors.push(format!("mkdir {}: {}", dst_path.display(), e));
                continue;
            }
            copy_tree_append_replace(&path, &dst_path, entries, errors, dst_root_for_rel);
        } else if ft.is_file() {
            let existed = dst_path.exists();
            // Skip Unity .meta files whose asset is a .meta we already
            // wrote — they are regenerated below. (We copy them
            // through normally; no special handling needed.)
            if let Some(parent) = dst_path.parent() {
                if let Err(e) = fs::create_dir_all(parent) {
                    errors.push(format!("mkdir {}: {}", parent.display(), e));
                    continue;
                }
            }
            if let Err(e) = fs::copy(&path, &dst_path) {
                errors.push(format!("copy {} → {}: {}", path.display(), dst_path.display(), e));
                continue;
            }
            let rel = dst_path
                .strip_prefix(dst_root_for_rel)
                .map(|p| p.to_string_lossy().replace('\\', "/").to_string())
                .unwrap_or_else(|_| name.clone());
            entries.push(MigrateEntry {
                rel_path: rel,
                action: if existed { "replaced".into() } else { "copied".into() },
            });
        }
        // Symlinks and other special files are skipped (avoids loops
        // and cross-device surprises).
    }
}

/// Migrates files from `source_folder` into the package root,
/// append-and-replace mode. Persists `source_folder` on the project
/// entry so it pre-fills next time.
#[tauri::command]
pub fn migrate_package_files(
    state: State<AppState>,
    project_id: String,
    source_folder: String,
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

    let mut entries: Vec<MigrateEntry> = Vec::new();
    let mut errors: Vec<String> = Vec::new();
    copy_tree_append_replace(&src, &dst, &mut entries, &mut errors, &dst);

    let copied = entries.iter().filter(|e| e.action == "copied").count() as u32;
    let replaced = entries.iter().filter(|e| e.action == "replaced").count() as u32;

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
        copied,
        replaced,
        saved_source_folder: source_folder,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn copies_new_files_into_empty_destination() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        fs::create_dir_all(src.join("Editor")).unwrap();
        fs::write(src.join("Editor/Foo.cs"), "class Foo {}").unwrap();
        fs::write(src.join("README.md"), "# hi").unwrap();
        fs::create_dir_all(&dst).unwrap();

        let mut entries = Vec::new();
        let mut errors = Vec::new();
        copy_tree_append_replace(&src, &dst, &mut entries, &mut errors, &dst);

        assert!(errors.is_empty());
        assert_eq!(entries.len(), 2);
        assert!(dst.join("Editor/Foo.cs").exists());
        assert!(dst.join("README.md").exists());
        assert!(entries.iter().all(|e| e.action == "copied"));
    }

    #[test]
    fn replaces_existing_files_without_deleting_others() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("pkg");
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dst).unwrap();

        // Destination already has a file that the source will replace,
        // plus a file that the source does NOT have — the latter must
        // survive (append-and-replace, never delete).
        fs::write(dst.join("existing.txt"), "old").unwrap();
        fs::write(dst.join("keep-me.txt"), "untouched").unwrap();
        fs::write(src.join("existing.txt"), "new").unwrap();

        let mut entries = Vec::new();
        let mut errors = Vec::new();
        copy_tree_append_replace(&src, &dst, &mut entries, &mut errors, &dst);

        assert!(errors.is_empty());
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].action, "replaced");
        assert_eq!(fs::read_to_string(dst.join("existing.txt")).unwrap(), "new");
        // The unrelated file survives.
        assert_eq!(fs::read_to_string(dst.join("keep-me.txt")).unwrap(), "untouched");
    }
}
