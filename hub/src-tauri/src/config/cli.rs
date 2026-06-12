//! CLI mode: launch Unity from the terminal.
//!
//! Implemented for M1.5-9 ("CLI mode — auto-launch matching Unity"). The
//! `tauri-plugin-cli` integration is wired in `lib.rs`; this module owns
//! the pure-Rust argv parser and the spawn path so the same launch logic
//! can be exercised in unit tests without booting Tauri.
//!
//! ## Flow
//!
//! 1. `parse_argv` scans `std::env::args()` for `-projectPath <path>` or
//!    `-projectPath=<path>`. The flag is the only one the Hub recognizes
//!    in v1; everything else is ignored so dev-tooling args (e.g.
//!    `cargo tauri dev`'s internal flags) are never mistaken for a path.
//! 2. `run_cli_mode` validates the path is a Unity project root
//!    (`Assets/` + `ProjectSettings/`), reads the version from
//!    `ProjectSettings/ProjectVersion.txt`, resolves the matching Unity
//!    install via the same discovery service the GUI uses, and spawns
//!    the editor with `-projectPath <path>`.
//! 3. On success the matching `projects.json` entry is updated with
//!    `lastLaunchPid` + `lastLaunchAt` (no frecency bump from CLI: the
//!    `lastLaunchAt` is what frecency cares about, and the user did not
//!    learn anything new about the project).
//!
//! ## Why a manual parser (not just `tauri-plugin-cli::CliExt`)?
//!
//! `main` calls `run_cli_mode` *before* `tauri::Builder::default()` is
//! built. Tauri's runtime is not yet alive, so `app.cli().matches()` is
//! not callable. The plugin is still installed for the JS side
//! (`getMatches()`) and the schema in `tauri.conf.json`, but the spawn
//! path uses the manual parser for the same reason. Both paths agree on
//! the flag shape because the schema declares `-projectPath` with
//! `takesValue: true`.

use std::env;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitCode};

use crate::config::discovery;
use crate::config::env_vars;
use crate::config::launch;
use crate::config::persistence;
use crate::config::schemas::ProjectsFile;

/// Outcome of `parse_argv`. `Run` means the caller should perform the
/// launch + exit flow; `Gui` means the caller should start Tauri
/// normally so the Hub window opens.
#[derive(Debug, PartialEq, Eq)]
pub enum CliDecision {
    /// `-projectPath <path>` was present (and parseable). `path` is the
    /// raw value from argv; validation happens later in `run_cli_mode`.
    Run { path: String },
    /// No `-projectPath` flag was present. Start the GUI.
    Gui,
}

/// Print usage / version to stdout. Used by `run_cli_mode` when the
/// caller passes `--help` or `-h`. Kept dead-simple so it can be called
/// from both GUI and CLI flows.
pub fn print_help() {
    println!(
        "unity-hub-pro {version}\n\n\
         USAGE:\n  \
           unity-hub-pro -projectPath <path>\n\n\
         OPTIONS:\n  \
           -projectPath <path>   Open the Unity project at <path> with the\n  \
                                 matching installed Unity version and exit.\n  \
           -h, --help            Show this help text and exit.\n  \
           -V, --version         Show the version and exit.\n",
        version = env!("CARGO_PKG_VERSION"),
    );
}

/// Parse argv and decide whether to enter CLI mode. Pure function —
/// does not touch the filesystem, environment, or Tauri. Visible at
/// crate scope for unit tests.
pub fn parse_argv<I, S>(args: I) -> CliDecision
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut iter = args.into_iter();
    // First token is the binary name in a normal invocation; the
    // caller can pass any iterator (the test suite passes the post-skip
    // slice) so we do not assume it is present here.
    while let Some(arg) = iter.next() {
        let arg = arg.as_ref();
        // Support `-projectPath <value>` and `-projectPath=<value>`.
        if let Some(value) = arg
            .strip_prefix("-projectPath=")
            .or_else(|| arg.strip_prefix("--projectPath="))
        {
            return CliDecision::Run {
                path: value.to_string(),
            };
        }
        if arg == "-projectPath" || arg == "--projectPath" {
            match iter.next() {
                Some(next) => {
                    return CliDecision::Run {
                        path: next.as_ref().to_string(),
                    }
                }
                None => {
                    // `-projectPath` with no value is a user error; we
                    // still treat it as a CLI run with an empty path so
                    // the validation branch in `run_cli_mode` reports
                    // the missing path with a one-line error.
                    return CliDecision::Run {
                        path: String::new(),
                    };
                }
            }
        }
    }
    CliDecision::Gui
}

/// Convenience wrapper around `parse_argv` that uses `std::env::args`.
pub fn parse_env() -> CliDecision {
    parse_argv(env::args().skip(1))
}

/// Validate that the path looks like a Unity project root
/// (`Assets/` + `ProjectSettings/`). Mirrors `projects::is_unity_project_root`
/// but is duplicated here to avoid leaking that helper's visibility.
fn is_unity_project_root(path: &Path) -> Result<(), String> {
    if !path.is_dir() {
        return Err(format!("Path is not a directory: {}", path.display()));
    }
    if !path.join("Assets").is_dir() {
        return Err(format!(
            "Missing 'Assets' folder in {}",
            path.display()
        ));
    }
    if !path.join("ProjectSettings").is_dir() {
        return Err(format!(
            "Missing 'ProjectSettings' folder in {}",
            path.display()
        ));
    }
    Ok(())
}

/// Print a one-line error to stderr and return the exit code. Centralised
/// so all CLI failure paths produce the same `unity-hub-pro: <message>`
/// shape on stderr (matches GNU coreutils' convention). Always exits
/// with status `1` — the launch flow does not currently distinguish
/// failure categories in the exit code, so the user can rely on
/// `if unity-hub-pro -projectPath …; then …` to mean "Unity opened".
fn cli_error(message: impl AsRef<str>) -> u8 {
    eprintln!("unity-hub-pro: {}", message.as_ref());
    1
}

/// Run the CLI launch flow. Returns the process exit code. This is the
/// entry point called from `main` before Tauri starts.
///
/// - `Run { path }` was returned by `parse_argv` and is passed in
///   directly so tests can drive the same code path with a synthetic
///   argv without mutating `env::args`.
/// - On success: returns 0 after recording the launch in `projects.json`
///   and printing a confirmation line on stdout.
/// - On failure: returns a non-zero exit code and prints a single line
///   to stderr. The Hub window is never shown.
pub fn run_cli_mode(decision: CliDecision) -> ExitCode {
    let path = match decision {
        CliDecision::Gui => {
            // Caller bug — this function should only run when the
            // argv parser asked for CLI mode. Treat as a non-fatal
            // internal error so a future refactor cannot silently fall
            // through to GUI startup.
            return ExitCode::from(cli_error(
                "internal: run_cli_mode called without a project path",
            ));
        }
        CliDecision::Run { path } => path,
    };

    if path.is_empty() {
        return ExitCode::from(cli_error(
            "-projectPath was given without a value",
        ));
    }

    let project_path = PathBuf::from(&path);
    if let Err(reason) = is_unity_project_root(&project_path) {
        return ExitCode::from(cli_error(reason));
    }

    let version = match launch::read_project_version(&project_path) {
        Some(v) => v,
        None => {
            return ExitCode::from(cli_error(format!(
                "could not read Unity version from {} (no ProjectSettings/ProjectVersion.txt or m_EditorVersion line)",
                project_path.display()
            )));
        }
    };

    let settings = persistence::load_settings();
    let discovery_result = discovery::discover_unity_installations(&settings);
    let install = discovery_result.installations.iter().find(|i| i.version == version);
    let install = match install {
        Some(i) => i,
        None => {
            return ExitCode::from(cli_error(format!(
                "Unity {} is not installed (discovery scanned {} parent folders; add the install root in Settings → Additional parent folders, set $UNITY_HUB, or install the version via Unity Hub)",
                version,
                discovery_result.installations.len(),
            )));
        }
    };

    let executable = match launch::get_unity_executable_path(Path::new(&install.path)) {
        Some(p) => p,
        None => {
            return ExitCode::from(cli_error(format!(
                "Unity {} install is missing the editor executable at {}",
                version,
                install.path
            )));
        }
    };

    // M1.5-17: layer the project's per-project env vars on top of the
    // inherited parent-process env. CLI mode reads `projects.json`
    // directly (the in-memory mirror is not initialised at this point
    // — Tauri has not been built yet), so a missing projects file is a
    // no-op rather than a panic. The lookup is best-effort: the
    // collision-confirmation modal only exists in the GUI, so the CLI
    // launches the project with whatever env vars are configured.
    let mut command = Command::new(&executable);
    command.arg("-projectPath").arg(&path);
    if let Some(env_vars_map) = load_env_vars_for_project(&path) {
        env_vars::apply_to_command(&mut command, &env_vars_map);
    }

    let child = match command.spawn() {
        Ok(c) => c,
        Err(e) => {
            return ExitCode::from(cli_error(format!(
                "Failed to spawn Unity: {}",
                e
            )));
        }
    };

    let pid = child.id();
    record_cli_launch(&project_path.to_string_lossy(), &version, pid);

    println!(
        "unity-hub-pro: launched Unity {} (pid {}) for {}",
        version,
        pid,
        project_path.display()
    );
    ExitCode::SUCCESS
}

/// Persist the CLI launch on the matching `projects.json` entry so the
/// GUI sees the same `lastLaunchPid` / `lastLaunchAt` it would see after
/// a normal launch. If the path is not yet in the list we silently skip
/// the write — the user has not asked us to add the project, just to
/// launch it. `lastModifiedAt` and `unityVersion` are refreshed on the
/// next GUI refresh; CLI mode intentionally does not bump `frecency` to
/// avoid skewing the sort when the user is just opening a project
/// repeatedly from a terminal script.
fn record_cli_launch(path: &str, version: &str, pid: u32) {
    let mut projects: ProjectsFile = persistence::load_projects();
    let now = chrono::Utc::now().to_rfc3339();
    let mut touched = false;
    for project in projects.projects.iter_mut() {
        if project.path == path {
            project.last_launch_pid = Some(pid);
            project.last_launch_at = Some(now.clone());
            if project.unity_version.is_none() {
                project.unity_version = Some(version.to_string());
            }
            touched = true;
            break;
        }
    }
    if touched {
        if let Err(e) = persistence::save_projects(&projects) {
            log::warn!("CLI mode: failed to persist launch record: {}", e);
        }
    }
}

/// Best-effort lookup of the project's per-project env vars from the
/// on-disk `projects.json` file. Returns `None` when the project is
/// not in the list, when the file is missing, or when the file fails
/// to parse — the CLI flow must not panic on a corrupt or absent
/// projects file. The caller layers the result on top of the
/// inherited env.
fn load_env_vars_for_project(path: &str) -> Option<std::collections::BTreeMap<String, String>> {
    let projects = persistence::load_projects();
    projects
        .projects
        .into_iter()
        .find(|p| p.path == path)
        .map(|p| p.env_vars)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn parse_argv_no_flags_returns_gui() {
        assert_eq!(parse_argv(args(&[])), CliDecision::Gui);
        assert_eq!(
            parse_argv(args(&["-unrelated", "value"])),
            CliDecision::Gui
        );
    }

    #[test]
    fn parse_argv_project_path_with_separate_value() {
        let decision = parse_argv(args(&["-projectPath", "/path/to/proj"]));
        assert_eq!(
            decision,
            CliDecision::Run {
                path: "/path/to/proj".to_string()
            }
        );
    }

    #[test]
    fn parse_argv_project_path_with_equals() {
        let decision = parse_argv(args(&["-projectPath=/path/to/proj"]));
        assert_eq!(
            decision,
            CliDecision::Run {
                path: "/path/to/proj".to_string()
            }
        );
    }

    #[test]
    fn parse_argv_project_path_long_form() {
        let decision = parse_argv(args(&["--projectPath", "/p"]));
        assert_eq!(
            decision,
            CliDecision::Run {
                path: "/p".to_string()
            }
        );
    }

    #[test]
    fn parse_argv_project_path_with_no_value_yields_empty_path() {
        // `-projectPath` at the end of argv with no value is a user
        // error; we surface it via the validation branch rather than
        // silently treating it as GUI mode.
        let decision = parse_argv(args(&["-projectPath"]));
        assert_eq!(
            decision,
            CliDecision::Run {
                path: String::new()
            }
        );
    }

    #[test]
    fn parse_argv_ignores_unrelated_flags() {
        // `cargo tauri dev` and similar tools add their own flags; we
        // only react to `-projectPath` and `--projectPath`.
        let decision = parse_argv(args(&[
            "--config",
            "tauri.conf.json",
            "-projectPath",
            "/real/path",
        ]));
        assert_eq!(
            decision,
            CliDecision::Run {
                path: "/real/path".to_string()
            }
        );
    }

    #[test]
    fn parse_argv_takes_first_project_path_only() {
        // Defense against a malformed script that supplies the flag
        // twice: we honour the first occurrence and ignore the rest,
        // matching the rest of the Hub CLI surface.
        let decision = parse_argv(args(&[
            "-projectPath",
            "/first",
            "-projectPath",
            "/second",
        ]));
        assert_eq!(
            decision,
            CliDecision::Run {
                path: "/first".to_string()
            }
        );
    }

    #[test]
    fn is_unity_project_root_accepts_valid_root() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(dir.path().join("Assets")).unwrap();
        std::fs::create_dir_all(dir.path().join("ProjectSettings")).unwrap();
        assert!(is_unity_project_root(dir.path()).is_ok());
    }

    #[test]
    fn is_unity_project_root_rejects_missing_assets() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(dir.path().join("ProjectSettings")).unwrap();
        let err = is_unity_project_root(dir.path()).unwrap_err();
        assert!(err.contains("Assets"), "got: {}", err);
    }

    #[test]
    fn is_unity_project_root_rejects_missing_project_settings() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(dir.path().join("Assets")).unwrap();
        let err = is_unity_project_root(dir.path()).unwrap_err();
        assert!(err.contains("ProjectSettings"), "got: {}", err);
    }

    #[test]
    fn is_unity_project_root_rejects_missing_directory() {
        let err = is_unity_project_root(Path::new("/definitely/does/not/exist/cli-test-xyz"))
            .unwrap_err();
        assert!(err.contains("not a directory"), "got: {}", err);
    }

    #[test]
    fn run_cli_mode_rejects_empty_path() {
        // The decision says Run with an empty path; `run_cli_mode` must
        // short-circuit with a one-line error and a non-zero exit code
        // rather than crashing on the path check.
        let code = run_cli_mode(CliDecision::Run {
            path: String::new(),
        });
        assert_eq!(code, ExitCode::from(1u8));
    }

    #[test]
    fn run_cli_mode_rejects_non_unity_directory() {
        let dir = tempfile::tempdir().unwrap();
        let code = run_cli_mode(CliDecision::Run {
            path: dir.path().to_string_lossy().to_string(),
        });
        assert_eq!(code, ExitCode::from(1u8));
    }

    #[test]
    fn run_cli_mode_rejects_gui_decision() {
        // Defensive: callers must only invoke `run_cli_mode` for a Run
        // decision. Passing Gui returns the same error shape as a
        // missing path so the test can assert the exit code without
        // touching stderr.
        let code = run_cli_mode(CliDecision::Gui);
        assert_eq!(code, ExitCode::from(1u8));
    }

    #[test]
    fn run_cli_mode_reports_missing_version_file() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(dir.path().join("Assets")).unwrap();
        std::fs::create_dir_all(dir.path().join("ProjectSettings")).unwrap();
        // Intentionally no `ProjectVersion.txt` written.
        let code = run_cli_mode(CliDecision::Run {
            path: dir.path().to_string_lossy().to_string(),
        });
        assert_eq!(code, ExitCode::from(1u8));
    }

    #[test]
    fn run_cli_mode_reports_install_not_found() {
        // A project with a `ProjectVersion.txt` that names a Unity
        // version we have not installed (no discovery roots) must
        // return non-zero with a clear "version not installed" error.
        let dir = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(dir.path().join("Assets")).unwrap();
        std::fs::create_dir_all(dir.path().join("ProjectSettings")).unwrap();
        std::fs::write(
            dir.path().join("ProjectSettings").join("ProjectVersion.txt"),
            "m_EditorVersion: 9999.9.9f99\n",
        )
        .unwrap();
        let code = run_cli_mode(CliDecision::Run {
            path: dir.path().to_string_lossy().to_string(),
        });
        assert_eq!(code, ExitCode::from(1u8));
    }
}
