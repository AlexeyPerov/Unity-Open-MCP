pub mod config;

use config::commands::AppState;
use std::sync::Mutex;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // Initialize logging. `env_logger` is a dependency but was never
    // initialized, so every `log::*` call elsewhere in the backend was
    // silently dropped. Best-effort init: ignores the "already set"
    // error so a double-init (e.g. CLI mode) is harmless. Default level
    // is `warn` (silent in normal use); set `RUST_LOG=info` during a
    // launch repro to see the per-operation timing spans in the dev
    // terminal that complement the Status Drawer's end-to-end numbers.
    let _ = env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("warn"))
        .try_init();

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
        .manage(config::command_runner::CommandRunnerState::default())
        .invoke_handler(tauri::generate_handler![
            config::commands::load_settings,
            config::commands::save_settings,
            config::commands::load_projects,
            config::commands::save_projects,
            config::seed::seed_from_unity_hub,
            config::seed::discover_hub_projects,
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
            config::commands::get_os_default_hub_paths,
            config::logs::log_paths,
            config::logs::asset_store_paths,
            config::logs::crash_log_path,
            config::process::kill_unity,
            config::process::is_pid_alive,
            config::diagnostics::get_diagnostics_paths,
            config::diagnostics::export_diagnostics,
            config::sizes::get_project_sizes,
            config::build_target::get_default_build_target,
            config::render_pipeline::get_render_pipeline,
            config::git_branch::get_git_branches,
            config::running_unity::scan_running_unity,
            config::walk_up_scan::start_walk_up_scan,
            config::walk_up_scan::cancel_walk_up_scan,
            config::new_project::create_new_project,
            config::new_project::list_hub_templates,
            config::upgrade::upgrade_unity,
            config::upgrade::upgrade_candidates,
            config::projects::set_project_hidden,
            config::projects::set_project_stale,
            config::env_vars::env_var_collisions,
            config::releases::fetch_releases,
            config::releases::refresh_releases_command,
            config::hub_install::open_unity_hub_install,
            config::ai_toolkit::validate_toolkit_root,
            config::ai_toolkit::check_node_version,
            config::wizard::detect_project_state,
            config::wizard::read_manifest,
            config::wizard::plan_manifest_merge,
            config::wizard::write_manifest_merge,
            config::mcp_config::plan_mcp_config,
            config::mcp_config::write_mcp_config,
            config::mcp_config::plan_skill_copy,
            config::mcp_config::copy_skill_files,
            config::clear::clear_ai_setup,
            config::launch_verify::launch_for_verify,
            config::launch_verify::poll_bridge_ping,
            config::bridge_port::resolve_bridge_port,
            config::line_count::count_lines,
            config::line_count::count_lines_cached,
            config::git_status::git_status,
            config::upm::manifest::read_package_manifest,
            config::upm::manifest::write_package_manifest,
            config::upm::migrate::migrate_package_files,
            config::upm::meta::regenerate_package_meta_guids,
            config::upm::meta::add_missing_package_meta,
            config::upm::create::create_package,
            config::command_runner::run_project_build,
            config::command_runner::run_project_test,
            config::command_runner::run_project_custom,
            config::command_runner::run_project_npm_version,
            config::command_runner::run_project_npm_publish_dry_run,
            config::command_runner::run_project_npm_publish,
            config::command_runner::read_mcp_package_info,
            config::command_runner::query_npm_registry,
            config::command_runner::stop_project_command,
            config::command_runner::project_command_running,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
