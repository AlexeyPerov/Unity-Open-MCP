//! Validation Suite — Tauri backend entry point.
//!
//! Wires the persistence, project-detection, profile, and scenario
//! modules into a Tauri app. The frontend (SvelteKit + Svelte 5) talks
//! to these modules exclusively through the command surface in
//! [`commands`]; structural validation of scenarios lives in the
//! engine-neutral core TS package (`packages/core`).

pub mod commands;
pub mod export_store;
pub mod fs_ops;
pub mod manifest_store;
pub mod mcp_runner;
pub mod paths;
pub mod persistence;
pub mod profile_loader;
pub mod project_kind;
pub mod sandbox;
pub mod scenario_loader;
pub mod schemas;

use commands::AppState;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // Best-effort log init (mirrors the Hub). `RUST_LOG=info` surfaces
    // the persistence/detection spans during a repro.
    let _ = env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("warn"))
        .try_init();

    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .manage(AppState::default())
        .invoke_handler(tauri::generate_handler![
            commands::get_engine_profile,
            commands::select_project,
            commands::get_last_project,
            commands::get_active_project,
            commands::read_scenarios,
            commands::load_suite_state,
            commands::save_suite_state,
            commands::reset_suite_state,
            commands::reveal_path,
            commands::resolve_fixture_root,
            commands::fs_copy_action,
            commands::fs_patch_action,
            commands::fs_delete_action,
            commands::mcp_tool_action,
            commands::mcp_health_action,
            commands::save_step_manifest,
            commands::load_step_manifest,
            commands::delete_step_manifest,
            commands::save_export,
        ])
        .run(tauri::generate_context!())
        .expect("error while running validation-suite");
}
