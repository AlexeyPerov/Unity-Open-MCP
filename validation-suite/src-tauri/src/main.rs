// Prevents an additional console window on Windows in release. DO NOT REMOVE!!
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() -> std::process::ExitCode {
    validation_suite_lib::run();
    std::process::ExitCode::SUCCESS
}
