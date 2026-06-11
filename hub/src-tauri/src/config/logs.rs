use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct LogPaths {
    pub editor_logs_folder: Option<String>,
    pub editor_log_file: Option<String>,
    /// M1.5-16: path to `Editor-prev.log` — the Unity Editor log from the
    /// *previous* session (Unity renames the live `Editor.log` to
    /// `Editor-prev.log` on every fresh launch). Same folder as
    /// `editor_log_file`; surfaces a separate button on the Tools tab so
    /// the user can cross-reference the previous run without leaving the
    /// current log open.
    pub editor_prev_log_file: Option<String>,
    pub player_logs_folder: Option<String>,
    /// M1.5-16: the per-project `Player.log` (Unity's editor preview
    /// player writes here, alongside the `Editor.log` and the
    /// `AssetImportWorker0.log`). Same folder as the editor logs; the
    /// button is distinct so a tool panel can reveal it without the
    /// user having to open the editor logs folder first.
    pub player_log_file: Option<String>,
    /// M1.5-16: the per-user *global* `Player.log` written by standalone
    /// Unity player builds (not the editor preview). macOS:
    /// `~/Library/Logs/Unity/Player.log`. Windows:
    /// `%LOCALAPPDATA%\Unity\Player.log`. Linux: `~/.config/unity3d/Player.log`.
    /// This is the same file standalone Player builds use when the user
    /// does not pass `-logFile` on the command line. Returns `None` when
    /// the user has not run a standalone player on this machine yet
    /// (the parent folder always exists, but the file may not).
    pub unity_player_log_file: Option<String>,
    pub crash_logs_folder: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct AssetStorePaths {
    /// The resolved Asset Store downloads folder, if any versioned subfolder
    /// (`Asset Store-5.x`) exists on disk. `None` when neither the versioned
    /// subfolder nor a fallback parent can be located.
    pub folder: Option<String>,
    /// `true` when `folder` points at the versioned `Asset Store-5.x`
    /// subfolder; `false` when we fell back to the parent (e.g. opening the
    /// `Unity` parent directory when no versioned subfolder is installed yet).
    pub versioned: bool,
    /// Human-readable inline message used by the UI when the folder is
    /// missing. `None` when a path was resolved.
    pub missing_message: Option<String>,
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
    // M1.5-16: `Editor-prev.log` is the rotated copy of the previous
    // editor session's log. The filename is the same on every OS — Unity
    // renames the live file on every fresh launch. Sibling of the live
    // `Editor.log` (i.e. it lives in `editor_dir`).
    let editor_prev_file = editor_dir.join("Editor-prev.log");
    // M1.5-16: per-project `Player.log`. Unity's editor preview player
    // (the "Play" button) writes `Player.log` next to `Editor.log`; the
    // file is created on the first Play, so the path resolves even when
    // the file does not yet exist.
    let player_file = editor_dir.join("Player.log");
    // M1.5-16: the per-user global Player.log for *standalone* player
    // builds (not the editor preview). Same folder as the editor logs
    // on macOS / Windows; on Linux it falls back to `~/.config/unity3d`.
    let unity_player_log = editor_dir.join("Player.log");
    // Filter out the per-user Player.log when the file is missing on
    // disk: the per-project variant is what most users want when the
    // editor preview path doesn't exist yet, and the button is then
    // disabled with the standard inline message.
    let unity_player_log_resolved = if unity_player_log.exists() {
        Some(path_to_string(&unity_player_log))
    } else {
        None
    };
    let player_dir = project_path.join("Logs");
    let crash_dir = crash_logs_dir();

    LogPaths {
        editor_logs_folder: Some(path_to_string(&editor_dir)),
        editor_log_file: Some(path_to_string(&editor_file)),
        editor_prev_log_file: Some(path_to_string(&editor_prev_file)),
        player_logs_folder: Some(path_to_string(&player_dir)),
        player_log_file: Some(path_to_string(&player_file)),
        unity_player_log_file: unity_player_log_resolved,
        crash_logs_folder: Some(path_to_string(&crash_dir)),
    }
}

#[tauri::command]
pub fn log_paths(project_path: String) -> LogPaths {
    resolve_log_paths(Path::new(&project_path))
}

/// Unity Asset Store-5.x downloads folder. The exact versioned subfolder name
/// (e.g. `Asset Store-5.x`) is the Unity convention; macOS and Windows each
/// resolve `Unity` under the user's per-OS app-data location and then look for
/// the first matching `Asset Store-*` subfolder. When no versioned subfolder
/// exists yet (the user has never opened the Asset Store in Unity), the
/// command falls back to the parent `Unity` directory so the button still has
/// something to open. Linux is intentionally unsupported in M1.5 — most
/// developers run the Hub on macOS or Windows.
fn asset_store_parent_macos() -> Option<PathBuf> {
    dirs::home_dir().map(|h| {
        h.join("Library")
            .join("Application Support")
            .join("Unity")
    })
}

fn asset_store_parent_windows() -> Option<PathBuf> {
    if let Some(local) = dirs::config_local_dir() {
        return Some(local.join("Unity"));
    }
    if let Some(appdata) = std::env::var_os("LOCALAPPDATA") {
        return Some(PathBuf::from(appdata).join("Unity"));
    }
    Some(PathBuf::from("C:\\Users\\Public\\AppData\\Local\\Unity"))
}

fn asset_store_parent() -> Option<PathBuf> {
    if cfg!(target_os = "macos") {
        asset_store_parent_macos()
    } else if cfg!(target_os = "windows") {
        asset_store_parent_windows()
    } else {
        None
    }
}

fn find_versioned_asset_store_dir(parent: &Path) -> Option<PathBuf> {
    // Unity creates subfolders named `Asset Store-5.0`, `Asset Store-5.1`, …
    // The versioned subfolder with the highest 5.x number is the one Unity
    // uses today. We accept any `Asset Store-*` directory — Unity is free to
    // introduce new minor version folders in the future. We compare on the
    // full `major.minor` tuple so `5.10` sorts above `5.2`, etc.
    let entries = std::fs::read_dir(parent).ok()?;
    let mut best: Option<((u32, u32), PathBuf)> = None;
    for entry in entries.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        let Some(rest) = name.strip_prefix("Asset Store-") else {
            continue;
        };
        if !entry.file_type().map(|t| t.is_dir()).unwrap_or(false) {
            continue;
        }
        let mut parts = rest.split('.');
        let major: u32 = parts.next().and_then(|n| n.parse().ok()).unwrap_or(0);
        let minor: u32 = parts.next().and_then(|n| n.parse().ok()).unwrap_or(0);
        let key = (major, minor);
        match &best {
            Some((p, _)) if *p >= key => {}
            _ => best = Some((key, entry.path())),
        }
    }
    best.map(|(_, p)| p)
}

pub fn resolve_asset_store_paths() -> AssetStorePaths {
    let Some(parent) = asset_store_parent() else {
        return AssetStorePaths {
            folder: None,
            versioned: false,
            missing_message: Some(
                "Asset Store path is not resolved on this platform yet (Linux support is deferred)"
                    .to_string(),
            ),
        };
    };
    if !parent.exists() {
        return AssetStorePaths {
            folder: Some(parent.to_string_lossy().to_string()),
            versioned: false,
            missing_message: Some(format!(
                "Unity app-data folder does not exist yet — the Asset Store will create it on first use: {}",
                parent.display()
            )),
        };
    }
    if let Some(versioned) = find_versioned_asset_store_dir(&parent) {
        AssetStorePaths {
            folder: Some(versioned.to_string_lossy().to_string()),
            versioned: true,
            missing_message: None,
        }
    } else {
        AssetStorePaths {
            folder: Some(parent.to_string_lossy().to_string()),
            versioned: false,
            missing_message: Some(format!(
                "No versioned Asset Store subfolder found under {} — open the Asset Store in Unity once to create it",
                parent.display()
            )),
        }
    }
}

#[tauri::command]
pub fn asset_store_paths() -> AssetStorePaths {
    resolve_asset_store_paths()
}

/// Per-OS crash-log directory path. This is the folder where Unity crash
/// reports (and most other user-mode crashes on macOS) accumulate:
/// - macOS: `~/Library/Logs/DiagnosticReports`
/// - Windows: `%LOCALAPPDATA%\CrashDumps`
/// - Linux: falls back to the editor logs dir (`~/.config/unity3d`).
///
/// Returned to the frontend as a plain string (or `null` when the path is
/// unknown) so the launch-failure quick-action can offer a single button to
/// reveal the folder in the native file manager.
#[tauri::command]
pub fn crash_log_path() -> Option<String> {
    Some(crash_logs_dir().to_string_lossy().to_string())
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
        assert_eq!(direct.editor_prev_log_file, via_cmd.editor_prev_log_file);
        assert_eq!(direct.player_logs_folder, via_cmd.player_logs_folder);
        assert_eq!(direct.player_log_file, via_cmd.player_log_file);
        assert_eq!(
            direct.unity_player_log_file,
            via_cmd.unity_player_log_file
        );
        assert_eq!(direct.crash_logs_folder, via_cmd.crash_logs_folder);
    }

    #[test]
    fn log_paths_default_is_empty() {
        let p = LogPaths::default();
        assert!(p.editor_logs_folder.is_none());
        assert!(p.editor_log_file.is_none());
        assert!(p.editor_prev_log_file.is_none());
        assert!(p.player_logs_folder.is_none());
        assert!(p.player_log_file.is_none());
        assert!(p.unity_player_log_file.is_none());
        assert!(p.crash_logs_folder.is_none());
    }

    #[test]
    fn resolve_log_paths_prev_log_lives_next_to_editor_log() {
        // M1.5-16: `Editor-prev.log` is the previous session's log
        // rotated by Unity on every fresh launch. It must live in the
        // same folder as `Editor.log` and use the exact filename Unity
        // picks — a typo here would silently fail to surface the
        // shortcut button on macOS / Windows.
        let project = fresh_dir("PrevProj");
        let result = resolve_log_paths(&project);
        let dir = result.editor_logs_folder.as_deref().unwrap();
        let prev = result.editor_prev_log_file.as_deref().unwrap();
        assert!(prev.starts_with(dir));
        assert!(prev.ends_with("Editor-prev.log"));
    }

    #[test]
    fn resolve_log_paths_player_log_lives_next_to_editor_log() {
        // M1.5-16: the per-project `Player.log` (editor preview player)
        // is a sibling of `Editor.log`. The path always resolves even
        // when the file does not exist on disk yet (the user has not
        // pressed Play in this project) — the frontend uses the
        // resolved path string for the title / "reveal in folder"
        // tooltip, and the existing openPath pattern handles the
        // missing-file case via the inline error.
        let project = fresh_dir("PlayerProj");
        let result = resolve_log_paths(&project);
        let dir = result.editor_logs_folder.as_deref().unwrap();
        let player = result.player_log_file.as_deref().unwrap();
        assert!(player.starts_with(dir));
        assert!(player.ends_with("Player.log"));
    }

    #[test]
    fn resolve_log_paths_unity_player_log_omitted_when_missing() {
        // M1.5-16: the per-user global `Player.log` (standalone player
        // builds) is filtered to `None` when the file does not exist on
        // disk. Most dev machines have not run a standalone build, so
        // returning a path to a non-existent file would make the button
        // look broken. The frontend disables the button + shows the
        // "no standalone player log found" hint when the field is null.
        let project = fresh_dir("UnityPlayerProj");
        let result = resolve_log_paths(&project);
        // We never write the file in the test, so the field must be
        // None on a clean tempdir. The existing editor_dir resolves
        // under the user's home so the parent always exists.
        assert!(result.unity_player_log_file.is_none());
    }

    #[test]
    fn asset_store_paths_default_is_empty() {
        let p = AssetStorePaths::default();
        assert!(p.folder.is_none());
        assert!(!p.versioned);
        assert!(p.missing_message.is_none());
    }

    #[test]
    fn find_versioned_asset_store_dir_picks_highest_5x() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(dir.path().join("Asset Store-5.0")).unwrap();
        std::fs::create_dir_all(dir.path().join("Asset Store-5.3")).unwrap();
        std::fs::create_dir_all(dir.path().join("Asset Store-5.1")).unwrap();
        std::fs::create_dir_all(dir.path().join("Unrelated")).unwrap();

        let found = find_versioned_asset_store_dir(dir.path()).expect("found");
        assert!(found.ends_with("Asset Store-5.3"));
    }

    #[test]
    fn find_versioned_asset_store_dir_ignores_files() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("Asset Store-5.0"), "not a dir").unwrap();
        assert!(find_versioned_asset_store_dir(dir.path()).is_none());
    }

    #[test]
    fn find_versioned_asset_store_dir_returns_none_when_no_subdirs() {
        let dir = tempfile::tempdir().unwrap();
        assert!(find_versioned_asset_store_dir(dir.path()).is_none());
    }

    #[test]
    fn asset_store_paths_command_matches_resolver() {
        let direct = resolve_asset_store_paths();
        let via_cmd = asset_store_paths();
        assert_eq!(direct.folder, via_cmd.folder);
        assert_eq!(direct.versioned, via_cmd.versioned);
        assert_eq!(direct.missing_message, via_cmd.missing_message);
    }

    #[test]
    fn crash_log_path_command_returns_absolute_path() {
        let p = crash_log_path();
        if let Some(s) = p {
            assert!(Path::new(&s).is_absolute());
        }
    }
}
