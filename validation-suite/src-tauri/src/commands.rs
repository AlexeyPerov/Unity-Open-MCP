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

use serde_json::Value;
use tauri::{AppHandle, State};
use tauri::Manager;

use crate::persistence::{self, StateLoad};
use crate::profile_loader;
use crate::project_kind;
use crate::scenario_loader;
use crate::schemas::{
    ActionResult, AppConfig, EngineProfile, ProjectCheck, ScenarioFile, StepManifest, SuiteState,
    TestState, Status,
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
        // Canonicalize before scoping so the project root the action
        // executor sandboxes against matches the canonical paths the
        // fs ops resolve to (on macOS `/var` → `/private/var` etc.).
        // Falls back to the raw path if canonicalization fails.
        let canonical = std::fs::canonicalize(&candidate).unwrap_or(candidate.clone());
        *state.project_root.lock().unwrap() = Some(canonical.clone());
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

/// Resolve the active project root + profile for an action command. All
/// phase-2 action commands share this prologue: they need both the
/// sandbox root and the profile (companions, CLI binary).
fn active_root_profile(
    state: &State<'_, AppState>,
) -> Result<(PathBuf, EngineProfile), String> {
    let root = state
        .project_root
        .lock()
        .unwrap()
        .clone()
        .ok_or_else(|| "No project selected.".to_string())?;
    let profile = state
        .engine_profile
        .lock()
        .unwrap()
        .clone()
        .ok_or_else(|| "No engine profile active.".to_string())?;
    Ok((root, profile))
}

/// Resolve the fixture root (absolute) for a scenario id by interpolating
/// `<test-id>` in the profile's `fixtureRoot` pattern. The TS runner uses
/// this to build its placeholder context before expanding action params.
#[tauri::command]
pub fn resolve_fixture_root(
    handle: AppHandle,
    scenario_id: String,
) -> Result<String, String> {
    let profile = profile_loader::active_profile(resource_dir(&handle).as_ref())?;
    Ok(fixture_root_abs(&profile.paths.fixture_root, &scenario_id))
}

/// Build the absolute fixture root for a scenario id. `fixtureRoot` is a
/// profile-relative pattern with a `<test-id>` token (unity.md).
pub fn fixture_root_abs(fixture_root_pattern: &str, scenario_id: &str) -> String {
    // Pattern is project-relative (e.g. `Assets/_ValidationSuite/<test-id>/`);
    // we return it relative to the project root (the caller prepends the
    // root) — keep the trailing slash stripped for path-join ergonomics.
    let rel = fixture_root_pattern.replace("<test-id>", scenario_id);
    rel.trim_end_matches('/').to_string()
}

/// `fs_copy` action (phase-2 task 2). Copies a file or directory tree,
/// auto-tracking companions when the source companion exists. Paths are
/// project-relative and sandboxed to the project root.
#[tauri::command]
pub fn fs_copy_action(
    state: State<'_, AppState>,
    from: String,
    to: String,
) -> Result<ActionResult, String> {
    let (root, profile) = active_root_profile(&state)?;
    crate::fs_ops::fs_copy(&root, &from, &to, &profile.companions)
}

/// `fs_patch` action (phase-2 task 3). Applies deterministic patches,
/// snapshotting the pre-patch file for reset. `snapshot_override` (when
/// set) restores that exact content instead of applying patches (reset path).
#[tauri::command]
pub fn fs_patch_action(
    state: State<'_, AppState>,
    path: String,
    patches: Vec<Value>,
    snapshot_override: Option<String>,
) -> Result<ActionResult, String> {
    let (root, _profile) = active_root_profile(&state)?;
    crate::fs_ops::fs_patch(&root, &path, &patches, snapshot_override.as_deref())
}

/// `fs_delete` action (phase-2 task 4). Deletes manifest-listed paths only;
/// used by reset. Paths are sandboxed to the project root.
#[tauri::command]
pub fn fs_delete_action(
    state: State<'_, AppState>,
    paths: Vec<String>,
) -> Result<ActionResult, String> {
    let (root, _profile) = active_root_profile(&state)?;
    crate::fs_ops::fs_delete(&root, &paths)
}

/// `mcp_tool` action (phase-2 task 5). Runs an MCP tool via the engine
/// CLI (`unity-open-mcp run-tool`) and parses its JSON result, surfacing
/// `isError` and the tool body. `args` is an optional JSON object.
#[tauri::command]
pub fn mcp_tool_action(
    handle: AppHandle,
    state: State<'_, AppState>,
    tool: String,
    args: Option<Value>,
    timeout_ms: Option<u64>,
) -> Result<ActionResult, String> {
    let (_root, profile) = active_root_profile(&state)?;
    let project_root = state
        .project_root
        .lock()
        .unwrap()
        .clone()
        .ok_or_else(|| "No project selected.".to_string())?;
    let _ = handle;
    crate::mcp_runner::run_tool(
        &profile,
        &project_root.to_string_lossy(),
        &tool,
        args.as_ref(),
        timeout_ms,
        None,
    )
}

/// MCP health check (`status` or `ping`) via the engine CLI. Used to
/// surface bridge readiness in the project bar / action log (Phase 3
/// wires `bridge_status`; this command is the generic runner).
#[tauri::command]
pub fn mcp_health_action(
    state: State<'_, AppState>,
    subcommand: String,
    timeout_ms: Option<u64>,
) -> Result<ActionResult, String> {
    let (_root, profile) = active_root_profile(&state)?;
    let project_root = state
        .project_root
        .lock()
        .unwrap()
        .clone()
        .ok_or_else(|| "No project selected.".to_string())?;
    crate::mcp_runner::run_health(&profile, &project_root.to_string_lossy(), &subcommand, timeout_ms, None)
}

/// Persist a step manifest blob and return its id (phase-2: manifest
/// recording on every mutating setup action). Stored under the project's
/// `UserSettings/ValidationSuite/manifests/`.
#[tauri::command]
pub fn save_step_manifest(
    state: State<'_, AppState>,
    scenario_id: String,
    step_id: String,
    entries: Vec<crate::schemas::ManifestEntry>,
) -> Result<String, String> {
    let (root, profile) = active_root_profile(&state)?;
    let counter = crate::manifest_store::count_for(&root, &profile.paths.state_root, &scenario_id, &step_id) + 1;
    let id = crate::manifest_store::make_id(&scenario_id, &step_id, counter);
    let manifest = StepManifest {
        scenario_id: scenario_id.clone(),
        step_id: step_id.clone(),
        entries,
    };
    crate::manifest_store::save(&root, &profile.paths.state_root, &id, &manifest)
        .map_err(|e| format!("Failed to save manifest: {e}"))?;
    Ok(id)
}

/// Load a step manifest blob by id (best-effort; `null` when absent so
/// reset can warn rather than crash — phase-2 reset contract).
#[tauri::command]
pub fn load_step_manifest(
    state: State<'_, AppState>,
    id: String,
) -> Result<Option<StepManifest>, String> {
    let (root, profile) = active_root_profile(&state)?;
    crate::manifest_store::load(&root, &profile.paths.state_root, &id)
}

/// Delete a step manifest blob after reset consumes it.
#[tauri::command]
pub fn delete_step_manifest(
    state: State<'_, AppState>,
    id: String,
) -> Result<(), String> {
    let (root, profile) = active_root_profile(&state)?;
    crate::manifest_store::delete(&root, &profile.paths.state_root, &id)
        .map_err(|e| format!("Failed to delete manifest: {e}"))
}

/// Write a run-summary export markdown body to the project's `exportsDir`
/// (phase-5 deliverable: export). The body is built by the frontend
/// (`packages/core/src/export.ts`); the backend owns the atomic disk
/// write + a timestamped filename. Returns the project-relative path so
/// the UI can show where the file landed. `stem` is a short label
/// (e.g. `m9`); `generated_at` is the ISO-8601 timestamp already baked
/// into the export body.
#[tauri::command]
pub fn save_export(
    state: State<'_, AppState>,
    stem: String,
    generated_at: String,
    body: String,
) -> Result<String, String> {
    let (root, profile) = active_root_profile(&state)?;
    let filename = crate::export_store::export_filename(&stem, &generated_at);
    crate::export_store::save(&root, &profile.paths.exports_dir, &filename, &body)
        .map_err(|e| format!("Failed to write export: {e}"))
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
