use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::discovery;
use crate::config::env_vars;
use crate::config::launch_log::{self, LaunchOutcome, LaunchRecord};
use crate::config::persistence;
use crate::config::projects::read_dir_mtime_iso;
use crate::config::running_unity::{scan_running_unity, RunningUnity};

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
    /// A Unity process is already running for this project (matched by
    /// `-projectPath` arg or by `lastLaunchPid`). Surfaced before spawn so
    /// the frontend can offer a "terminate and relaunch" path instead of
    /// forking a second Unity that will hit the `Library/EditorInstance.json`
    /// lock and either hang or show its own system-modal dialog the Hub
    /// cannot dismiss.
    #[serde(rename_all = "camelCase")]
    AlreadyRunning {
        project_id: String,
        pid: u32,
        project_path: String,
    },
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
    pub git_branch: Option<String>,
}

/// Pure helper: decide whether a fresh `scan_running_unity` snapshot
/// already contains a Unity process that would conflict with launching
/// `project_path` (whose own `last_launch_pid` is `last_pid`).
///
/// Returns `Some((pid, project_path))` when a conflict is found, `None`
/// otherwise. The two matchers:
///   1. **Path match** — any scan row whose `project_path` canonicalises
///      equal to `project_path` (caller is expected to pass an already-
///      canonicalised project path so the comparison is string-only).
///   2. **PID match** — any scan row whose `pid` equals `last_pid`,
///      regardless of whether its `-projectPath` could be parsed. Covers
///      Hub-launched Editors opened without `-projectPath` (the "Open
///      empty editor only" launch mode).
///
/// When both matchers fire, the path-match wins so the `project_path`
/// field on the returned error is the real project root (useful for the
/// frontend error message) and the PID is the path-match's PID. The
/// function never shells out — it is the unit-testable core of the
/// double-launch guard.
pub(crate) fn is_already_running(
    scan: &[RunningUnity],
    project_path: &str,
    last_pid: Option<u32>,
) -> Option<(u32, String)> {
    // Pass 1: path match — the explicit "this exact project is open" case.
    for row in scan {
        if let Some(p) = &row.project_path {
            if p == project_path {
                return Some((row.pid, p.clone()));
            }
        }
    }
    // Pass 2: PID fallback — the Hub tracked a previous launch PID and
    // that exact PID is still alive, even if its `-projectPath` was not
    // parseable (e.g. bare-editor launch, Windows quoting edge case).
    if let Some(pid) = last_pid {
        if pid == 0 {
            return None;
        }
        for row in scan {
            if row.pid == pid {
                // We don't know the project path in this branch; surface
                // the project_path the caller asked about so the error
                // message is still useful.
                return Some((row.pid, project_path.to_string()));
            }
        }
    }
    None
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

/// Write a launch record to the persistent per-launch log. Fire-and-forget
/// on a background thread so the launch command never blocks on disk I/O.
fn record_launch(record: LaunchRecord) {
    launch_log::append_record_async(record);
}

#[tauri::command]
pub fn launch_project(
    state: State<AppState>,
    project_id: String,
    // M1.5-18: the active Hub theme at the time of the launch
    // (`"dark" | "light"` — the frontend has already resolved
    // `system` to a concrete palette). Optional; the backend
    // defaults to `"system"` so legacy callers (CLI mode, the
    // upgrade flow) keep working without changes.
    theme: Option<String>,
) -> Result<LaunchResult, LaunchError> {
    let resolved_theme = theme
        .as_deref()
        .map(|t| if t == "dark" || t == "light" { t } else { "system" })
        .unwrap_or("system");
    match launch_project_inner(&state, &project_id) {
        Ok(result) => {
            let record = launch_log::build_record(
                &result.project_id,
                &result.project_name,
                &result.project_path,
                result.unity_version.as_deref(),
                Some(&result.executable_path),
                Some(result.pid),
                &result.launch_args,
                result.build_target.as_deref(),
                LaunchOutcome::Ok {
                    pid: result.pid,
                    unity_version: result.unity_version.clone(),
                    executable_path: result.executable_path.clone(),
                },
                Some(resolved_theme),
            );
            record_launch(record);
            // Strip the helpers we added to the success path; the public
            // LaunchResult shape is unchanged for frontend consumers.
            Ok(LaunchResult {
                project_id: result.project_id,
                pid: result.pid,
                unity_version: result.unity_version,
                executable_path: result.executable_path,
            })
        }
        Err(err) => {
            // Re-raise a typed LaunchError after recording context.
            let typed = err.typed;
            let record = launch_log::build_record(
                &err.project_id,
                &err.project_name,
                &err.project_path,
                err.unity_version.as_deref(),
                err.install_path.as_deref(),
                None,
                &err.launch_args,
                err.build_target.as_deref(),
                LaunchOutcome::Error {
                    code: err.code,
                    message: err.message,
                },
                Some(resolved_theme),
            );
            record_launch(record);
            Err(typed)
        }
    }
}

#[derive(Debug, Clone)]
pub struct InnerLaunchResult {
    pub project_id: String,
    pub project_name: String,
    pub project_path: String,
    pub pid: u32,
    pub unity_version: Option<String>,
    pub executable_path: String,
    pub launch_args: Vec<String>,
    pub build_target: Option<String>,
}

#[derive(Debug, Clone)]
struct InnerLaunchError {
    typed: LaunchError,
    project_id: String,
    project_name: String,
    project_path: String,
    unity_version: Option<String>,
    install_path: Option<String>,
    launch_args: Vec<String>,
    build_target: Option<String>,
    code: String,
    message: String,
}

fn launch_project_inner(
    state: &State<AppState>,
    project_id: &str,
) -> Result<InnerLaunchResult, InnerLaunchError> {
    let projects = {
        let guard = state.projects.lock().unwrap();
        guard.clone()
    };

    let project = projects
        .projects
        .iter()
        .find(|p| p.id == project_id)
        .cloned();

    let project = match project {
        Some(p) => p,
        None => {
            return Err(InnerLaunchError {
                typed: LaunchError::ProjectNotFound {
                    project_id: project_id.to_string(),
                },
                project_id: project_id.to_string(),
                project_name: String::new(),
                project_path: String::new(),
                unity_version: None,
                install_path: None,
                launch_args: vec![],
                build_target: None,
                code: "projectNotFound".to_string(),
                message: format!("project not found: {}", project_id),
            });
        }
    };

    let project_path_str = project.path.clone();
    let project_name = project.name.clone();
    let project_path = Path::new(&project_path_str);

    if !project_path.exists() {
        return Err(InnerLaunchError {
            typed: LaunchError::PathInvalid {
                project_id: project_id.to_string(),
                path: project.path.clone(),
            },
            project_id: project_id.to_string(),
            project_name,
            project_path: project_path_str,
            unity_version: None,
            install_path: None,
            launch_args: vec![],
            build_target: None,
            code: "pathInvalid".to_string(),
            message: format!("path invalid: {}", project.path),
        });
    }

    let refreshed_version = read_project_version(project_path);
    let unity_version = refreshed_version.clone().or(project.unity_version.clone());

    let version = match unity_version {
        Some(v) => v,
        None => {
            return Err(InnerLaunchError {
                typed: LaunchError::VersionMissing {
                    project_id: project_id.to_string(),
                },
                project_id: project_id.to_string(),
                project_name,
                project_path: project_path_str,
                unity_version: None,
                install_path: None,
                launch_args: vec![],
                build_target: None,
                code: "versionMissing".to_string(),
                message: "unity version missing".to_string(),
            });
        }
    };

    let settings = state.settings.lock().unwrap().clone();
    let launch_mode = settings.launch.mode.clone();

    let (executable, install_path) = match resolve_install_for_version(&state, &version) {
        Some(v) => v,
        None => {
            return Err(InnerLaunchError {
                typed: LaunchError::InstallNotFound {
                    project_id: project_id.to_string(),
                    version: version.clone(),
                },
                project_id: project_id.to_string(),
                project_name,
                project_path: project_path_str,
                unity_version: Some(version.clone()),
                install_path: None,
                launch_args: vec![],
                build_target: None,
                code: "installNotFound".to_string(),
                message: format!("unity {} is not installed", version),
            });
        }
    };

    // Double-launch guard: refuse to spawn a second Unity that would
    // collide with one already running for this project. A scan failure
    // is non-fatal — we fall through to the spawn rather than blocking
    // the user on a transient `ps` / PowerShell error. The frontend
    // stores the same scan on a 5-second poll, so the worst case after
    // a backend scan failure is the same as the worst case without this
    // check (a duplicate spawn), not worse.
    let project_path_canon = match std::fs::canonicalize(project_path) {
        Ok(p) => p.to_string_lossy().to_string(),
        Err(_) => project_path_str.clone(),
    };
    let scan = scan_running_unity();
    if let Some((conflict_pid, conflict_path)) = is_already_running(
        &scan,
        &project_path_canon,
        project.last_launch_pid,
    ) {
        return Err(InnerLaunchError {
            typed: LaunchError::AlreadyRunning {
                project_id: project_id.to_string(),
                pid: conflict_pid,
                project_path: conflict_path,
            },
            project_id: project_id.to_string(),
            project_name,
            project_path: project_path_str,
            unity_version: Some(version.clone()),
            install_path: Some(install_path),
            launch_args: vec![],
            build_target: None,
            code: "alreadyRunning".to_string(),
            message: format!(
                "Unity is already running for this project (pid {})",
                conflict_pid
            ),
        });
    }

    let mut args: Vec<String> = Vec::new();
    let mut build_target: Option<String> = None;

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
            build_target = Some(platform_intent.clone());
        }
    }

    let mut command = Command::new(&executable);
    command.args(&args);
    // M1.5-17: layer the project's per-project env vars on top of the
    // inherited parent-process env. `Command::env` replaces the value
    // for keys that already exist, so the child wins on collisions —
    // matches the documented "env vars in the child override the
    // parent where keys collide" contract.
    env_vars::apply_to_command(&mut command, &project.env_vars);

    let spawn_result = command.spawn();

    let child = match spawn_result {
        Ok(c) => c,
        Err(e) => {
            return Err(InnerLaunchError {
                typed: LaunchError::LaunchFailed {
                    project_id: project_id.to_string(),
                    message: format!("Failed to spawn Unity: {}", e),
                },
                project_id: project_id.to_string(),
                project_name,
                project_path: project_path_str,
                unity_version: Some(version),
                install_path: Some(install_path),
                launch_args: args,
                build_target,
                code: "launchFailed".to_string(),
                message: format!("Failed to spawn Unity: {}", e),
            });
        }
    };

    let pid = child.id();

    let mut projects = projects;
    if let Some(p) = projects.projects.iter_mut().find(|p| p.id == project_id) {
        p.last_launch_pid = Some(pid);
        p.last_launch_at = Some(chrono::Utc::now().to_rfc3339());
        // Increment frecency on every successful launch. The frontend
        // combines this counter with `lastLaunchAt` (14-day half-life) to
        // rank the project in the list. Saturate on overflow so a long
        // session of CLI launches cannot panic on a `u32` wrap.
        p.frecency = p.frecency.saturating_add(1);
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

    Ok(InnerLaunchResult {
        project_id: project_id.to_string(),
        project_name,
        project_path: project_path_str,
        pid,
        unity_version: Some(version),
        executable_path: executable.to_string_lossy().to_string(),
        launch_args: args,
        build_target,
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
    let git_branch = crate::config::git_branch::read_git_branch(project_path);

    let mut projects = projects;
    if let Some(p) = projects.projects.iter_mut().find(|p| p.id == project_id) {
        p.unity_version = unity_version.clone();
        p.last_modified_at = last_modified_at.clone();
        p.git_branch = git_branch.clone();
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
        git_branch,
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
            git_branch: Some("main".to_string()),
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(json.contains("\"projectId\""));
        assert!(json.contains("\"unityVersion\""));
        assert!(json.contains("\"lastModifiedAt\""));
        assert!(json.contains("\"gitBranch\""));
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

    // ---- double-launch guard --------------------------------------------

    fn scan_row(pid: u32, path: Option<&str>) -> RunningUnity {
        RunningUnity {
            pid,
            project_path: path.map(|s| s.to_string()),
        }
    }

    #[test]
    fn is_already_running_empty_scan_is_none() {
        assert!(is_already_running(&[], "/p", Some(42)).is_none());
    }

    #[test]
    fn is_already_running_unrelated_pid_is_none() {
        let scan = vec![scan_row(42, Some("/other"))];
        assert!(is_already_running(&scan, "/p", Some(99)).is_none());
    }

    #[test]
    fn is_already_running_matches_by_path() {
        let scan = vec![
            scan_row(7, Some("/p")),
            scan_row(8, Some("/other")),
        ];
        let got = is_already_running(&scan, "/p", Some(99));
        assert_eq!(got, Some((7, "/p".to_string())));
    }

    #[test]
    fn is_already_running_matches_by_pid_when_path_unparseable() {
        // Bare-editor launch: Unity is alive for our project but the
        // scanner could not read its `-projectPath` (None).
        let scan = vec![scan_row(55, None), scan_row(7, Some("/other"))];
        let got = is_already_running(&scan, "/p", Some(55));
        assert_eq!(got, Some((55, "/p".to_string())));
    }

    #[test]
    fn is_already_running_prefers_path_match_over_pid() {
        // Both a path-match (pid 7) and a PID-match (last_launch_pid = 9,
        // pid 9 is a different Unity on a different project) are present.
        // The path-match wins so the returned PID targets the conflicting
        // project, not a stale tracked PID.
        let scan = vec![
            scan_row(9, Some("/other")),
            scan_row(7, Some("/p")),
        ];
        let got = is_already_running(&scan, "/p", Some(9));
        assert_eq!(got, Some((7, "/p".to_string())));
    }

    #[test]
    fn is_already_running_ignores_zero_pid() {
        // PID 0 is a sentinel: never treated as a real match (it means
        // "process group" on Unix and is unused on Windows).
        let scan = vec![scan_row(0, None)];
        assert!(is_already_running(&scan, "/p", Some(0)).is_none());
    }

    #[test]
    fn is_already_running_no_last_pid_skips_pid_pass() {
        // Without a tracked PID, the PID pass must not run; only the
        // path pass can flag a conflict (Unity Hub-launched externally).
        let scan = vec![scan_row(99, None)];
        assert!(is_already_running(&scan, "/p", None).is_none());
    }

    #[test]
    fn launch_error_already_running_serializes() {
        let err = LaunchError::AlreadyRunning {
            project_id: "abc".to_string(),
            pid: 1234,
            project_path: "/Users/me/MyGame".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"alreadyRunning\""));
        assert!(json.contains("\"projectId\":\"abc\""));
        assert!(json.contains("\"pid\":1234"));
        assert!(json.contains("\"projectPath\":\"/Users/me/MyGame\""));
    }
}
