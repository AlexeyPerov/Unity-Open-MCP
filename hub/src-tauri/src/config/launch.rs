use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::discovery;
use crate::config::persistence;
use crate::config::projects::read_dir_mtime_iso;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum LaunchError {
    #[serde(rename_all = "camelCase")]
    ProjectNotFound { project_id: String },
    #[serde(rename_all = "camelCase")]
    PathInvalid { project_id: String, path: String },
    #[serde(rename_all = "camelCase")]
    VersionMissing { project_id: String },
    #[serde(rename_all = "camelCase")]
    InstallNotFound { project_id: String, version: String },
    #[serde(rename_all = "camelCase")]
    LaunchFailed { project_id: String, message: String },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchResult {
    pub project_id: String,
    pub pid: u32,
    pub unity_version: Option<String>,
    pub executable_path: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VersionRefreshResult {
    pub project_id: String,
    pub unity_version: Option<String>,
    pub last_modified_at: Option<String>,
}

pub(crate) fn get_unity_executable_path(install_dir: &Path) -> Option<PathBuf> {
    if cfg!(target_os = "macos") {
        let exe = install_dir
            .join("Unity.app")
            .join("Contents")
            .join("MacOS")
            .join("Unity");
        if exe.exists() {
            Some(exe)
        } else {
            None
        }
    } else if cfg!(target_os = "windows") {
        let exe = install_dir.join("Editor").join("Unity.exe");
        if exe.exists() {
            Some(exe)
        } else {
            None
        }
    } else {
        let exe = install_dir.join("Editor").join("Unity");
        if exe.exists() {
            Some(exe)
        } else {
            None
        }
    }
}

pub fn read_project_version(project_path: &Path) -> Option<String> {
    let version_file = project_path
        .join("ProjectSettings")
        .join("ProjectVersion.txt");
    if !version_file.exists() {
        return None;
    }
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

pub(crate) fn resolve_install_for_version(
    state: &State<AppState>,
    version: &str,
) -> Option<(PathBuf, String)> {
    let settings = state.settings.lock().unwrap().clone();

    let cached = {
        let cache = state.discovery_cache.lock().unwrap();
        cache.clone()
    };

    let discovery_result = match cached {
        Some(result) => result,
        None => {
            let result = discovery::discover_unity_installations(&settings);
            let mut cache = state.discovery_cache.lock().unwrap();
            *cache = Some(result.clone());
            result
        }
    };

    let install = discovery_result
        .installations
        .iter()
        .find(|i| i.version == version)?;

    let install_path = Path::new(&install.path);
    let exe = get_unity_executable_path(install_path)?;
    Some((exe, install.path.clone()))
}

#[tauri::command]
pub fn launch_project(
    state: State<AppState>,
    project_id: String,
) -> Result<LaunchResult, LaunchError> {
    let projects = {
        let guard = state.projects.lock().unwrap();
        guard.clone()
    };

    let project = projects
        .projects
        .iter()
        .find(|p| p.id == project_id)
        .ok_or_else(|| LaunchError::ProjectNotFound {
            project_id: project_id.clone(),
        })?
        .clone();

    let project_path = Path::new(&project.path);
    if !project_path.exists() {
        return Err(LaunchError::PathInvalid {
            project_id: project_id.clone(),
            path: project.path.clone(),
        });
    }

    let refreshed_version = read_project_version(project_path);
    let unity_version = refreshed_version
        .clone()
        .or(project.unity_version.clone());

    let version = unity_version
        .as_ref()
        .ok_or_else(|| LaunchError::VersionMissing {
            project_id: project_id.clone(),
        })?;

    let settings = state.settings.lock().unwrap().clone();
    let launch_mode = settings.launch.mode.clone();

    let (executable, _) = resolve_install_for_version(&state, version).ok_or_else(|| {
        LaunchError::InstallNotFound {
            project_id: project_id.clone(),
            version: version.clone(),
        }
    })?;

    let mut args: Vec<String> = Vec::new();

    if launch_mode == "openProject" {
        args.push("-projectPath".to_string());
        args.push(project.path.clone());
    }

    if let Some(ref launch_args) = project.launch_args {
        if !launch_args.is_empty() {
            for arg in launch_args.split_whitespace() {
                args.push(arg.to_string());
            }
        }
    }

    if let Some(ref platform_intent) = project.platform_intent {
        if !platform_intent.is_empty() {
            args.push("-buildTarget".to_string());
            args.push(platform_intent.clone());
        }
    }

    let child = Command::new(&executable)
        .args(&args)
        .spawn()
        .map_err(|e| LaunchError::LaunchFailed {
            project_id: project_id.clone(),
            message: format!("Failed to spawn Unity: {}", e),
        })?;

    let pid = child.id();

    let mut projects = projects;
    if let Some(p) = projects.projects.iter_mut().find(|p| p.id == project_id) {
        p.last_launch_pid = Some(pid);
        p.last_launch_at = Some(chrono::Utc::now().to_rfc3339());
        if refreshed_version.is_some() {
            p.unity_version = refreshed_version.clone();
        }
    }

    if let Err(e) = persistence::save_projects(&projects) {
        log::error!("Failed to persist launch data: {}", e);
    }

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects;
    }

    Ok(LaunchResult {
        project_id: project_id.clone(),
        pid,
        unity_version,
        executable_path: executable.to_string_lossy().to_string(),
    })
}

#[tauri::command]
pub fn refresh_project_version(
    state: State<AppState>,
    project_id: String,
) -> Result<VersionRefreshResult, LaunchError> {
    let projects = {
        let guard = state.projects.lock().unwrap();
        guard.clone()
    };

    let project = projects
        .projects
        .iter()
        .find(|p| p.id == project_id)
        .ok_or_else(|| LaunchError::ProjectNotFound {
            project_id: project_id.clone(),
        })?
        .clone();

    let project_path = Path::new(&project.path);
    if !project_path.exists() {
        return Err(LaunchError::PathInvalid {
            project_id: project_id.clone(),
            path: project.path.clone(),
        });
    }

    let unity_version = read_project_version(project_path);
    let last_modified_at = read_dir_mtime_iso(project_path);

    let mut projects = projects;
    if let Some(p) = projects.projects.iter_mut().find(|p| p.id == project_id) {
        p.unity_version = unity_version.clone();
        p.last_modified_at = last_modified_at.clone();
    }

    if let Err(e) = persistence::save_projects(&projects) {
        log::error!("Failed to persist version refresh: {}", e);
    }

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects;
    }

    Ok(VersionRefreshResult {
        project_id,
        unity_version,
        last_modified_at,
    })
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum RunUnityError {
    #[serde(rename_all = "camelCase")]
    VersionMissing,
    #[serde(rename_all = "camelCase")]
    InstallNotFound { version: String },
    #[serde(rename_all = "camelCase")]
    ExecutableMissing { version: String, install_path: String },
    #[serde(rename_all = "camelCase")]
    LaunchFailed { version: String, message: String },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RunUnityResult {
    pub version: String,
    pub pid: u32,
    pub executable_path: String,
}

#[tauri::command]
pub fn run_unity_install(
    state: State<AppState>,
    version: String,
) -> Result<RunUnityResult, RunUnityError> {
    if version.trim().is_empty() {
        return Err(RunUnityError::VersionMissing);
    }

    let (executable, _install_path) =
        resolve_install_for_version(&state, &version).ok_or_else(|| {
            RunUnityError::InstallNotFound {
                version: version.clone(),
            }
        })?;

    let child = Command::new(&executable)
        .spawn()
        .map_err(|e| RunUnityError::LaunchFailed {
            version: version.clone(),
            message: format!("Failed to spawn Unity: {}", e),
        })?;

    let pid = child.id();

    Ok(RunUnityResult {
        version: version.clone(),
        pid,
        executable_path: executable.to_string_lossy().to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn read_project_version_parses_version_line() {
        let dir = tempfile::tempdir().unwrap();
        let ps_dir = dir.path().join("ProjectSettings");
        fs::create_dir_all(&ps_dir).unwrap();
        fs::write(
            ps_dir.join("ProjectVersion.txt"),
            "m_EditorVersion: 6000.0.1f1\nm_Revision: abc123\n",
        )
        .unwrap();

        let version = read_project_version(dir.path());
        assert_eq!(version, Some("6000.0.1f1".to_string()));
    }

    #[test]
    fn read_project_version_returns_none_for_missing_file() {
        let dir = tempfile::tempdir().unwrap();
        assert!(read_project_version(dir.path()).is_none());
    }

    #[test]
    fn read_project_version_returns_none_for_empty_version() {
        let dir = tempfile::tempdir().unwrap();
        let ps_dir = dir.path().join("ProjectSettings");
        fs::create_dir_all(&ps_dir).unwrap();
        fs::write(
            ps_dir.join("ProjectVersion.txt"),
            "m_EditorVersion: \n",
        )
        .unwrap();

        assert!(read_project_version(dir.path()).is_none());
    }

    #[test]
    fn read_project_version_handles_no_newline() {
        let dir = tempfile::tempdir().unwrap();
        let ps_dir = dir.path().join("ProjectSettings");
        fs::create_dir_all(&ps_dir).unwrap();
        fs::write(
            ps_dir.join("ProjectVersion.txt"),
            "m_EditorVersion: 2022.3.48f1",
        )
        .unwrap();

        let version = read_project_version(dir.path());
        assert_eq!(version, Some("2022.3.48f1".to_string()));
    }

    #[test]
    fn read_project_version_skips_non_version_lines() {
        let dir = tempfile::tempdir().unwrap();
        let ps_dir = dir.path().join("ProjectSettings");
        fs::create_dir_all(&ps_dir).unwrap();
        fs::write(
            ps_dir.join("ProjectVersion.txt"),
            "something else\nm_EditorVersion: 6000.0.2f1\nmore text\n",
        )
        .unwrap();

        let version = read_project_version(dir.path());
        assert_eq!(version, Some("6000.0.2f1".to_string()));
    }

    #[test]
    fn launch_error_project_not_found_serializes() {
        let err = LaunchError::ProjectNotFound {
            project_id: "abc".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"projectNotFound\""));
        assert!(json.contains("\"projectId\""));
    }

    #[test]
    fn launch_error_path_invalid_serializes() {
        let err = LaunchError::PathInvalid {
            project_id: "abc".to_string(),
            path: "/missing".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"pathInvalid\""));
    }

    #[test]
    fn launch_error_version_missing_serializes() {
        let err = LaunchError::VersionMissing {
            project_id: "abc".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"versionMissing\""));
    }

    #[test]
    fn launch_error_install_not_found_serializes() {
        let err = LaunchError::InstallNotFound {
            project_id: "abc".to_string(),
            version: "6000.0.1f1".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"installNotFound\""));
    }

    #[test]
    fn launch_error_launch_failed_serializes() {
        let err = LaunchError::LaunchFailed {
            project_id: "abc".to_string(),
            message: "permission denied".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"launchFailed\""));
    }

    #[test]
    fn launch_result_serializes_camel_case() {
        let result = LaunchResult {
            project_id: "abc".to_string(),
            pid: 12345,
            unity_version: Some("6000.0.1f1".to_string()),
            executable_path: "/path/to/Unity".to_string(),
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(json.contains("\"projectId\""));
        assert!(json.contains("\"unityVersion\""));
        assert!(json.contains("\"executablePath\""));
    }

    #[test]
    fn version_refresh_result_serializes() {
        let result = VersionRefreshResult {
            project_id: "abc".to_string(),
            unity_version: Some("2022.3.48f1".to_string()),
            last_modified_at: Some("2026-06-10T12:00:00+00:00".to_string()),
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(json.contains("\"projectId\""));
        assert!(json.contains("\"unityVersion\""));
        assert!(json.contains("\"lastModifiedAt\""));
    }

    #[test]
    fn refresh_readers_return_version_and_fresh_mtime() {
        // Covers the two helpers that `refresh_project_version` composes
        // without needing a Tauri State (which the unit test cannot easily
        // construct). The command itself wires these reads into the same
        // `Ok(VersionRefreshResult { ... })` payload.
        let dir = tempfile::tempdir().unwrap();
        let ps_dir = dir.path().join("ProjectSettings");
        fs::create_dir_all(&ps_dir).unwrap();
        fs::write(
            ps_dir.join("ProjectVersion.txt"),
            "m_EditorVersion: 6000.0.1f1\n",
        )
        .unwrap();

        let version = read_project_version(dir.path());
        assert_eq!(version.as_deref(), Some("6000.0.1f1"));

        let mtime = read_dir_mtime_iso(dir.path());
        assert!(mtime.is_some(), "mtime should resolve for a real dir");
        // A 1970 sentinel must differ from the real mtime — guards against
        // the readers silently returning the stored value instead of
        // re-reading from disk.
        assert_ne!(
            mtime.as_deref(),
            Some("1970-01-01T00:00:00+00:00")
        );
    }

    #[test]
    fn run_unity_error_version_missing_serializes() {
        let err = RunUnityError::VersionMissing;
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"versionMissing\""));
    }

    #[test]
    fn run_unity_error_install_not_found_serializes() {
        let err = RunUnityError::InstallNotFound {
            version: "6000.0.1f1".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"installNotFound\""));
        assert!(json.contains("\"version\""));
    }

    #[test]
    fn run_unity_error_executable_missing_serializes() {
        let err = RunUnityError::ExecutableMissing {
            version: "6000.0.1f1".to_string(),
            install_path: "/Applications/Unity".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"executableMissing\""));
        assert!(json.contains("\"installPath\""));
    }

    #[test]
    fn run_unity_error_launch_failed_serializes() {
        let err = RunUnityError::LaunchFailed {
            version: "6000.0.1f1".to_string(),
            message: "permission denied".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"launchFailed\""));
        assert!(json.contains("\"message\""));
    }

    #[test]
    fn run_unity_result_serializes_camel_case() {
        let result = RunUnityResult {
            version: "6000.0.1f1".to_string(),
            pid: 9999,
            executable_path: "/path/to/Unity".to_string(),
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(json.contains("\"version\""));
        assert!(json.contains("\"pid\""));
        assert!(json.contains("\"executablePath\""));
    }

    #[test]
    fn get_unity_executable_path_finds_macos_bundle() {
        if !cfg!(target_os = "macos") {
            return;
        }
        let dir = tempfile::tempdir().unwrap();
        let macos = dir.path().join("Unity.app").join("Contents").join("MacOS");
        fs::create_dir_all(&macos).unwrap();
        fs::write(macos.join("Unity"), "").unwrap();

        let found = get_unity_executable_path(dir.path());
        assert_eq!(
            found.as_deref().map(|p| p.to_string_lossy().to_string()),
            Some(macos.join("Unity").to_string_lossy().to_string())
        );
    }

    #[test]
    fn get_unity_executable_path_returns_none_when_missing() {
        let dir = tempfile::tempdir().unwrap();
        assert!(get_unity_executable_path(dir.path()).is_none());
    }
}
