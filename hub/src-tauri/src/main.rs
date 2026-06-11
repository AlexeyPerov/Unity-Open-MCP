// Prevents additional console window on Windows in release, DO NOT REMOVE!!
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() -> std::process::ExitCode {
    // CLI mode (M1.5-9): if `-projectPath <path>` is on argv, validate
    // the path, resolve the matching Unity install, spawn the editor,
    // record `lastLaunchPid`/`lastLaunchAt`, and exit *before* Tauri is
    // ever constructed. The window never appears on either platform.
    //
    // We do the argv scan in `main` (not in a Tauri setup hook) so the
    // GUI is not even initialized for CLI invocations. The same parser
    // is also wired into `tauri-plugin-cli` so the JS side can read
    // matches from `tauri.conf.json` for the Settings → Diagnostics
    // "CLI help" copy.
    let decision = hub_lib::config::cli::parse_env();
    match decision {
        hub_lib::config::cli::CliDecision::Run { .. } => {
            hub_lib::config::cli::run_cli_mode(decision)
        }
        hub_lib::config::cli::CliDecision::Gui => {
            hub_lib::run();
            std::process::ExitCode::SUCCESS
        }
    }
}
