use tauri::State;
use std::sync::Mutex;

use crate::config::discovery::DiscoveryResult;
use crate::config::persistence;
use crate::config::schemas::{ProjectsFile, Settings};

pub struct AppState {
    pub settings: Mutex<Settings>,
    pub projects: Mutex<ProjectsFile>,
    pub discovery_cache: Mutex<Option<DiscoveryResult>>,
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
