mod config;

use config::commands::AppState;
use std::sync::Mutex;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_shell::init())
        .manage(AppState {
            settings: Mutex::new(config::persistence::load_settings()),
            projects: Mutex::new(config::persistence::load_projects()),
        })
        .invoke_handler(tauri::generate_handler![
            config::commands::load_settings,
            config::commands::save_settings,
            config::commands::load_projects,
            config::commands::save_projects,
            config::seed::seed_from_unity_hub,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
