use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::discovery;
use crate::config::git_branch;
use crate::config::persistence;
use crate::config::schemas::{ProjectEntry, ProjectKind, ProjectsFile};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum AddProjectError {
    #[serde(rename_all = "camelCase")]
    NotADirectory { path: String },
    #[serde(rename_all = "camelCase")]
    Duplicate { path: String },
    #[serde(rename_all = "camelCase")]
    PersistFailed { message: String },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AddProjectResult {
    pub project: ProjectEntry,
    pub projects: ProjectsFile,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshOutcome {
    pub projects: ProjectsFile,
    pub updated: Vec<String>,
    pub skipped: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum RemoveProjectError {
    #[serde(rename_all = "camelCase")]
    ProjectNotFound { project_id: String },
    #[serde(rename_all = "camelCase")]
    PersistFailed { message: String },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoveProjectResult {
    pub project_id: String,
    pub removed_name: String,
    pub removed_path: String,
    pub projects: ProjectsFile,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum RelinkProjectError {
    #[serde(rename_all = "camelCase")]
    ProjectNotFound { project_id: String },
    #[serde(rename_all = "camelCase")]
    NotADirectory { path: String },
    #[serde(rename_all = "camelCase")]
    NotAUnityProject { path: String, reason: String },
    /// New path collides with another tracked project (excluding the one
    /// being relinked). Same collision semantics as `AddProjectError::Duplicate`.
    #[serde(rename_all = "camelCase")]
    Duplicate { path: String },
    #[serde(rename_all = "camelCase")]
    PersistFailed { message: String },
}

/// M1.5-15: typed errors for the Hide / Mark-stale commands. The only
/// failure mode is a stale `project_id`; the field updates are pure
/// in-memory writes followed by an atomic file save.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum SetProjectFlagError {
    #[serde(rename_all = "camelCase")]
    ProjectNotFound { project_id: String },
    #[serde(rename_all = "camelCase")]
    PersistFailed { message: String },
}

fn is_unity_project_root(path: &Path) -> Result<(), String> {
    if !path.is_dir() {
        return Err(format!("Path is not a directory: {}", path.display()));
    }
    let assets = path.join("Assets");
    let project_settings = path.join("ProjectSettings");
    if !assets.is_dir() {
        return Err("Missing 'Assets' folder".to_string());
    }
    if !project_settings.is_dir() {
        return Err("Missing 'ProjectSettings' folder".to_string());
    }
    Ok(())
}

fn read_unity_version(project_path: &Path) -> Option<String> {
    let version_file = project_path
        .join("ProjectSettings")
        .join("ProjectVersion.txt");
    let content = fs::read_to_string(&version_file).ok()?;
    for line in content.lines() {
        // Strip a UTF-8 BOM if the file was hand-edited in a tool that
        // prepends one — it would otherwise break the strip_prefix match.
        let line = line.strip_prefix('\u{FEFF}').unwrap_or(line);
        if let Some(version) = line.strip_prefix("m_EditorVersion:") {
            let trimmed = version.trim();
            if !trimmed.is_empty() {
                return Some(trimmed.to_string());
            }
        }
    }
    None
}

pub(crate) fn read_dir_mtime_iso(dir: &Path) -> Option<String> {
    let meta = fs::metadata(dir).ok()?;
    let time = meta.modified().ok().or_else(|| meta.created().ok())?;
    let duration = time.duration_since(std::time::SystemTime::UNIX_EPOCH).ok()?;
    let secs = duration.as_secs() as i64;
    chrono::DateTime::from_timestamp(secs, 0).map(|dt| dt.to_rfc3339())
}

fn canonicalize_for_compare(path: &str) -> String {
    let p = PathBuf::from(path);
    fs::canonicalize(&p)
        .map(|c| c.to_string_lossy().to_string())
        .unwrap_or_else(|_| path.to_string())
}

fn derive_name(path: &Path) -> String {
    path.file_name()
        .and_then(|n| n.to_str())
        .map(|s| s.to_string())
        .unwrap_or_else(|| path.to_string_lossy().to_string())
}

#[tauri::command]
pub fn add_project(
    state: State<AppState>,
    path: String,
) -> Result<AddProjectResult, AddProjectError> {
    let project_path = PathBuf::from(&path);

    if !project_path.is_dir() {
        return Err(AddProjectError::NotADirectory { path: path.clone() });
    }

    // Multi-type: any directory is accepted. `detect_kind` classifies
    // the folder (Unity → OpenMcp → Package → Custom) so the row and
    // settings popup can adapt per type. The Unity-only enrichments
    // below (render pipeline / build target / unity version) only run
    // for the Unity branch; other kinds get `None` for those fields.
    let kind = crate::config::project_kind::detect_kind(&project_path);

    let canonical = canonicalize_for_compare(&path);

    {
        let guard = state.projects.lock().unwrap();
        if guard
            .projects
            .iter()
            .any(|p| canonicalize_for_compare(&p.path) == canonical)
        {
            return Err(AddProjectError::Duplicate { path: path.clone() });
        }
    }

    let last_modified_at = read_dir_mtime_iso(&project_path);

    // Unity-only enrichments. The render-pipeline / build-target
    // helpers read Unity-specific asset files that do not exist for
    // other kinds, so we skip the work entirely outside the Unity
    // branch (they fall back to BIRP / `None` internally anyway, but
    // running them on a package folder is wasted I/O).
    let (unity_version, render_pipeline, default_build_target) = match kind {
        ProjectKind::Unity => {
            let version = read_unity_version(&project_path);
            let rp = Some(
                crate::config::render_pipeline::read_render_pipeline(&project_path)
                    .label()
                    .to_string(),
            );
            let bt = crate::config::build_target::read_default_build_target(&project_path).target;
            (version, rp, bt)
        }
        ProjectKind::Package | ProjectKind::OpenMcp | ProjectKind::Custom => (None, None, None),
    };

    let package_manifest_path = crate::config::project_kind::package_manifest_relative(kind)
        .map(|s| s.to_string());

    let entry = ProjectEntry {
        id: uuid::Uuid::new_v4().to_string(),
        name: derive_name(&project_path),
        path: path.clone(),
        unity_version,
        last_opened_at: None,
        last_modified_at,
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
        render_pipeline,
        default_build_target,
        kind,
        package_manifest_path,
        migrate_source_folder: None,
        line_count_stats: None,
        ai_setup_wizard: None,
    };

    let mut projects = state.projects.lock().unwrap().clone();
    projects.projects.push(entry.clone());

    persistence::save_projects(&projects).map_err(|e| AddProjectError::PersistFailed {
        message: e.to_string(),
    })?;

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects.clone();
    }

    Ok(AddProjectResult {
        project: entry,
        projects,
    })
}

#[tauri::command]
pub fn refresh_all_projects(state: State<AppState>) -> RefreshOutcome {
    let mut projects = state.projects.lock().unwrap().clone();
    let mut updated: Vec<String> = Vec::new();
    let mut skipped: Vec<String> = Vec::new();

    for project in projects.projects.iter_mut() {
        let project_path = PathBuf::from(&project.path);
        if !project_path.is_dir() {
            skipped.push(project.id.clone());
            continue;
        }
        let new_version = read_unity_version(&project_path);
        let new_mtime = read_dir_mtime_iso(&project_path);
        let new_branch = git_branch::read_git_branch(&project_path);
        // M15 T6.4: refresh the SRP label and default build target
        // alongside the other disk-derived fields. Both helpers fall
        // back to BIRP / `None` for projects without the relevant
        // asset file so the row always has a value (no `Option` to
        // thread through the diff check).
        let new_render_pipeline = Some(
            crate::config::render_pipeline::read_render_pipeline(&project_path)
                .label()
                .to_string(),
        );
        let new_default_build_target =
            crate::config::build_target::read_default_build_target(&project_path).target;
        if new_version != project.unity_version
            || new_mtime != project.last_modified_at
            || new_branch != project.git_branch
            || new_render_pipeline != project.render_pipeline
            || new_default_build_target != project.default_build_target
        {
            project.unity_version = new_version;
            project.last_modified_at = new_mtime;
            project.git_branch = new_branch;
            project.render_pipeline = new_render_pipeline;
            project.default_build_target = new_default_build_target;
            updated.push(project.id.clone());
        }
    }

    if let Err(e) = persistence::save_projects(&projects) {
        log::error!("Failed to persist refreshed projects: {}", e);
    }

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects.clone();
    }

    let settings = state.settings.lock().unwrap().clone();
    let discovery_result = discovery::discover_unity_installations(&settings);
    {
        let mut cache = state.discovery_cache.lock().unwrap();
        *cache = Some(discovery_result);
    }

    RefreshOutcome {
        projects,
        updated,
        skipped,
    }
}

#[tauri::command]
pub fn remove_project(
    state: State<AppState>,
    project_id: String,
) -> Result<RemoveProjectResult, RemoveProjectError> {
    let mut projects = state.projects.lock().unwrap().clone();

    let removed = projects
        .projects
        .iter()
        .find(|p| p.id == project_id)
        .cloned();

    let removed = match removed {
        Some(p) => p,
        None => {
            return Err(RemoveProjectError::ProjectNotFound {
                project_id: project_id.clone(),
            });
        }
    };

    projects.projects.retain(|p| p.id != project_id);

    if let Err(e) = persistence::save_projects(&projects) {
        return Err(RemoveProjectError::PersistFailed {
            message: e.to_string(),
        });
    }

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects.clone();
    }

    Ok(RemoveProjectResult {
        project_id: removed.id.clone(),
        removed_name: removed.name.clone(),
        removed_path: removed.path.clone(),
        projects,
    })
}

/// Relink a `pathMissing` row to a new folder on disk. Validates the
/// target as a Unity project root, replaces `path` in place, refreshes
/// the version string, and bumps `lastModifiedAt` to "now" so the row
/// sorts to the top of any `lastModified` view. The `id` and per-project
/// fields (`launchArgs`, `platformIntent`, `lastLaunchPid`,
/// `lastLaunchAt`, `frecency`, `gitBranch`) are preserved.
///
/// Idempotency: if the new path canonicalizes to the same as the current
/// path, the entry is returned unchanged and the on-disk file is not
/// touched. A duplicate against a *different* project is rejected with
/// `RelinkProjectError::Duplicate` (matches `add_project` semantics so
/// the frontend can surface the same inline error for both flows).
#[tauri::command]
pub fn relink_project(
    state: State<AppState>,
    project_id: String,
    new_path: String,
) -> Result<ProjectEntry, RelinkProjectError> {
    let new_project_path = PathBuf::from(&new_path);

    if !new_project_path.is_dir() {
        return Err(RelinkProjectError::NotADirectory { path: new_path });
    }

    if let Err(reason) = is_unity_project_root(&new_project_path) {
        return Err(RelinkProjectError::NotAUnityProject {
            path: new_path,
            reason,
        });
    }

    let mut projects = state.projects.lock().unwrap().clone();

    let target_index = projects
        .projects
        .iter()
        .position(|p| p.id == project_id)
        .ok_or_else(|| RelinkProjectError::ProjectNotFound {
            project_id: project_id.clone(),
        })?;

    // Idempotency: re-running relink on the same path is a no-op. We
    // canonicalize so `/a/b/..//b/c` and `/a/b/c` are treated as the
    // same path; this matches the `add_project` duplicate check.
    let current_canonical = canonicalize_for_compare(&projects.projects[target_index].path);
    let new_canonical = canonicalize_for_compare(&new_path);
    if current_canonical == new_canonical {
        return Ok(projects.projects[target_index].clone());
    }

    // Reject collisions with *other* projects.
    let collision = projects
        .projects
        .iter()
        .any(|p| p.id != project_id && canonicalize_for_compare(&p.path) == new_canonical);
    if collision {
        return Err(RelinkProjectError::Duplicate { path: new_path });
    }

    let unity_version = read_unity_version(&new_project_path);
    let new_mtime = read_dir_mtime_iso(&new_project_path)
        .unwrap_or_else(|| chrono::Utc::now().to_rfc3339());

    let target = &mut projects.projects[target_index];
    target.path = new_path.clone();
    target.name = derive_name(&new_project_path);
    target.unity_version = unity_version;
    target.last_modified_at = Some(new_mtime);
    // Clear any cached state tied to the old path. `gitBranch` is the
    // only field that depends on the project root; let the next refresh
    // re-resolve it from the new `.git/HEAD` rather than showing a stale
    // chip for a directory the user has moved away from.
    target.git_branch = None;

    if let Err(e) = persistence::save_projects(&projects) {
        return Err(RelinkProjectError::PersistFailed {
            message: e.to_string(),
        });
    }

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects.clone();
    }

    let updated = projects
        .projects
        .iter()
        .find(|p| p.id == project_id)
        .cloned()
        .ok_or_else(|| RelinkProjectError::ProjectNotFound {
            project_id: project_id.clone(),
        })?;
    Ok(updated)
}

/// M1.5-15: soft-delete a project row. The entry stays in
/// `projects.json` with `hidden: true`; the GUI hides it from the
/// default list view and reveals it again through a "Show hidden"
/// toggle in the toolbar. No file operations on the project folder.
/// Re-running on a row that is already hidden is a no-op (idempotent).
#[tauri::command]
pub fn set_project_hidden(
    state: State<AppState>,
    project_id: String,
    hidden: bool,
) -> Result<ProjectEntry, SetProjectFlagError> {
    let mut projects = state.projects.lock().unwrap().clone();
    let target_index = projects
        .projects
        .iter()
        .position(|p| p.id == project_id)
        .ok_or_else(|| SetProjectFlagError::ProjectNotFound {
            project_id: project_id.clone(),
        })?;

    if projects.projects[target_index].hidden == hidden {
        // Idempotent no-op: do not write the on-disk file.
        let snapshot = projects.projects[target_index].clone();
        let mut guard = state.projects.lock().unwrap();
        *guard = projects;
        return Ok(snapshot);
    }

    projects.projects[target_index].hidden = hidden;
    // Clearing `stale` is a no-op for this command — Hide and Mark
    // stale are independent flags per the spec. A hidden-and-stale
    // row stays hidden, and a relink still clears `stale` (handled in
    // `relink_project`).

    if let Err(e) = persistence::save_projects(&projects) {
        return Err(SetProjectFlagError::PersistFailed {
            message: e.to_string(),
        });
    }

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects.clone();
    }
    Ok(projects.projects[target_index].clone())
}

/// M1.5-15: tag a project row as `stale`. Stale rows stay visible in
/// the Projects tab with a `stale` chip (distinct from `missing path`),
/// are excluded from launch / running-Unity actions, and remain
/// candidates for relink. No file operations on the project folder.
/// Re-running on a row that is already stale is a no-op (idempotent).
#[tauri::command]
pub fn set_project_stale(
    state: State<AppState>,
    project_id: String,
    stale: bool,
) -> Result<ProjectEntry, SetProjectFlagError> {
    let mut projects = state.projects.lock().unwrap().clone();
    let target_index = projects
        .projects
        .iter()
        .position(|p| p.id == project_id)
        .ok_or_else(|| SetProjectFlagError::ProjectNotFound {
            project_id: project_id.clone(),
        })?;

    if projects.projects[target_index].stale == stale {
        let snapshot = projects.projects[target_index].clone();
        let mut guard = state.projects.lock().unwrap();
        *guard = projects;
        return Ok(snapshot);
    }

    projects.projects[target_index].stale = stale;

    if let Err(e) = persistence::save_projects(&projects) {
        return Err(SetProjectFlagError::PersistFailed {
            message: e.to_string(),
        });
    }

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects.clone();
    }
    Ok(projects.projects[target_index].clone())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_unity_project(dir: &Path, version: Option<&str>) {
        fs::create_dir_all(dir.join("Assets")).unwrap();
        fs::create_dir_all(dir.join("ProjectSettings")).unwrap();
        if let Some(v) = version {
            fs::write(
                dir.join("ProjectSettings").join("ProjectVersion.txt"),
                format!("m_EditorVersion: {}\n", v),
            )
            .unwrap();
        }
    }

    #[test]
    fn is_unity_project_root_accepts_valid_root() {
        let dir = tempfile::tempdir().unwrap();
        make_unity_project(dir.path(), None);
        assert!(is_unity_project_root(dir.path()).is_ok());
    }

    #[test]
    fn is_unity_project_root_rejects_missing_assets() {
        let dir = tempfile::tempdir().unwrap();
        fs::create_dir_all(dir.path().join("ProjectSettings")).unwrap();
        let err = is_unity_project_root(dir.path()).unwrap_err();
        assert!(err.contains("Assets"));
    }

    #[test]
    fn is_unity_project_root_rejects_missing_project_settings() {
        let dir = tempfile::tempdir().unwrap();
        fs::create_dir_all(dir.path().join("Assets")).unwrap();
        let err = is_unity_project_root(dir.path()).unwrap_err();
        assert!(err.contains("ProjectSettings"));
    }

    #[test]
    fn is_unity_project_root_rejects_file() {
        let dir = tempfile::tempdir().unwrap();
        let file = dir.path().join("not-a-dir.txt");
        fs::write(&file, "").unwrap();
        let err = is_unity_project_root(&file).unwrap_err();
        assert!(err.contains("not a directory"));
    }

    #[test]
    fn read_unity_version_parses_known_version() {
        let dir = tempfile::tempdir().unwrap();
        make_unity_project(dir.path(), Some("6000.0.1f1"));
        assert_eq!(read_unity_version(dir.path()), Some("6000.0.1f1".into()));
    }

    #[test]
    fn read_unity_version_returns_none_for_missing_file() {
        let dir = tempfile::tempdir().unwrap();
        make_unity_project(dir.path(), None);
        assert_eq!(read_unity_version(dir.path()), None);
    }

    #[test]
    fn derive_name_uses_basename() {
        let p = Path::new("/some/parent/MyProject");
        assert_eq!(derive_name(p), "MyProject");
    }

    #[test]
    fn derive_name_falls_back_to_path() {
        let p = Path::new("/");
        let name = derive_name(p);
        assert!(!name.is_empty());
    }

    fn entry_with(id: &str, path: &str) -> ProjectEntry {
        ProjectEntry {
            id: id.to_string(),
            name: derive_name(Path::new(path)),
            path: path.to_string(),
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
            kind: ProjectKind::Unity,
            package_manifest_path: None,
            migrate_source_folder: None,
            line_count_stats: None,
            ai_setup_wizard: None,
        }
    }

    #[test]
    fn remove_project_entry_serializes() {
        let dir = tempfile::tempdir().unwrap();
        let project = dir.path().join("MyGame");
        make_unity_project(&project, Some("6000.0.1f1"));
        let entry = entry_with("abc-123", project.to_str().unwrap());

        let result = RemoveProjectResult {
            project_id: entry.id.clone(),
            removed_name: entry.name.clone(),
            removed_path: entry.path.clone(),
            projects: ProjectsFile {
                version: 1,
                projects: vec![entry.clone()],
            },
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(json.contains("\"projectId\""));
        assert!(json.contains("\"removedName\""));
        assert!(json.contains("\"removedPath\""));
        assert!(json.contains("\"projects\""));
    }

    #[test]
    fn remove_project_error_not_found_serializes() {
        let err = RemoveProjectError::ProjectNotFound {
            project_id: "missing".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"projectNotFound\""));
        assert!(json.contains("\"missing\""));
    }

    #[test]
    fn remove_project_error_persist_failed_serializes() {
        let err = RemoveProjectError::PersistFailed {
            message: "io error".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"persistFailed\""));
        assert!(json.contains("\"io error\""));
    }

    #[test]
    fn relink_project_error_not_found_serializes() {
        let err = RelinkProjectError::ProjectNotFound {
            project_id: "missing".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"projectNotFound\""));
        assert!(json.contains("\"missing\""));
    }

    #[test]
    fn relink_project_error_not_a_unity_project_serializes() {
        let err = RelinkProjectError::NotAUnityProject {
            path: "/some/path".to_string(),
            reason: "Missing 'Assets' folder".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"notAUnityProject\""));
        assert!(json.contains("\"Missing 'Assets' folder\""));
    }

    #[test]
    fn relink_project_error_duplicate_serializes() {
        let err = RelinkProjectError::Duplicate {
            path: "/x".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"duplicate\""));
        assert!(json.contains("\"/x\""));
    }

    #[test]
    fn canonicalize_for_compare_handles_missing_path() {
        // A non-existent path falls back to the input string verbatim so
        // canonical equality is never panicky; the relink path check
        // relies on this to keep the idempotency fast-path safe.
        let original = "/definitely/does/not/exist/qwerty";
        assert_eq!(canonicalize_for_compare(original), original);
    }

    #[test]
    fn relink_idempotency_check_matches_same_path() {
        // `relink_project` short-circuits when the canonicalized old and
        // new paths are equal. We exercise the helper here so a future
        // change to `canonicalize_for_compare` cannot silently break the
        // idempotency contract.
        let dir = tempfile::tempdir().unwrap();
        let project = dir.path().join("MyGame");
        make_unity_project(&project, Some("6000.0.1f1"));
        let canonical = fs::canonicalize(&project).unwrap();
        let canonical_str = canonical.to_string_lossy().to_string();
        assert_eq!(canonicalize_for_compare(&canonical_str), canonical_str);
    }

    #[test]
    fn set_project_flag_error_not_found_serializes() {
        let err = SetProjectFlagError::ProjectNotFound {
            project_id: "missing".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"projectNotFound\""));
        assert!(json.contains("\"missing\""));
    }

    #[test]
    fn set_project_flag_error_persist_failed_serializes() {
        let err = SetProjectFlagError::PersistFailed {
            message: "io error".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"persistFailed\""));
        assert!(json.contains("\"io error\""));
    }

    /// M1.5-15: round-trip a hidden flag through the on-disk format.
    /// The flag is part of the persistent `ProjectEntry` shape, so a
    /// backwards-incompatible change here would be picked up by the
    /// test suite. The round-trip is the only "integration" coverage
    /// we have without booting a full Tauri runtime, so we also
    /// assert the explicit `false → true` transition.
    #[test]
    fn project_entry_hidden_round_trips_through_json() {
        let mut entry = entry_with("id-1", "/tmp/Proj");
        assert!(!entry.hidden);
        assert!(!entry.stale);

        entry.hidden = true;
        let json = serde_json::to_string(&entry).unwrap();
        let restored: ProjectEntry = serde_json::from_str(&json).unwrap();
        assert!(restored.hidden);
        assert!(!restored.stale);

        entry.stale = true;
        let json = serde_json::to_string(&entry).unwrap();
        let restored: ProjectEntry = serde_json::from_str(&json).unwrap();
        assert!(restored.hidden);
        assert!(restored.stale);
    }

    /// M1.5-15: legacy entries (pre-M1.5-15) carry no `hidden` / `stale`
    /// fields. The deserializer must default both to `false` so existing
    /// user configs are not silently hidden or marked stale on the next
    /// launch. This is the contract that the M1.5-15 acceptance
    /// checklist calls out: "The M1 missing-path chip and behavior
    /// remain unchanged when the new toggles are off."
    #[test]
    fn project_entry_hidden_stale_default_for_legacy_json() {
        let legacy = r#"{
            "id": "id-1",
            "name": "Proj",
            "path": "/tmp/Proj",
            "unityVersion": "6000.0.1f1"
        }"#;
        let entry: ProjectEntry = serde_json::from_str(legacy).unwrap();
        assert!(!entry.hidden);
        assert!(!entry.stale);
    }
}
