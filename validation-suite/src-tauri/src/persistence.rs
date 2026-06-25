//! Atomic persistence for app config and per-project suite state.
//!
//! Mirrors the Hub's persistence pattern
//! (`hub/src-tauri/src/config/persistence.rs`): write to a sibling
//! temp file, `sync_all`, then rename onto the target for a crash-safe
//! atomic replace. On a corrupt file we back it up to `.corrupt` and
//! fall back to defaults rather than crashing (phase-1 task 6;
//! unity.md → State file schema → robust load).
//!
//! The **state file** lives under the selected project
//! (`UserSettings/ValidationSuite/.state.json`), not in the app config
//! dir, so it travels with the project. The **app config** (last
//! project pointer) lives in the app config dir.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

use crate::paths;
use crate::schemas::{AppConfig, SuiteState, TestState, STATE_VERSION};

/// Rename a corrupt file to `<name>.corrupt` so the operator can recover
/// it manually, then log. Best-effort — never fails the caller.
fn backup_corrupt(path: &Path) {
    let backup = path.with_extension("json.corrupt");
    let _ = fs::rename(path, &backup);
    log::warn!("Corrupt file backed up to {}", backup.display());
}

/// Atomically write `data` to `path` via a temp file + rename.
pub fn atomic_write(path: &Path, data: &str) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let tmp_path = path.with_extension("json.tmp");
    {
        let mut tmp = fs::File::create(&tmp_path)?;
        tmp.write_all(data.as_bytes())?;
        tmp.sync_all()?;
    }
    fs::rename(&tmp_path, path)?;
    Ok(())
}

// ── App config (last project pointer) ────────────────────────────────────────

/// Load the app config, falling back to defaults on a missing or
/// corrupt file. A corrupt file is backed up and replaced.
pub fn load_app_config() -> AppConfig {
    let path = paths::app_config_path();
    if !path.exists() {
        return AppConfig::default();
    }
    match fs::read_to_string(&path) {
        Ok(content) => match serde_json::from_str::<AppConfig>(&content) {
            Ok(c) => c,
            Err(e) => {
                log::warn!("Corrupt app-config.json ({}), restoring defaults", e);
                backup_corrupt(&path);
                AppConfig::default()
            }
        },
        Err(e) => {
            log::warn!("Cannot read app-config.json ({}), restoring defaults", e);
            AppConfig::default()
        }
    }
}

/// Persist the app config atomically to the app config dir.
pub fn save_app_config(config: &AppConfig) -> std::io::Result<()> {
    paths::ensure_config_dir()?;
    let json = serde_json::to_string_pretty(config)?;
    atomic_write(&paths::app_config_path(), &json)
}

// ── Suite state (per-project) ────────────────────────────────────────────────

/// Where the per-project state file lives for a given project root and
/// profile `stateFile` path. Resolved as `<project>/<stateFile>` so it
/// always travels with the project (unity.md → Path conventions).
pub fn state_file_path(project_root: &Path, state_file: &str) -> PathBuf {
    project_root.join(state_file)
}

/// Outcome of loading a project's state file.
pub enum StateLoad {
    /// A valid, shape-compatible state file was loaded.
    Ok(SuiteState),
    /// No state file exists yet for this project.
    Missing,
    /// The file exists but is not valid JSON or has the wrong envelope.
    Malformed { reason: String },
    /// The file parsed but its `version` does not match
    /// [`STATE_VERSION`] — the operator must reset local suite data
    /// (unity.md → version policy; no migration).
    Incompatible { found: u32 },
}

/// Load a project's suite state. Never panics: a corrupt or
/// incompatible file is reported via the [`StateLoad`] discriminant so
/// the UI can warn + offer reset (phase-1 task 6 / validation #5).
pub fn load_state(project_root: &Path, state_file: &str) -> StateLoad {
    let path = state_file_path(project_root, state_file);
    if !path.exists() {
        return StateLoad::Missing;
    }
    let content = match fs::read_to_string(&path) {
        Ok(c) => c,
        Err(e) => {
            return StateLoad::Malformed {
                reason: format!("Cannot read state file: {e}"),
            }
        }
    };
    let parsed: serde_json::Value = match serde_json::from_str(&content) {
        Ok(v) => v,
        Err(e) => {
            log::warn!("Corrupt state file ({e}), backing up");
            backup_corrupt(&path);
            return StateLoad::Malformed {
                reason: format!("State file is not valid JSON: {e}"),
            };
        }
    };
    // Version check first — a wrong version is a deliberate stop signal,
    // not a "malformed" file the app should silently rebuild.
    let version = parsed
        .get("version")
        .and_then(|v| v.as_u64())
        .map(|v| v as u32);
    match version {
        Some(v) if v != STATE_VERSION => {
            return StateLoad::Incompatible { found: v };
        }
        None => {
            backup_corrupt(&path);
            return StateLoad::Malformed {
                reason: "State root must be an object with a numeric \"version\".".to_string(),
            };
        }
        _ => {}
    }
    match serde_json::from_value::<SuiteState>(parsed) {
        Ok(s) => StateLoad::Ok(s),
        Err(e) => {
            log::warn!("State file failed schema ({e}), backing up");
            backup_corrupt(&path);
            StateLoad::Malformed {
                reason: format!("State file shape is unreadable: {e}"),
            }
        }
    }
}

/// Atomically write a suite state file for a project.
pub fn save_state(project_root: &Path, state_file: &str, state: &SuiteState) -> std::io::Result<()> {
    let path = state_file_path(project_root, state_file);
    let json = serde_json::to_string_pretty(state)?;
    atomic_write(&path, &json)
}

/// Build a fresh empty state for a project + profile (no tests recorded).
pub fn empty_state(project_path: &str, engine_profile_id: &str) -> SuiteState {
    SuiteState {
        version: STATE_VERSION,
        project: crate::schemas::ProjectState {
            path: project_path.to_string(),
            engine_profile_id: engine_profile_id.to_string(),
            last_opened_at: now_iso(),
        },
        tests: std::collections::BTreeMap::new(),
    }
}

/// Build a fresh per-test default state. The frontend usually drives
/// this via the core package, but the backend exposes it so commands
/// can seed an entry for a scenario without round-tripping JSON.
pub fn empty_test_state() -> TestState {
    TestState {
        status: crate::schemas::Status::Awaiting,
        step_status: serde_json::Map::new(),
        started_at: None,
        completed_at: None,
        actuals_refs: serde_json::Map::new(),
        manifest_refs: serde_json::Map::new(),
    }
}

/// ISO-8601 timestamp for `now` (UTC, milliseconds).
pub fn now_iso() -> String {
    use chrono::Utc;
    Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schemas::Status;

    #[test]
    fn atomic_write_creates_and_overwrites() {
        let dir = tempfile::tempdir().unwrap();
        let p = dir.path().join("x.json");
        atomic_write(&p, "first").unwrap();
        atomic_write(&p, "second").unwrap();
        assert_eq!(fs::read_to_string(&p).unwrap(), "second");
        assert!(!dir.path().join("x.json.tmp").exists());
    }

    #[test]
    fn atomic_write_creates_parent_dirs() {
        // The state file lives under UserSettings/ValidationSuite/ which
        // may not exist on a fresh project — atomic_write must create it.
        let dir = tempfile::tempdir().unwrap();
        let p = dir.path().join("UserSettings/ValidationSuite/.state.json");
        atomic_write(&p, "{}").unwrap();
        assert!(p.exists());
    }

    #[test]
    fn load_state_reports_missing_when_absent() {
        let dir = tempfile::tempdir().unwrap();
        match load_state(dir.path(), "UserSettings/ValidationSuite/.state.json") {
            StateLoad::Missing => {}
            other => panic!("expected Missing, got {other:?}", other = match other {
                StateLoad::Ok(_) => "Ok",
                StateLoad::Malformed { .. } => "Malformed",
                StateLoad::Incompatible { .. } => "Incompatible",
                StateLoad::Missing => "Missing",
            }),
        }
    }

    #[test]
    fn load_state_roundtrips_a_valid_state() {
        let dir = tempfile::tempdir().unwrap();
        let rel = "UserSettings/ValidationSuite/.state.json";
        let state = empty_state(dir.path().to_str().unwrap(), "unity");
        save_state(dir.path(), rel, &state).unwrap();
        match load_state(dir.path(), rel) {
            StateLoad::Ok(loaded) => {
                assert_eq!(loaded.version, STATE_VERSION);
                assert_eq!(loaded.project.engine_profile_id, "unity");
            }
            other => panic!("expected Ok, got {:?}", other_kind(&other)),
        }
    }

    #[test]
    fn load_state_reports_malformed_on_bad_json() {
        let dir = tempfile::tempdir().unwrap();
        let rel = "UserSettings/ValidationSuite/.state.json";
        atomic_write(&state_file_path(dir.path(), rel), "{not json").unwrap();
        match load_state(dir.path(), rel) {
            StateLoad::Malformed { reason } => assert!(reason.contains("JSON")),
            other => panic!("expected Malformed, got {:?}", other_kind(&other)),
        }
        // The corrupt file is backed up to .corrupt (mirrors the Hub
        // persistence pattern); the original path no longer exists.
        assert!(!state_file_path(dir.path(), rel).exists());
        assert!(state_file_path(dir.path(), rel)
            .with_extension("json.corrupt")
            .exists());
    }

    #[test]
    fn load_state_reports_incompatible_on_version_mismatch() {
        let dir = tempfile::tempdir().unwrap();
        let rel = "UserSettings/ValidationSuite/.state.json";
        let bad = serde_json::json!({
            "version": 99,
            "project": { "path": "/p", "engine_profile_id": "unity", "last_opened_at": "x" },
            "tests": {}
        });
        atomic_write(
            &state_file_path(dir.path(), rel),
            &serde_json::to_string_pretty(&bad).unwrap(),
        )
        .unwrap();
        match load_state(dir.path(), rel) {
            StateLoad::Incompatible { found } => assert_eq!(found, 99),
            other => panic!("expected Incompatible, got {:?}", other_kind(&other)),
        }
        // Not backed up — the operator may want to inspect/reset it.
        assert!(state_file_path(dir.path(), rel).exists());
    }

    #[test]
    fn empty_test_state_defaults_to_awaiting() {
        let t = empty_test_state();
        assert_eq!(t.status, Status::Awaiting);
        assert!(t.step_status.is_empty());
    }

    fn other_kind(s: &StateLoad) -> &'static str {
        match s {
            StateLoad::Ok(_) => "Ok",
            StateLoad::Missing => "Missing",
            StateLoad::Malformed { .. } => "Malformed",
            StateLoad::Incompatible { .. } => "Incompatible",
        }
    }
}
