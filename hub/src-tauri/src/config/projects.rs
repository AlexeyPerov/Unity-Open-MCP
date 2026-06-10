use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::discovery;
use crate::config::persistence;
use crate::config::schemas::{ProjectEntry, ProjectsFile};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum AddProjectError {
    #[serde(rename_all = "camelCase")]
    NotADirectory { path: String },
    #[serde(rename_all = "camelCase")]
    NotAUnityProject { path: String, reason: String },
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
        if let Some(version) = line.strip_prefix("m_EditorVersion:") {
            let trimmed = version.trim();
            if !trimmed.is_empty() {
                return Some(trimmed.to_string());
            }
        }
    }
    None
}

fn read_dir_mtime_iso(dir: &Path) -> Option<String> {
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

    if let Err(reason) = is_unity_project_root(&project_path) {
        return Err(AddProjectError::NotAUnityProject {
            path: path.clone(),
            reason,
        });
    }

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

    let unity_version = read_unity_version(&project_path);
    let last_modified_at = read_dir_mtime_iso(&project_path);

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
        if new_version != project.unity_version || new_mtime != project.last_modified_at {
            project.unity_version = new_version;
            project.last_modified_at = new_mtime;
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
}
