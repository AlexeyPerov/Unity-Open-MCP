use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct LogPaths {
    pub editor_logs_folder: Option<String>,
    pub editor_log_file: Option<String>,
    pub player_logs_folder: Option<String>,
    pub crash_logs_folder: Option<String>,
}

fn path_to_string(path: &Path) -> String {
    path.to_string_lossy().to_string()
}

fn editor_logs_dir_macos() -> PathBuf {
    dirs::home_dir()
        .map(|h| h.join("Library").join("Logs").join("Unity"))
        .unwrap_or_else(|| PathBuf::from("~/Library/Logs/Unity"))
}

fn editor_logs_dir_windows() -> PathBuf {
    if let Some(local) = dirs::config_local_dir() {
        return local.join("Unity").join("Editor");
    }
    if let Some(appdata) = std::env::var_os("LOCALAPPDATA") {
        return PathBuf::from(appdata).join("Unity").join("Editor");
    }
    PathBuf::from("C:\\Users\\Public\\AppData\\Local\\Unity\\Editor")
}

fn editor_logs_dir_linux() -> PathBuf {
    // Unity Editor on Linux writes logs under ~/.config/unity3d/Editor.log by
    // default, but the *folder* typically shown in third-party launchers is
    // ~/.config/unity3d. Fall back to that if XDG_CONFIG_HOME is missing.
    if let Some(xdg) = std::env::var_os("XDG_CONFIG_HOME") {
        return PathBuf::from(xdg).join("unity3d");
    }
    dirs::home_dir()
        .map(|h| h.join(".config").join("unity3d"))
        .unwrap_or_else(|| PathBuf::from("~/.config/unity3d"))
}

fn editor_logs_dir() -> PathBuf {
    if cfg!(target_os = "macos") {
        editor_logs_dir_macos()
    } else if cfg!(target_os = "windows") {
        editor_logs_dir_windows()
    } else {
        editor_logs_dir_linux()
    }
}

fn crash_logs_dir_macos() -> PathBuf {
    dirs::home_dir()
        .map(|h| h.join("Library").join("Logs").join("DiagnosticReports"))
        .unwrap_or_else(|| PathBuf::from("~/Library/Logs/DiagnosticReports"))
}

fn crash_logs_dir_windows() -> PathBuf {
    if let Some(local) = dirs::config_local_dir() {
        return local.join("CrashDumps");
    }
    if let Some(appdata) = std::env::var_os("LOCALAPPDATA") {
        return PathBuf::from(appdata).join("CrashDumps");
    }
    PathBuf::from("C:\\Users\\Public\\AppData\\Local\\CrashDumps")
}

fn crash_logs_dir_linux() -> PathBuf {
    // No canonical Unity crash folder on Linux; ~/_test is sometimes used by
    // Unity test runners, but for end-user projects ~/.config/unity3d is the
    // closest standard location.
    editor_logs_dir_linux()
}

fn crash_logs_dir() -> PathBuf {
    if cfg!(target_os = "macos") {
        crash_logs_dir_macos()
    } else if cfg!(target_os = "windows") {
        crash_logs_dir_windows()
    } else {
        crash_logs_dir_linux()
    }
}

pub fn resolve_log_paths(project_path: &Path) -> LogPaths {
    let editor_dir = editor_logs_dir();
    let editor_file = editor_dir.join("Editor.log");
    let player_dir = project_path.join("Logs");
    let crash_dir = crash_logs_dir();

    LogPaths {
        editor_logs_folder: Some(path_to_string(&editor_dir)),
        editor_log_file: Some(path_to_string(&editor_file)),
        player_logs_folder: Some(path_to_string(&player_dir)),
        crash_logs_folder: Some(path_to_string(&crash_dir)),
    }
}

#[tauri::command]
pub fn log_paths(project_path: String) -> LogPaths {
    resolve_log_paths(Path::new(&project_path))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fresh_dir(name: &str) -> PathBuf {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(name);
        std::fs::create_dir_all(&path).unwrap();
        // keep tempdir alive for the test by leaking it (tests are short-lived)
        std::mem::forget(dir);
        path
    }

    #[test]
    fn resolve_log_paths_includes_player_dir_under_project() {
        let project = fresh_dir("MyProj");
        let result = resolve_log_paths(&project);
        let expected = project.join("Logs").to_string_lossy().to_string();
        assert_eq!(result.player_logs_folder.as_deref(), Some(expected.as_str()));
    }

    #[test]
    fn resolve_log_paths_editor_dir_is_absolute() {
        let project = fresh_dir("MyProj");
        let result = resolve_log_paths(&project);
        let editor = result
            .editor_logs_folder
            .as_deref()
            .expect("editor_logs_folder set");
        assert!(Path::new(editor).is_absolute());
    }

    #[test]
    fn resolve_log_paths_crash_dir_is_absolute() {
        let project = fresh_dir("MyProj");
        let result = resolve_log_paths(&project);
        let crash = result
            .crash_logs_folder
            .as_deref()
            .expect("crash_logs_folder set");
        assert!(Path::new(crash).is_absolute());
    }

    #[test]
    fn resolve_log_paths_editor_file_lives_in_editor_dir() {
        let project = fresh_dir("MyProj");
        let result = resolve_log_paths(&project);
        let dir = result.editor_logs_folder.as_deref().unwrap();
        let file = result.editor_log_file.as_deref().unwrap();
        assert!(
            file.starts_with(dir),
            "editor_log_file {file} should be inside editor dir {dir}"
        );
        assert!(file.ends_with("Editor.log"));
    }

    #[test]
    fn log_paths_command_matches_resolver() {
        let project = fresh_dir("MyProj2");
        let direct = resolve_log_paths(&project);
        let via_cmd = log_paths(project.to_string_lossy().to_string());
        assert_eq!(direct.editor_logs_folder, via_cmd.editor_logs_folder);
        assert_eq!(direct.editor_log_file, via_cmd.editor_log_file);
        assert_eq!(direct.player_logs_folder, via_cmd.player_logs_folder);
        assert_eq!(direct.crash_logs_folder, via_cmd.crash_logs_folder);
    }

    #[test]
    fn log_paths_default_is_empty() {
        let p = LogPaths::default();
        assert!(p.editor_logs_folder.is_none());
        assert!(p.editor_log_file.is_none());
        assert!(p.player_logs_folder.is_none());
        assert!(p.crash_logs_folder.is_none());
    }
}
