use std::collections::HashMap;
use std::path::Path;
use tauri::State;
use std::sync::Mutex;

use crate::config::discovery::DiscoveryResult;
use crate::config::persistence;
use crate::config::schemas::{os_default_hub_paths, ProjectsFile, Settings};
use crate::config::walk_up_scan::WalkUpRegistry;

pub struct AppState {
    pub settings: Mutex<Settings>,
    pub projects: Mutex<ProjectsFile>,
    pub discovery_cache: Mutex<Option<DiscoveryResult>>,
    /// M1.5-11: in-process registry of running walk-up directory
    /// scans. Keyed by `scan_id` so a Cancel on scan A leaves scan B
    /// untouched when the user starts a second scan before the first
    /// has settled. See `config::walk_up_scan` for the full design.
    pub walk_up_registry: Mutex<WalkUpRegistry>,
}

#[tauri::command]
pub fn load_settings(state: State<AppState>) -> Settings {
    let settings = persistence::load_settings();
    let mut guard = state.settings.lock().unwrap();
    *guard = settings.clone();
    settings
}

#[tauri::command]
pub fn save_settings(state: State<AppState>, settings: Settings) -> Result<(), String> {
    persistence::save_settings(&settings).map_err(|e| e.to_string())?;
    let mut guard = state.settings.lock().unwrap();
    *guard = settings;
    Ok(())
}

#[tauri::command]
pub fn load_projects(state: State<AppState>) -> ProjectsFile {
    let projects = persistence::load_projects();
    let mut guard = state.projects.lock().unwrap();
    *guard = projects.clone();
    projects
}

#[tauri::command]
pub fn save_projects(state: State<AppState>, projects: ProjectsFile) -> Result<(), String> {
    persistence::save_projects(&projects).map_err(|e| e.to_string())?;
    let mut guard = state.projects.lock().unwrap();
    *guard = projects;
    Ok(())
}

#[tauri::command]
pub fn check_paths_exists(paths: Vec<String>) -> HashMap<String, bool> {
    let mut result: HashMap<String, bool> = HashMap::with_capacity(paths.len());
    for path in paths {
        let exists = Path::new(&path).exists();
        result.insert(path, exists);
    }
    result
}

/// Returns the OS-default Unity Hub editor folders for the current
/// host. The Settings tab compares each "additional parent folder"
/// row against this list to decide whether to render a Remove button
/// (default paths are informational and not user-removable).
#[tauri::command]
pub fn get_os_default_hub_paths() -> Vec<String> {
    os_default_hub_paths()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn check_paths_exists_returns_true_for_existing() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().to_string_lossy().to_string();
        let result = check_paths_exists(vec![path.clone()]);
        assert_eq!(result.get(&path), Some(&true));
    }

    #[test]
    fn check_paths_exists_returns_false_for_missing() {
        let result = check_paths_exists(vec!["/nonexistent/path/xyz_123".to_string()]);
        assert_eq!(
            result.get("/nonexistent/path/xyz_123"),
            Some(&false)
        );
    }

    #[test]
    fn check_paths_exists_handles_empty_input() {
        let result = check_paths_exists(vec![]);
        assert!(result.is_empty());
    }

    #[test]
    fn check_paths_exists_mixes_existing_and_missing() {
        let dir = tempfile::tempdir().unwrap();
        let existing = dir.path().to_string_lossy().to_string();
        let missing = format!("{}/nope", existing);
        let result = check_paths_exists(vec![existing.clone(), missing.clone()]);
        assert_eq!(result.get(&existing), Some(&true));
        assert_eq!(result.get(&missing), Some(&false));
    }
}
