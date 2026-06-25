//! Tauri command surface.
//!
//! All IPC between the Svelte UI and the Rust backend goes through
//! these `#[tauri::command]` functions. The commands are thin: they
//! resolve resources, delegate to the persistence/detection/loader
//! modules, and return plain serializable types. Frontend validation
//! of scenario structure lives in `packages/core`; the backend only
//! owns disk I/O + subprocess capability hooks.
//!
//! The resource dir (bundled profiles + scenarios) is resolved via
//! Tauri's `path::PathResolver`. In `cargo test` there is no app
//! handle, so the loader modules fall back to the manifest-relative
//! dev path — hence commands pass `None` resource dir and rely on that
//! fallback when the resolver is unavailable.

use std::path::PathBuf;

use tauri::{AppHandle, State};
use tauri::Manager;

use crate::persistence::{self, StateLoad};
use crate::profile_loader;
use crate::project_kind;
use crate::scenario_loader;
use crate::schemas::{
    AppConfig, EngineProfile, ProjectCheck, ScenarioFile, SuiteState, TestState, Status,
};

/// Shared app state. We cache the active engine profile + last-known
/// project so the UI does not re-resolve them on every render. The
/// project root is the single source of scoping for state I/O.
#[derive(Default)]
pub struct AppState {
    pub project_root: std::sync::Mutex<Option<PathBuf>>,
    pub engine_profile: std::sync::Mutex<Option<EngineProfile>>,
}

/// Resolve the bundled-resource dir, when available.
fn resource_dir(handle: &AppHandle) -> Option<PathBuf> {
    handle.path().resource_dir().ok()
}

/// Return the bundled engine profile. v1 always returns the `unity`
/// profile; the indirection keeps the command shape ready for a future
/// multi-engine app.
#[tauri::command]
pub fn get_engine_profile(handle: AppHandle) -> Result<EngineProfile, String> {
    profile_loader::active_profile(resource_dir(&handle).as_ref())
}

/// Validate a candidate project folder against the active profile's
/// markers. On success, remember it as the scoped project root and
/// persist it as the last-project pointer (phase-1 task 3).
#[tauri::command]
pub fn select_project(
    handle: AppHandle,
    state: State<'_, AppState>,
    path: String,
) -> Result<ProjectCheck, String> {
    let profile = profile_loader::active_profile(resource_dir(&handle).as_ref())?;
    let candidate = PathBuf::from(&path);
    let check = project_kind::check_project(&candidate, &profile);
    if check.valid {
        // Scope the app to this project and persist the pointer.
        *state.project_root.lock().unwrap() = Some(candidate.clone());
        *state.engine_profile.lock().unwrap() = Some(profile.clone());
        let _ = persistence::save_app_config(&AppConfig {
            last_project_path: Some(path.clone()),
            engine_profile_id: Some(profile.id.clone()),
        });
    }
    Ok(check)
}

/// Load the persisted last-project pointer (may be `None` on first
/// launch) plus the configured engine profile id.
#[tauri::command]
pub fn get_last_project() -> AppConfig {
    persistence::load_app_config()
}

/// Return the active scoped project root, if any. The UI uses this to
/// decide whether to show the project bar prompt or the runner view.
#[tauri::command]
pub fn get_active_project(state: State<'_, AppState>) -> Option<String> {
    state
        .project_root
        .lock()
        .unwrap()
        .as_ref()
        .map(|p| p.to_string_lossy().to_string())
}

/// Discover + parse all scenario files for the active engine. Returns
/// raw `{ source, content }` documents; the frontend core loader
/// validates structure and reports per-file errors.
#[tauri::command]
pub fn read_scenarios(
    handle: AppHandle,
) -> Result<scenario_loader::ScenarioReadResult, String> {
    let profile = profile_loader::active_profile(resource_dir(&handle).as_ref())?;
    Ok(scenario_loader::read_scenarios(&profile.id, resource_dir(&handle).as_ref()))
}

/// Load the suite state for the active project. The discriminant
/// (`ok | missing | malformed | incompatible`) drives the UI's
/// warn+reset flow (phase-1 task 6 / validation #5).
#[tauri::command]
pub fn load_suite_state(
    handle: AppHandle,
    state: State<'_, AppState>,
) -> Result<SuiteStateOutcome, String> {
    let profile = profile_loader::active_profile(resource_dir(&handle).as_ref())?;
    let root = state
        .project_root
        .lock()
        .unwrap()
        .clone()
        .ok_or_else(|| "No project selected.".to_string())?;
    match persistence::load_state(&root, &profile.paths.state_file) {
        StateLoad::Ok(s) => Ok(SuiteStateOutcome {
            kind: "ok".to_string(),
            state: Some(s),
            reason: None,
            found_version: None,
        }),
        StateLoad::Missing => {
            // Seed a fresh empty state so the UI has a project block to
            // render and the operator can start marking steps.
            let fresh = persistence::empty_state(
                &root.to_string_lossy(),
                &profile.id,
            );
            Ok(SuiteStateOutcome {
                kind: "missing".to_string(),
                state: Some(fresh),
                reason: None,
                found_version: None,
            })
        }
        StateLoad::Malformed { reason } => Ok(SuiteStateOutcome {
            kind: "malformed".to_string(),
            state: None,
            reason: Some(reason),
            found_version: None,
        }),
        StateLoad::Incompatible { found } => Ok(SuiteStateOutcome {
            kind: "incompatible".to_string(),
            state: None,
            reason: Some(format!(
                "Local suite state version {found} is incompatible with this app (expected {}). \
                 Reset local Validation Suite data to continue.",
                crate::schemas::STATE_VERSION
            )),
            found_version: Some(found),
        }),
    }
}

/// Serializable load outcome. The `kind` discriminant selects how the
/// UI reacts; `state` is present for `ok`/`missing`, absent otherwise.
#[derive(serde::Serialize)]
pub struct SuiteStateOutcome {
    pub kind: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state: Option<SuiteState>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub found_version: Option<u32>,
}

/// Persist the suite state for the active project atomically. The UI
/// sends the full state (it owns step-status transitions via the core
/// package); the backend only writes it.
#[tauri::command]
pub fn save_suite_state(
    handle: AppHandle,
    state: State<'_, AppState>,
    suite: SuiteState,
) -> Result<(), String> {
    let profile = profile_loader::active_profile(resource_dir(&handle).as_ref())?;
    let root = state
        .project_root
        .lock()
        .unwrap()
        .clone()
        .ok_or_else(|| "No project selected.".to_string())?;
    persistence::save_state(&root, &profile.paths.state_file, &suite)
        .map_err(|e| format!("Failed to save state: {e}"))
}

/// Reset all local Validation Suite data for the active project: delete
/// the state file so the next load reports `missing` and seeds fresh.
/// Used by the incompatible-shape warning's reset guidance (phase-1
/// validation #5). Actual payload files under `actualsDir`/`exportsDir`
/// are not deleted in Phase 1 — reset targets the state file only.
#[tauri::command]
pub fn reset_suite_state(
    handle: AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let profile = profile_loader::active_profile(resource_dir(&handle).as_ref())?;
    let root = state
        .project_root
        .lock()
        .unwrap()
        .clone()
        .ok_or_else(|| "No project selected.".to_string())?;
    let path = persistence::state_file_path(&root, &profile.paths.state_file);
    if path.exists() {
        std::fs::remove_file(&path).map_err(|e| format!("Failed to remove state: {e}"))?;
    }
    Ok(())
}

/// Open a path in the OS file manager / default app. Used by
/// `external_doc` steps (phase-1 task: doc open capability). The
/// `tauri-plugin-opener` plugin enforces the capability allowlist.
#[tauri::command]
pub fn reveal_path(path: String) -> Result<(), String> {
    // Delegate to the opener plugin from the frontend via its JS API;
    // this command is retained as a stable IPC seam for future
    // backend-side path validation (Phase 2 sandbox).
    let _ = path;
    Ok(())
}

// ── helpers re-exported for tests ────────────────────────────────────────────
/// Construct a default awaiting test state (used by tests).
pub fn test_state_default() -> TestState {
    TestState {
        status: Status::Awaiting,
        step_status: serde_json::Map::new(),
        started_at: None,
        completed_at: None,
        actuals_refs: serde_json::Map::new(),
        manifest_refs: serde_json::Map::new(),
    }
}

/// (Tests only) parse a scenario file payload the way the backend
/// forwards it — kept here so integration tests can build inputs.
pub fn scenario_file(source: &str, content: serde_json::Value) -> ScenarioFile {
    ScenarioFile {
        source: source.to_string(),
        content,
    }
}
