//! M1.5-17 — project-level custom environment variables.
//!
//! Adds a small surface area on top of the Rust schema:
//!
//! - [`validate_key`] rejects empty / whitespace-only keys (the field
//!   is always required, no duplicate keys) before the value is
//!   written to `projects.json`.
//! - [`list_collisions`] returns the subset of keys that the spawned
//!   Unity would override in the parent process. The frontend surfaces
//!   the result in the confirmation modal when
//!   `safety.confirmEnvVarOverride` is on.
//! - [`apply_to_command`] mutates a `std::process::Command` so the
//!   spawned process inherits the project's `env_vars` (the child
//!   wins on key collision, which is the documented behaviour).
//! - [`env_var_collisions`] is the `#[tauri::command]` that the
//!   confirmation modal calls: it takes a project id, looks the
//!   project up in `AppState.projects`, and returns the sorted list
//!   of colliding keys (empty when no env vars collide with the
//!   parent process).
//!
//! ## Why a separate module?
//!
//! The launch path (M1 Plan 2 Task 2) and the CLI path (M1.5-9) both
//! need the same merge / collision helpers, and a third caller — the
//! confirmation modal — needs the key set without actually launching.
//! Keeping the helpers in one place means the launch and CLI tests can
//! assert the same shape from the same code path.

use std::collections::BTreeMap;
use std::process::Command;

use tauri::State;

use crate::config::commands::AppState;

/// Reject empty or whitespace-only env var keys. The frontend rejects
/// these in the input row, but the backend re-validates so a manual
/// edit to `projects.json` cannot crash a launch.
pub fn validate_key(key: &str) -> Result<(), String> {
    if key.trim().is_empty() {
        return Err("environment variable keys must be non-empty".to_string());
    }
    // `=` is invalid in env var names on every major platform; refuse
    // it here so a malformed value is caught at save-time.
    if key.contains('=') {
        return Err(format!(
            "environment variable keys cannot contain '=': {}",
            key
        ));
    }
    Ok(())
}

/// Detect keys that would override a parent-process variable. The
/// returned `Vec` is sorted alphabetically (the input is a
/// `BTreeMap`, so iteration is already stable) and de-duplicated.
///
/// `parent_env` is the resolved `std::env::vars()` snapshot taken at
/// the time of the call. In tests we pass a synthetic map so the
/// comparison logic is unit-testable without mutating the real
/// process environment.
pub fn list_collisions(
    env_vars: &BTreeMap<String, String>,
    parent_env: &BTreeMap<String, String>,
) -> Vec<String> {
    env_vars
        .keys()
        .filter(|k| parent_env.contains_key(*k))
        .cloned()
        .collect()
}

/// Merge the project's `env_vars` into a `Command`'s environment. The
/// caller still controls the executable + args + working dir; this
/// helper only owns the env-mutation policy.
///
/// The merge happens in two passes so the resulting `Command` is
/// auditable: the inherited environment stays intact, then the
/// per-project entries are layered on top. `Command::env` replaces
/// the value for keys that already exist in the inherited env, so
/// collisions resolve to the project's value (the documented
/// behaviour).
pub fn apply_to_command(cmd: &mut Command, env_vars: &BTreeMap<String, String>) {
    for (key, value) in env_vars {
        cmd.env(key, value);
    }
}

/// Tauri command that returns the list of env-var keys that the
/// spawned Unity would override in the parent process. Empty when
/// no env vars collide. Sorted alphabetically (BTreeMap iteration
/// is already stable). The lookup is non-mutating; the parent
/// environment is snapshotted via `std::env::vars()` at the time
/// of the call so a freshly-spawned subshell does not race with
/// the modal.
#[tauri::command]
pub fn env_var_collisions(
    state: State<AppState>,
    project_id: String,
) -> Result<Vec<String>, String> {
    let projects = {
        let guard = state.projects.lock().unwrap();
        guard.clone()
    };
    let Some(project) = projects.projects.iter().find(|p| p.id == project_id) else {
        return Err(format!("project not found: {}", project_id));
    };
    let parent_env: BTreeMap<String, String> = std::env::vars().collect();
    Ok(list_collisions(&project.env_vars, &parent_env))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_cmd() -> Command {
        // Use a portable no-op executable; the test only inspects
        // the environment we layer on top.
        Command::new(if cfg!(target_os = "windows") {
            "cmd"
        } else {
            "true"
        })
    }

    #[test]
    fn validate_key_rejects_empty() {
        assert!(validate_key("").is_err());
        assert!(validate_key("   ").is_err());
    }

    #[test]
    fn validate_key_rejects_equals() {
        let err = validate_key("FOO=BAR").unwrap_err();
        assert!(err.contains("="));
    }

    #[test]
    fn validate_key_accepts_canonical_names() {
        assert!(validate_key("MY_KEY").is_ok());
        assert!(validate_key("PATH_EXT").is_ok());
        assert!(validate_key("_UNDERSCORE").is_ok());
    }

    #[test]
    fn list_collisions_returns_only_keys_present_in_parent_env() {
        let mut env_vars = BTreeMap::new();
        env_vars.insert("FOO".to_string(), "child".to_string());
        env_vars.insert("BAR".to_string(), "child".to_string());
        env_vars.insert("BAZ".to_string(), "child".to_string());

        let mut parent = BTreeMap::new();
        parent.insert("FOO".to_string(), "parent".to_string());
        parent.insert("BAZ".to_string(), "parent".to_string());
        // BAR is not in the parent — no collision.

        let collisions = list_collisions(&env_vars, &parent);
        assert_eq!(collisions, vec!["BAZ".to_string(), "FOO".to_string()]);
        // The list is alphabetically sorted.
    }

    #[test]
    fn list_collisions_empty_when_no_overlap() {
        let mut env_vars = BTreeMap::new();
        env_vars.insert("ONLY_CHILD".to_string(), "x".to_string());
        let mut parent = BTreeMap::new();
        parent.insert("OTHER".to_string(), "x".to_string());
        assert!(list_collisions(&env_vars, &parent).is_empty());
    }

    #[test]
    fn list_collisions_empty_when_env_vars_empty() {
        let env_vars = BTreeMap::new();
        let mut parent = BTreeMap::new();
        parent.insert("FOO".to_string(), "x".to_string());
        assert!(list_collisions(&env_vars, &parent).is_empty());
    }

    #[test]
    fn apply_to_command_layers_env_vars() {
        // `Command::get_envs` was stabilised in Rust 1.57; use it to
        // assert the keys are present in the layered environment. The
        // helper returns `OsStr` values, which we convert to `String`
        // via `to_string_lossy` for the assertion comparison.
        let mut env_vars = BTreeMap::new();
        env_vars.insert("HUB_TEST_KEY".to_string(), "hello".to_string());
        env_vars.insert("HUB_TEST_KEY_2".to_string(), "world".to_string());

        let mut cmd = make_cmd();
        apply_to_command(&mut cmd, &env_vars);

        let as_map: std::collections::HashMap<String, String> = cmd
            .get_envs()
            .filter_map(|(k, v)| {
                let val = v?;
                Some((k.to_string_lossy().to_string(), val.to_string_lossy().to_string()))
            })
            .collect();
        assert_eq!(as_map.get("HUB_TEST_KEY").map(String::as_str), Some("hello"));
        assert_eq!(
            as_map.get("HUB_TEST_KEY_2").map(String::as_str),
            Some("world")
        );
    }

    #[test]
    fn apply_to_command_noop_when_env_vars_empty() {
        // Build the command fresh, take a snapshot, then verify the
        // helper did not mutate the env table. The first snapshot is
        // dropped before the second call so the borrow checker does
        // not complain about overlapping borrows.
        let mut cmd = make_cmd();
        let before_len = cmd.get_envs().count();
        apply_to_command(&mut cmd, &BTreeMap::new());
        let after_len = cmd.get_envs().count();
        assert_eq!(before_len, after_len);
    }
}
