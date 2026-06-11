pub mod config;

use config::commands::AppState;
use std::sync::Mutex;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_cli::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .manage(AppState {
            settings: Mutex::new(config::persistence::load_settings()),
            projects: Mutex::new(config::persistence::load_projects()),
            discovery_cache: Mutex::new(None),
            walk_up_registry: Mutex::new(config::walk_up_scan::WalkUpRegistry::default()),
        })
        .invoke_handler(tauri::generate_handler![
            config::commands::load_settings,
            config::commands::save_settings,
            config::commands::load_projects,
            config::commands::save_projects,
            config::seed::seed_from_unity_hub,
            config::discovery::discover_installations,
            config::discovery::refresh_discovery,
            config::launch::launch_project,
            config::launch::refresh_project_version,
            config::launch::run_unity_install,
            config::launch_log::get_launch_log_tail,
            config::projects::add_project,
            config::projects::refresh_all_projects,
            config::projects::remove_project,
            config::projects::relink_project,
            config::commands::check_paths_exists,
            config::logs::log_paths,
            config::logs::asset_store_paths,
            config::logs::crash_log_path,
            config::process::kill_unity,
            config::process::is_pid_alive,
            config::diagnostics::get_diagnostics_paths,
            config::diagnostics::export_diagnostics,
            config::sizes::get_project_sizes,
            config::build_target::get_default_build_target,
            config::git_branch::get_git_branches,
            config::running_unity::scan_running_unity,
            config::walk_up_scan::start_walk_up_scan,
            config::walk_up_scan::cancel_walk_up_scan,
            config::new_project::create_new_project,
            config::new_project::list_hub_templates,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
