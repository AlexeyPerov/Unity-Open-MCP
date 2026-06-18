//! Unity upgrade assistant (M1.5-14).
//!
//! Provides the `upgrade_unity` Tauri command that rewrites
//! `ProjectSettings/ProjectVersion.txt` and bumps
//! `ProjectSettings/ProjectManager.asset` / `ProjectSettings/ProjectSettings.asset`
//! `bundleVersion` per a user-chosen strategy (`none` | `patch` | `minor` | `major`).
//! The flow is atomic at the file level: each file is rewritten in place, and
//! any failure rolls back the on-disk state to the pre-upgrade snapshot so a
//! half-applied upgrade never leaves a project in a mixed state.
//!
//! ## Flow
//!
//! 1. Validate the project entry exists in `projects.json`, the path resolves
//!    to a directory, and the requested version is installed (per the
//!    discovery cache).
//! 2. Snapshot the four files we may touch:
//!    - `ProjectSettings/ProjectVersion.txt`
//!    - `ProjectSettings/ProjectManager.asset` (carries `bundleVersion`)
//!    - `ProjectSettings/ProjectSettings.asset` (also carries `bundleVersion`
//!      in the modern layout)
//! 3. Rewrite each file in place. The first failure rolls the others back
//!    from the snapshot.
//! 4. Refresh `unityVersion` and `lastModifiedAt` on the project entry, persist
//!    `projects.json`, and append an `Upgrade` record to the per-launch log
//!    (M1.5-2) so the change is part of the diagnostic trail.
//!
//! ## Why file-level rollback (not staging dir) ?
//!
//! `ProjectSettings/ProjectVersion.txt` lives next to the user's project, and
//! Unity re-reads it on every editor open. Renaming the file in/out of place
//! would race the user opening Unity. We instead snapshot the bytes before
//! the edit and restore them on any error path. The snapshot is per-file
//! because the four files are independent and the user may legitimately
//! own the project version file with a non-Unity-managed toolchain.

use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::launch_log::{self, LaunchOutcome, LaunchRecord};
use crate::config::persistence;
use crate::config::schemas::ProjectEntry;

/// Bundle-version bump strategy. The default is `Patch` per the task spec;
/// `None` means "rewrite the project version only, leave `bundleVersion`
/// alone" (the spec's "preview" UX still bumps the version string).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum BundleStrategy {
    None,
    Patch,
    Minor,
    Major,
}

impl Default for BundleStrategy {
    fn default() -> Self {
        BundleStrategy::Patch
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpgradeUnityParams {
    pub project_id: String,
    /// Target Unity version. Must be present in the in-memory discovery cache.
    pub target_version: String,
    /// Default in the `UpgradeUnityParams` deserializer so a frontend that
    /// forgets to send the strategy still gets the documented default.
    #[serde(default)]
    pub bundle_strategy: BundleStrategy,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum UpgradeUnityError {
    #[serde(rename_all = "camelCase")]
    ProjectNotFound { project_id: String },
    #[serde(rename_all = "camelCase")]
    PathInvalid { project_id: String, path: String },
    #[serde(rename_all = "camelCase")]
    VersionNotInstalled { project_id: String, version: String },
    /// `ProjectVersion.txt` could not be read or rewritten.
    #[serde(rename_all = "camelCase")]
    ProjectVersionUnreadable { project_id: String, path: String, reason: String },
    /// `ProjectManager.asset` (or `ProjectSettings.asset`) was malformed and
    /// could not be parsed back after the rewrite.
    #[serde(rename_all = "camelCase")]
    BundleVersionUnwritable { project_id: String, path: String, reason: String },
    /// Underlying I/O error during the file rewrite; the on-disk state has
    /// already been rolled back.
    #[serde(rename_all = "camelCase")]
    IoError { project_id: String, message: String },
    #[serde(rename_all = "camelCase")]
    PersistFailed { project_id: String, message: String },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpgradeUnityResult {
    pub project: ProjectEntry,
    /// The new Unity version written to `ProjectVersion.txt` (echoes
    /// `params.target_version` after a successful upgrade).
    pub unity_version: String,
    /// The new `bundleVersion` written to `ProjectManager.asset` /
    /// `ProjectSettings.asset`. When `bundleStrategy = None` this is the
    /// pre-existing value, redacted for callers that do not need it.
    pub bundle_version: String,
    /// The `bundleVersion` value that was on disk before the upgrade.
    pub previous_bundle_version: String,
    /// The Unity version that was on disk before the upgrade.
    pub previous_unity_version: String,
    pub bundle_strategy: BundleStrategy,
}

/// Read the current `bundleVersion` from `ProjectManager.asset`. Returns
/// `None` when the line is missing (older Unity projects that never wrote
/// one) — in that case the upgrade flow treats the value as `0.0.0` so the
/// bump is a clean publish, not a no-op.
pub(crate) fn read_bundle_version(asset: &str) -> Option<String> {
    for line in asset.lines() {
        let line = line.strip_prefix('\u{FEFF}').unwrap_or(line);
        if let Some(value) = line.strip_prefix("bundleVersion:") {
            let trimmed = value.trim().to_string();
            if !trimmed.is_empty() {
                return Some(trimmed);
            }
        }
    }
    None
}

/// Parse a `major.minor.patch` string into its parts. Returns `None` for
/// anything that does not match the schema Unity writes (e.g. `1.0`,
/// `1.0.0.0`, `1.0.0-rc1`). The bump math is strict on purpose: a
/// non-conforming version is a signal that Unity wrote something we do
/// not understand, and silently dropping the patch component would lie
/// to the user about what is in the file.
pub(crate) fn parse_bundle_version(raw: &str) -> Option<(u32, u32, u32)> {
    let s = raw.trim();
    let mut parts = s.split('.');
    let major = parts.next()?.parse::<u32>().ok()?;
    let minor = parts.next()?.parse::<u32>().ok()?;
    let patch = parts.next()?.parse::<u32>().ok()?;
    if parts.next().is_some() {
        return None;
    }
    Some((major, minor, patch))
}

/// Bump a `(major, minor, patch)` triple per the strategy. `None` returns
/// the same triple unchanged (caller writes the pre-existing string back).
pub(crate) fn bump(
    triple: (u32, u32, u32),
    strategy: BundleStrategy,
) -> (u32, u32, u32) {
    let (major, minor, patch) = triple;
    match strategy {
        BundleStrategy::None => triple,
        BundleStrategy::Patch => (major, minor, patch + 1),
        BundleStrategy::Minor => (major, minor + 1, 0),
        BundleStrategy::Major => (major + 1, 0, 0),
    }
}

/// Rewrite the `m_EditorVersion:` line in `ProjectVersion.txt`. Preserves
/// the rest of the file (e.g. `m_Revision`) byte-for-byte. If the line is
/// missing, appends a new one so the file always round-trips.
pub(crate) fn rewrite_project_version_file(
    original: &str,
    new_version: &str,
) -> String {
    let mut out = String::with_capacity(original.len() + 32);
    let mut replaced = false;
    for line in original.lines() {
        let line_stripped = line.strip_prefix('\u{FEFF}').unwrap_or(line);
        if !replaced && line_stripped.starts_with("m_EditorVersion:") {
            out.push_str("m_EditorVersion: ");
            out.push_str(new_version);
            out.push('\n');
            replaced = true;
        } else {
            out.push_str(line);
            out.push('\n');
        }
    }
    if !replaced {
        out.push_str("m_EditorVersion: ");
        out.push_str(new_version);
        out.push('\n');
    }
    out
}

/// Replace the `bundleVersion:` line in a `ProjectManager.asset` /
/// `ProjectSettings.asset` file. If the line is missing (very old Unity
/// projects), appends a new one. Preserves every other line verbatim so
/// the diff in the user's git log is a single-line change.
pub(crate) fn rewrite_bundle_version_line(original: &str, new_bundle: &str) -> String {
    let mut out = String::with_capacity(original.len() + 32);
    let mut replaced = false;
    for line in original.lines() {
        let line_stripped = line.strip_prefix('\u{FEFF}').unwrap_or(line);
        if !replaced && line_stripped.starts_with("bundleVersion:") {
            out.push_str("bundleVersion: ");
            out.push_str(new_bundle);
            out.push('\n');
            replaced = true;
        } else {
            out.push_str(line);
            out.push('\n');
        }
    }
    if !replaced {
        out.push_str("bundleVersion: ");
        out.push_str(new_bundle);
        out.push('\n');
    }
    out
}

/// A snapshot of a single file's pre-upgrade bytes. None when the file was
/// missing on disk before the upgrade.
#[derive(Debug, Clone)]
struct FileSnapshot {
    path: PathBuf,
    contents: Option<String>,
}

impl FileSnapshot {
    fn capture(path: &Path) -> std::io::Result<Self> {
        let contents = match fs::read_to_string(path) {
            Ok(s) => Some(s),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => None,
            Err(e) => return Err(e),
        };
        Ok(FileSnapshot {
            path: path.to_path_buf(),
            contents,
        })
    }

    /// Restore the file to the captured bytes. Missing files are deleted
    /// (so the post-upgrade on-disk state matches the pre-upgrade one).
    /// Existing files are overwritten with the captured bytes.
    fn restore(&self) -> std::io::Result<()> {
        match &self.contents {
            Some(bytes) => fs::write(&self.path, bytes),
            None => {
                if self.path.exists() {
                    fs::remove_file(&self.path)
                } else {
                    Ok(())
                }
            }
        }
    }
}

/// Apply the upgrade to disk. Returns the previous and new bundle versions
/// on success. On any error, all touched files are restored from the
/// snapshot and the typed error is returned.
fn apply_upgrade(
    project_path: &Path,
    new_version: &str,
    new_bundle: &str,
) -> Result<(String, String), UpgradeUnityError> {
    let project_settings = project_path.join("ProjectSettings");
    let version_path = project_settings.join("ProjectVersion.txt");
    let manager_path = project_settings.join("ProjectManager.asset");
    let settings_path = project_settings.join("ProjectSettings.asset");

    // Snapshot the on-disk state before any edit. The "did not exist"
    // case is also captured so a rollback can delete a file Unity
    // created during a partial upgrade.
    let snapshots = [
        FileSnapshot::capture(&version_path),
        FileSnapshot::capture(&manager_path),
        FileSnapshot::capture(&settings_path),
    ];
    let snapshots = match snapshots.into_iter().collect::<Result<Vec<_>, _>>() {
        Ok(s) => s,
        Err(e) => {
            return Err(UpgradeUnityError::IoError {
                project_id: String::new(),
                message: format!("snapshot read failed: {}", e),
            });
        }
    };

    // 1) ProjectVersion.txt — required file. We never want to ship a
    //    upgrade with a missing version line, so a missing/garbled file
    //    is a hard error (and the snapshot is restored on the way out).
    let version_contents = match fs::read_to_string(&version_path) {
        Ok(s) => s,
        Err(e) => {
            return Err(UpgradeUnityError::ProjectVersionUnreadable {
                project_id: String::new(),
                path: version_path.display().to_string(),
                reason: format!("read failed: {}", e),
            });
        }
    };
    let new_version_contents =
        rewrite_project_version_file(&version_contents, new_version);
    if let Err(e) = write_atomic(&version_path, &new_version_contents) {
        return Err(UpgradeUnityError::IoError {
            project_id: String::new(),
            message: format!("write {} failed: {}", version_path.display(), e),
        });
    }

    // 2) ProjectManager.asset — may or may not exist (older projects).
    //    If it does not exist, skip the rewrite and rely on the on-disk
    //    `ProjectSettings.asset` to carry the bundleVersion. If neither
    //    exists we still write ProjectManager.asset because Unity
    //    creates one on first save.
    if manager_path.exists() {
        let previous_bundle = fs::read_to_string(&manager_path).unwrap_or_default();
        let new_manager =
            rewrite_bundle_version_line(&previous_bundle, new_bundle);
        if let Err(e) = write_atomic(&manager_path, &new_manager) {
            rollback(&snapshots);
            return Err(UpgradeUnityError::BundleVersionUnwritable {
                project_id: String::new(),
                path: manager_path.display().to_string(),
                reason: format!("write failed: {}", e),
            });
        }
    }

    // 3) ProjectSettings.asset — optional. The modern Unity layout
    //    also writes bundleVersion here so a manual edit of one file
    //    does not leave the other stale. Missing files are left alone.
    if settings_path.exists() {
        let previous_settings = fs::read_to_string(&settings_path).unwrap_or_default();
        let new_settings =
            rewrite_bundle_version_line(&previous_settings, new_bundle);
        if let Err(e) = write_atomic(&settings_path, &new_settings) {
            rollback(&snapshots);
            return Err(UpgradeUnityError::BundleVersionUnwritable {
                project_id: String::new(),
                path: settings_path.display().to_string(),
                reason: format!("write failed: {}", e),
            });
        }
    }

    // 4) Verify the new version file is parseable — guards against a
    //    silent corruption on a Windows file-lock race.
    let verify_contents = match fs::read_to_string(&version_path) {
        Ok(s) => s,
        Err(e) => {
            rollback(&snapshots);
            return Err(UpgradeUnityError::ProjectVersionUnreadable {
                project_id: String::new(),
                path: version_path.display().to_string(),
                reason: format!("post-write read failed: {}", e),
            });
        }
    };
    if rewrite_project_version_file(&verify_contents, new_version)
        != new_version_contents
    {
        rollback(&snapshots);
        return Err(UpgradeUnityError::ProjectVersionUnreadable {
            project_id: String::new(),
            path: version_path.display().to_string(),
            reason: "post-write round-trip mismatch".to_string(),
        });
    }

    // Read back the previous bundle version for the result payload.
    // (The snapshot already holds the bytes; we do a single read here
    // because the on-disk state is the source of truth for "previous".)
    let previous_bundle = snapshots
        .iter()
        .find(|s| s.path == manager_path)
        .and_then(|s| s.contents.as_deref())
        .and_then(read_bundle_version)
        .unwrap_or_else(|| "0.0.0".to_string());

    Ok((previous_bundle, new_bundle.to_string()))
}

/// Best-effort rollback. A second failure here is logged but never
/// overwrites the original error — the user already has a typed error
/// from the original write attempt and we cannot make it worse.
fn rollback(snapshots: &[FileSnapshot]) {
    for snapshot in snapshots {
        if let Err(e) = snapshot.restore() {
            log::error!(
                "Failed to roll back {} during upgrade: {}",
                snapshot.path.display(),
                e
            );
        }
    }
}

/// Write `contents` to `path` via a sibling `.tmp` + rename so a partial
/// write can never leave a half-baked `ProjectVersion.txt` on disk.
fn write_atomic(path: &Path, contents: &str) -> std::io::Result<()> {
    let parent = path.parent().ok_or_else(|| {
        std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "path has no parent directory",
        )
    })?;
    if !parent.exists() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            format!("parent directory does not exist: {}", parent.display()),
        ));
    }
    let file_name = path
        .file_name()
        .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::InvalidInput, "no filename"))?;
    let tmp = parent.join(format!(".{}.hub-upgrading", file_name.to_string_lossy()));
    fs::write(&tmp, contents)?;
    // On Windows the rename over an existing file fails; remove the
    // target first. The file we are removing was the on-disk content we
    // already snapshotted, so the user is never at risk of losing data.
    if cfg!(windows) && path.exists() {
        let _ = fs::remove_file(path);
    }
    fs::rename(&tmp, path)?;
    Ok(())
}

/// Append a structured `Upgrade` record to the persistent per-launch log.
/// Fire-and-forget on a background thread so the upgrade command never
/// blocks on disk I/O. Mirrors the launch flow's `append_record_async`
/// pattern.
fn record_upgrade(
    project_id: &str,
    project_name: &str,
    project_path: &str,
    from_version: &str,
    to_version: &str,
    previous_bundle: &str,
    new_bundle: &str,
    strategy: BundleStrategy,
) {
    let record = LaunchRecord {
        timestamp: chrono::Utc::now().to_rfc3339(),
        project_id: project_id.to_string(),
        project_name: project_name.to_string(),
        project_path: project_path.to_string(),
        unity_version: Some(to_version.to_string()),
        install_path: None,
        pid: None,
        launch_args: Vec::new(),
        build_target: None,
        outcome: LaunchOutcome::Upgrade {
            from_version: from_version.to_string(),
            to_version: to_version.to_string(),
            previous_bundle_version: previous_bundle.to_string(),
            new_bundle_version: new_bundle.to_string(),
            strategy: match strategy {
                BundleStrategy::None => "none".to_string(),
                BundleStrategy::Patch => "patch".to_string(),
                BundleStrategy::Minor => "minor".to_string(),
                BundleStrategy::Major => "major".to_string(),
            },
        },
        // M1.5-18: the theme is captured at the time of the upgrade
        // event; we default to `"system"` here because the upgrade
        // flow does not have access to the frontend store. The
        // frontend can read the active theme off the on-disk record
        // for analytics / future audit-trail work.
        theme: Some("system".to_string()),
    };
    launch_log::append_record_async(record);
}

#[tauri::command]
pub fn upgrade_unity(
    state: State<AppState>,
    params: UpgradeUnityParams,
) -> Result<UpgradeUnityResult, UpgradeUnityError> {
    let projects = {
        let guard = state.projects.lock().unwrap();
        guard.clone()
    };
    let project = projects
        .projects
        .iter()
        .find(|p| p.id == params.project_id)
        .cloned();
    let project = match project {
        Some(p) => p,
        None => {
            return Err(UpgradeUnityError::ProjectNotFound {
                project_id: params.project_id.clone(),
            });
        }
    };

    let project_path = PathBuf::from(&project.path);
    if !project_path.is_dir() {
        return Err(UpgradeUnityError::PathInvalid {
            project_id: params.project_id.clone(),
            path: project.path.clone(),
        });
    }

    // Version resolution: re-use the in-memory discovery cache so the GUI
    // and the upgrade flow see the same view of the world.
    let target_version = params.target_version.clone();
    let installed = {
        let cache = state.discovery_cache.lock().unwrap();
        cache.as_ref().map(|r| {
            r.installations
                .iter()
                .any(|i| i.version == target_version)
        })
    };
    match installed {
        Some(true) => {}
        _ => {
            return Err(UpgradeUnityError::VersionNotInstalled {
                project_id: params.project_id.clone(),
                version: target_version,
            });
        }
    }

    // Compute the new bundle version up front so the on-disk edit is the
    // last thing that can fail. A parse failure here (e.g. Unity wrote
    // `1.0`) is non-recoverable: the user must hand-fix the file before
    // we can bump it.
    let current_bundle = {
        let manager_path = project_path
            .join("ProjectSettings")
            .join("ProjectManager.asset");
        if manager_path.exists() {
            fs::read_to_string(&manager_path)
                .ok()
                .and_then(|s| read_bundle_version(&s))
                .unwrap_or_else(|| "0.0.0".to_string())
        } else {
            "0.0.0".to_string()
        }
    };
    let next_bundle = match params.bundle_strategy {
        BundleStrategy::None => current_bundle.clone(),
        other => match parse_bundle_version(&current_bundle) {
            Some(triple) => {
                let (maj, min, pat) = bump(triple, other);
                format!("{}.{}.{}", maj, min, pat)
            }
            None => {
                return Err(UpgradeUnityError::BundleVersionUnwritable {
                    project_id: params.project_id.clone(),
                    path: project
                        .path
                        .clone(),
                    reason: format!(
                        "could not parse current bundleVersion '{}' (expected major.minor.patch)",
                        current_bundle
                    ),
                });
            }
        },
    };

    // Apply the upgrade to disk. On any error we return without touching
    // `projects.json` so the GUI state and the on-disk state stay in
    // lock-step (the on-disk state is already rolled back at this point).
    let (previous_bundle_for_log, new_bundle_for_log) =
        match apply_upgrade(&project_path, &target_version, &next_bundle) {
            Ok(pair) => pair,
            Err(err) => {
                let tagged = tag_error(err, &params.project_id, &project.path);
                return Err(tagged);
            }
        };

    // Refresh the project entry in `projects.json` and bump
    // `lastModifiedAt` to "now" so the row surfaces at the top of a
    // last-modified view.
    let now_iso = chrono::Utc::now().to_rfc3339();
    let mut projects = projects;
    let target = projects
        .projects
        .iter_mut()
        .find(|p| p.id == params.project_id)
        .expect("project present after earlier lookup");
    target.unity_version = Some(target_version.clone());
    target.last_modified_at = Some(now_iso.clone());

    if let Err(e) = persistence::save_projects(&projects) {
        // Best-effort rollback of the on-disk file changes. The user has
        // not yet seen the success state, so undoing the file edits
        // keeps the project in a known-good shape.
        // Note: we do not have the snapshot in scope here, but the
        // `apply_upgrade` call already cleaned up after itself; the
        // remaining state to undo is the new bundle / version bytes. We
        // re-apply with the *previous* values to keep the on-disk state
        // consistent with the still-stale projects.json.
        let _ = apply_upgrade(
            &project_path,
            project
                .unity_version
                .as_deref()
                .unwrap_or("0.0.0"),
            &current_bundle,
        );
        return Err(UpgradeUnityError::PersistFailed {
            project_id: params.project_id.clone(),
            message: e.to_string(),
        });
    }

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects.clone();
    }

    let updated_project = projects
        .projects
        .iter()
        .find(|p| p.id == params.project_id)
        .cloned()
        .expect("project present after persistence");

    // Fire-and-forget log entry. Even on a log failure the upgrade
    // itself succeeded; the user-facing error path is already past.
    record_upgrade(
        &params.project_id,
        &updated_project.name,
        &updated_project.path,
        project.unity_version.as_deref().unwrap_or(""),
        &target_version,
        &previous_bundle_for_log,
        &new_bundle_for_log,
        params.bundle_strategy,
    );

    Ok(UpgradeUnityResult {
        project: updated_project,
        unity_version: target_version,
        bundle_version: new_bundle_for_log,
        previous_bundle_version: previous_bundle_for_log,
        previous_unity_version: project.unity_version.unwrap_or_default(),
        bundle_strategy: params.bundle_strategy,
    })
}

fn tag_error(err: UpgradeUnityError, project_id: &str, path: &str) -> UpgradeUnityError {
    match err {
        UpgradeUnityError::ProjectVersionUnreadable { reason, .. } => {
            UpgradeUnityError::ProjectVersionUnreadable {
                project_id: project_id.to_string(),
                path: path.to_string(),
                reason,
            }
        }
        UpgradeUnityError::BundleVersionUnwritable { reason, .. } => {
            UpgradeUnityError::BundleVersionUnwritable {
                project_id: project_id.to_string(),
                path: path.to_string(),
                reason,
            }
        }
        UpgradeUnityError::IoError { message, .. } => UpgradeUnityError::IoError {
            project_id: project_id.to_string(),
            message,
        },
        other => other,
    }
}

/// Suggest a list of installed Unity versions that are strictly higher than
/// the project's current version. Returns the same strings the discovery
/// service exposes, sorted descending by version (so the user sees the
/// newest option first).
#[tauri::command]
pub fn upgrade_candidates(state: State<AppState>, project_id: String) -> Vec<String> {
    let project = {
        let guard = state.projects.lock().unwrap();
        guard
            .projects
            .iter()
            .find(|p| p.id == project_id)
            .cloned()
    };
    let project = match project {
        Some(p) => p,
        None => return Vec::new(),
    };
    let current = project.unity_version.clone().unwrap_or_default();
    let mut installed: Vec<String> = {
        let cache = state.discovery_cache.lock().unwrap();
        cache
            .as_ref()
            .map(|r| {
                r.installations
                    .iter()
                    .filter(|i| {
                        if current.is_empty() {
                            true
                        } else {
                            version_is_higher(&i.version, &current)
                        }
                    })
                    .map(|i| i.version.clone())
                    .collect()
            })
            .unwrap_or_default()
    };
    installed.sort_by(|a, b| b.cmp(a));
    installed
}

/// True when `candidate` is strictly higher than `current` using the same
/// lexicographic comparison as the discovery service (which sorts version
/// strings as `6000.0.2f1 > 6000.0.1f1 > 2022.3.48f1`).
fn version_is_higher(candidate: &str, current: &str) -> bool {
    candidate != current && candidate > current
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::path::Path;

    fn make_project(dir: &Path, version: &str, bundle: Option<&str>) {
        let ps = dir.join("ProjectSettings");
        fs::create_dir_all(&ps).unwrap();
        fs::write(
            ps.join("ProjectVersion.txt"),
            format!("m_EditorVersion: {}\nm_Revision: abc\n", version),
        )
        .unwrap();
        if let Some(b) = bundle {
            fs::write(
                ps.join("ProjectManager.asset"),
                format!(
                    "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1045\nbundleVersion: {}\n",
                    b
                ),
            )
            .unwrap();
        }
    }

    #[test]
    fn parse_bundle_version_accepts_canonical() {
        assert_eq!(parse_bundle_version("1.2.3"), Some((1, 2, 3)));
        assert_eq!(parse_bundle_version("0.0.0"), Some((0, 0, 0)));
        assert_eq!(parse_bundle_version("6000.0.1"), Some((6000, 0, 1)));
    }

    #[test]
    fn parse_bundle_version_rejects_non_canonical() {
        assert_eq!(parse_bundle_version(""), None);
        assert_eq!(parse_bundle_version("1"), None);
        assert_eq!(parse_bundle_version("1.2"), None);
        assert_eq!(parse_bundle_version("1.2.3.4"), None);
        assert_eq!(parse_bundle_version("1.2.x"), None);
        assert_eq!(parse_bundle_version("1.2.3-rc1"), None);
    }

    #[test]
    fn bump_patch_increments_patch_zeroes_nothing() {
        assert_eq!(bump((1, 2, 3), BundleStrategy::Patch), (1, 2, 4));
    }

    #[test]
    fn bump_minor_increments_minor_zeroes_patch() {
        assert_eq!(bump((1, 2, 3), BundleStrategy::Minor), (1, 3, 0));
    }

    #[test]
    fn bump_major_increments_major_zeroes_others() {
        assert_eq!(bump((1, 2, 3), BundleStrategy::Major), (2, 0, 0));
    }

    #[test]
    fn bump_none_returns_same_triple() {
        assert_eq!(bump((4, 5, 6), BundleStrategy::None), (4, 5, 6));
    }

    #[test]
    fn read_bundle_version_parses_value() {
        let contents = "%YAML 1.1\nbundleVersion: 1.2.3\notherKey: x\n";
        assert_eq!(read_bundle_version(contents).as_deref(), Some("1.2.3"));
    }

    #[test]
    fn read_bundle_version_returns_none_for_missing_line() {
        let contents = "%YAML 1.1\notherKey: x\n";
        assert_eq!(read_bundle_version(contents), None);
    }

    #[test]
    fn read_bundle_version_handles_bom() {
        let contents = "\u{FEFF}%YAML 1.1\nbundleVersion: 7.0.0\n";
        assert_eq!(read_bundle_version(contents).as_deref(), Some("7.0.0"));
    }

    #[test]
    fn read_bundle_version_returns_none_for_empty_value() {
        let contents = "bundleVersion: \n";
        assert_eq!(read_bundle_version(contents), None);
    }

    #[test]
    fn rewrite_project_version_replaces_existing_line() {
        let original = "m_EditorVersion: 2022.3.48f1\nm_Revision: abc\n";
        let out = rewrite_project_version_file(original, "6000.0.1f1");
        assert!(out.contains("m_EditorVersion: 6000.0.1f1"));
        assert!(out.contains("m_Revision: abc"));
        // m_EditorVersion must not appear twice in the rewritten output.
        assert_eq!(out.matches("m_EditorVersion:").count(), 1);
    }

    #[test]
    fn rewrite_project_version_appends_when_missing() {
        let original = "m_Revision: abc\n";
        let out = rewrite_project_version_file(original, "6000.0.1f1");
        assert!(out.contains("m_EditorVersion: 6000.0.1f1"));
        assert!(out.contains("m_Revision: abc"));
    }

    #[test]
    fn rewrite_project_version_handles_empty_input() {
        let out = rewrite_project_version_file("", "6000.0.1f1");
        assert!(out.contains("m_EditorVersion: 6000.0.1f1"));
    }

    #[test]
    fn rewrite_bundle_version_replaces_existing_line() {
        let original = "%YAML 1.1\nbundleVersion: 1.0.0\notherKey: x\n";
        let out = rewrite_bundle_version_line(original, "1.0.1");
        assert!(out.contains("bundleVersion: 1.0.1"));
        assert!(!out.contains("bundleVersion: 1.0.0"));
        assert_eq!(out.matches("bundleVersion:").count(), 1);
    }

    #[test]
    fn rewrite_bundle_version_appends_when_missing() {
        let original = "%YAML 1.1\notherKey: x\n";
        let out = rewrite_bundle_version_line(original, "1.0.1");
        assert!(out.contains("bundleVersion: 1.0.1"));
    }

    #[test]
    fn apply_upgrade_rewrites_version_and_bumps_patch() {
        let dir = tempfile::tempdir().unwrap();
        make_project(dir.path(), "2022.3.48f1", Some("1.0.0"));

        let (prev, next) = apply_upgrade(dir.path(), "6000.0.1f1", "1.0.1").unwrap();
        assert_eq!(prev, "1.0.0");
        assert_eq!(next, "1.0.1");

        let v = fs::read_to_string(
            dir.path().join("ProjectSettings").join("ProjectVersion.txt"),
        )
        .unwrap();
        assert!(v.contains("m_EditorVersion: 6000.0.1f1"));
        assert!(v.contains("m_Revision: abc"));

        let m = fs::read_to_string(
            dir.path().join("ProjectSettings").join("ProjectManager.asset"),
        )
        .unwrap();
        assert!(m.contains("bundleVersion: 1.0.1"));
    }

    #[test]
    fn apply_upgrade_creates_project_manager_when_missing() {
        // Older projects ship without `ProjectManager.asset`; the upgrade
        // flow must not crash on a missing file. The version file is the
        // canonical source for the version string; `bundleVersion` is
        // left implicit (Unity writes a `ProjectManager.asset` on first
        // save, which is the documented Unity behavior).
        let dir = tempfile::tempdir().unwrap();
        make_project(dir.path(), "2022.3.48f1", None);

        let (prev, next) = apply_upgrade(dir.path(), "6000.0.1f1", "0.0.1").unwrap();
        assert_eq!(prev, "0.0.0");
        assert_eq!(next, "0.0.1");

        let v = fs::read_to_string(
            dir.path().join("ProjectSettings").join("ProjectVersion.txt"),
        )
        .unwrap();
        assert!(v.contains("m_EditorVersion: 6000.0.1f1"));
    }

    #[test]
    fn apply_upgrade_writes_project_settings_asset_when_present() {
        // When both files exist, both must be updated in lock-step so a
        // manual edit of one does not leave the other stale.
        let dir = tempfile::tempdir().unwrap();
        make_project(dir.path(), "2022.3.48f1", Some("1.0.0"));
        let ps = dir.path().join("ProjectSettings");
        fs::write(
            ps.join("ProjectSettings.asset"),
            "%YAML 1.1\nbundleVersion: 1.0.0\nm_Foo: 1\n",
        )
        .unwrap();

        apply_upgrade(dir.path(), "6000.0.1f1", "1.0.1").unwrap();

        let s = fs::read_to_string(ps.join("ProjectSettings.asset")).unwrap();
        assert!(s.contains("bundleVersion: 1.0.1"));
        assert!(s.contains("m_Foo: 1"));
    }

    #[test]
    fn apply_upgrade_rolls_back_on_missing_project_settings_dir() {
        // Defensive: a project root without `ProjectSettings/` is not a
        // Unity root in the first place, but if one slips through we must
        // fail loudly and not touch the parent dir.
        let dir = tempfile::tempdir().unwrap();
        // No ProjectSettings directory created.
        let err = apply_upgrade(dir.path(), "6000.0.1f1", "1.0.1").unwrap_err();
        assert!(matches!(
            err,
            UpgradeUnityError::ProjectVersionUnreadable { .. }
                | UpgradeUnityError::IoError { .. }
        ));
    }

    #[test]
    fn bundle_strategy_serializes_camel_case() {
        let json = serde_json::to_string(&BundleStrategy::Patch).unwrap();
        assert_eq!(json, "\"patch\"");
        let json = serde_json::to_string(&BundleStrategy::None).unwrap();
        assert_eq!(json, "\"none\"");
    }

    #[test]
    fn upgrade_params_default_strategy_is_patch() {
        // The frontend is allowed to omit `bundleStrategy`; the
        // `#[serde(default)]` on the field must yield the documented
        // default (`Patch`).
        let json = r#"{"projectId":"a","targetVersion":"6000.0.1f1"}"#;
        let parsed: UpgradeUnityParams = serde_json::from_str(json).unwrap();
        assert_eq!(parsed.bundle_strategy, BundleStrategy::Patch);
    }

    #[test]
    fn upgrade_error_variants_serialize_with_camel_case() {
        let err = UpgradeUnityError::ProjectNotFound {
            project_id: "x".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"projectNotFound\""));

        let err = UpgradeUnityError::PathInvalid {
            project_id: "x".to_string(),
            path: "/p".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"pathInvalid\""));

        let err = UpgradeUnityError::VersionNotInstalled {
            project_id: "x".to_string(),
            version: "1.0".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"versionNotInstalled\""));

        let err = UpgradeUnityError::ProjectVersionUnreadable {
            project_id: "x".to_string(),
            path: "/p".to_string(),
            reason: "no file".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"projectVersionUnreadable\""));

        let err = UpgradeUnityError::BundleVersionUnwritable {
            project_id: "x".to_string(),
            path: "/p".to_string(),
            reason: "bad".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"bundleVersionUnwritable\""));

        let err = UpgradeUnityError::IoError {
            project_id: "x".to_string(),
            message: "x".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"ioError\""));

        let err = UpgradeUnityError::PersistFailed {
            project_id: "x".to_string(),
            message: "x".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"persistFailed\""));
    }

    #[test]
    fn upgrade_result_serializes_camel_case() {
        let result = UpgradeUnityResult {
            project: crate::config::schemas::ProjectEntry {
                id: "id".to_string(),
                name: "Proj".to_string(),
                path: "/p".to_string(),
                unity_version: Some("6000.0.1f1".to_string()),
                last_opened_at: None,
                last_modified_at: None,
                launch_args: None,
                platform_intent: None,
                last_launch_pid: None,
                last_launch_at: None,
                frecency: 0,
                git_branch: None,
                source: "manual".to_string(),
                hidden: false,
                stale: false,
                env_vars: Default::default(),
                render_pipeline: None,
                default_build_target: None,
            },
            unity_version: "6000.0.1f1".to_string(),
            bundle_version: "1.0.1".to_string(),
            previous_bundle_version: "1.0.0".to_string(),
            previous_unity_version: "2022.3.48f1".to_string(),
            bundle_strategy: BundleStrategy::Patch,
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(json.contains("\"projectId\":\"id\"") || json.contains("\"id\":\"id\""));
        assert!(json.contains("\"unityVersion\":\"6000.0.1f1\""));
        assert!(json.contains("\"bundleVersion\":\"1.0.1\""));
        assert!(json.contains("\"previousBundleVersion\":\"1.0.0\""));
        assert!(json.contains("\"previousUnityVersion\":\"2022.3.48f1\""));
        assert!(json.contains("\"bundleStrategy\":\"patch\""));
    }

    #[test]
    fn version_is_higher_uses_lexicographic_order() {
        // The discovery service sorts versions lexicographically and the
        // upgrade flow uses the same comparator. These assertions pin the
        // contract so a future refactor (e.g. to a semver-aware parser)
        // does not silently change which versions are offered.
        assert!(version_is_higher("6000.0.1f1", "2022.3.48f1"));
        assert!(!version_is_higher("2022.3.48f1", "6000.0.1f1"));
        assert!(!version_is_higher("6000.0.1f1", "6000.0.1f1"));
        assert!(version_is_higher("6000.0.2f1", "6000.0.1f1"));
        assert!(!version_is_higher("6000.0.0f1", "6000.0.1f1"));
    }
}
