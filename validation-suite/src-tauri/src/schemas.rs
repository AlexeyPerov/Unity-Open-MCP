//! Serde DTOs for the Validation Suite backend.
//!
//! These mirror the frozen contracts in the core TS package
//! (`packages/core/src/types.ts`) and the engine-profile spec
//! (`specs/testsuite-tauri/engine-profiles/unity.md`). The state-file
//! shape is pinned so a version mismatch triggers the warn+reset policy
//! rather than a silent migration (idea.md → Schema policy for v1).

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// The only supported `.state.json` shape version. Bumped only when the
/// shape changes; no migration logic is ever written.
pub const STATE_VERSION: u32 = 1;

/// Status values shared by tests and steps (idea.md → UI shape).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Status {
    Awaiting,
    Done,
    Blocked,
}

impl Default for Status {
    fn default() -> Self {
        Status::Awaiting
    }
}

/// Per-scenario persisted state (unity.md → State file schema). Field
/// names are `camelCase` to match the frozen on-disk shape + the TS
/// contract, so the UI can read/write the same object through IPC.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TestState {
    pub status: Status,
    /// Keyed by step id from the scenario JSON.
    #[serde(default)]
    pub step_status: serde_json::Map<String, Value>,
    #[serde(default)]
    pub started_at: Option<String>,
    #[serde(default)]
    pub completed_at: Option<String>,
    /// Actual payload filenames in `actualsDir`, keyed by step id.
    #[serde(default)]
    pub actuals_refs: serde_json::Map<String, Value>,
    /// Per-step manifest reference for reset (Phase 2; `null` in v1).
    #[serde(default)]
    pub manifest_refs: serde_json::Map<String, Value>,
}

/// Active project + engine profile block.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectState {
    pub path: String,
    pub engine_profile_id: String,
    pub last_opened_at: String,
}

/// The on-disk `.state.json` shape. Frozen in unity.md.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SuiteState {
    pub version: u32,
    pub project: ProjectState,
    #[serde(default)]
    pub tests: std::collections::BTreeMap<String, TestState>,
}

/// Minimal app-config record persisted in the OS config dir: the last
/// opened project path so the project bar can pre-select it on launch.
/// (phase-1 task 3: persist last project path in app config dir.)
#[derive(Clone, Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct AppConfig {
    #[serde(default)]
    pub last_project_path: Option<String>,
    #[serde(default)]
    pub engine_profile_id: Option<String>,
}

// ── Phase 2: manifest + action execution DTOs ────────────────────────────────

/// Kind of artifact a manifest entry describes (mirrors the core TS
/// `ManifestEntryKind`). Determines how reset reverts it.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ManifestEntryKind {
    /// Reset deletes the path (and companion if any).
    Created,
    /// Reset restores the recorded `snapshot` (pre-patch file contents).
    Modified,
    /// Reset does nothing (the delete was the cleanup).
    Deleted,
}

/// One artifact recorded by an `fs_*` action. `path` is project-relative
/// (forward-slash) so the manifest is portable. `snapshot` holds the
/// pre-patch file bytes (utf-8) for `Modified` entries.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestEntry {
    pub kind: ManifestEntryKind,
    pub path: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub companion_path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub snapshot: Option<String>,
}

/// A per-step manifest blob: the ordered list of artifacts a step's
/// setup actions produced. Persisted under `UserSettings/ValidationSuite/`
/// and referenced from `.state.json` by blob id.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StepManifest {
    pub scenario_id: String,
    pub step_id: String,
    #[serde(default)]
    pub entries: Vec<ManifestEntry>,
}

/// Severity of an action-log line (mirrors the core TS `ActionLogLevel`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ActionLogLevel {
    Info,
    Warn,
    Error,
}

/// A single line in a step's action log panel.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ActionLogLine {
    pub level: ActionLogLevel,
    pub message: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub snippet: Option<String>,
}

/// Parsed MCP CLI result body for `mcp_tool` actions.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct McpResult {
    pub is_error: bool,
    pub result: Value,
}

/// Outcome of running a single setup action (mirrors core TS
/// `ActionResult`).
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ActionResult {
    pub ok: bool,
    pub summary: String,
    #[serde(default)]
    pub logs: Vec<ActionLogLine>,
    #[serde(default)]
    pub entries: Vec<ManifestEntry>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mcp: Option<McpResult>,
}

impl ActionResult {
    pub fn ok(summary: impl Into<String>) -> Self {
        Self {
            ok: true,
            summary: summary.into(),
            logs: vec![],
            entries: vec![],
            mcp: None,
        }
    }

    pub fn err(summary: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            ok: false,
            summary: summary.into(),
            logs: vec![ActionLogLine {
                level: ActionLogLevel::Error,
                message: message.into(),
                snippet: None,
            }],
            entries: vec![],
            mcp: None,
        }
    }
}

/// Outcome of running all actions in a step (mirrors core TS
/// `StepRunResult`).
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StepRunResult {
    pub ok: bool,
    #[serde(default)]
    pub results: Vec<ActionResult>,
    /// Backend manifest blob id for this step (null when nothing mutated).
    pub manifest_id: Option<String>,
    #[serde(default)]
    pub logs: Vec<ActionLogLine>,
}

/// Outcome of a reset (step or all).
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResetResult {
    pub ok: bool,
    #[serde(default)]
    pub warnings: Vec<String>,
    #[serde(default)]
    pub logs: Vec<ActionLogLine>,
}

/// Result of a project detection check (unity.md → Project detection).
/// Carries a clear, human-readable reason for rejection so the project
/// bar can show actionable copy.
#[derive(Clone, Debug, Serialize)]
pub struct ProjectCheck {
    pub valid: bool,
    pub path: String,
    pub reason: Option<String>,
}

/// A scenario document as loaded from disk, before the frontend loader
/// validates it. The raw JSON is forwarded verbatim so all validation
/// lives in one place (`packages/core`).
#[derive(Clone, Debug, Serialize)]
pub struct ScenarioFile {
    pub source: String,
    pub content: Value,
}

/// Engine profile paths block (unity.md → Path conventions).
/// `camelCase` matches the bundled JSON + the TS contract so the same
/// file round-trips through both layers without field renaming.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfilePaths {
    pub fixture_root: String,
    pub state_root: String,
    pub state_file: String,
    pub actuals_dir: String,
    pub exports_dir: String,
}

/// Project markers (unity.md → Project detection).
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectMarkers {
    pub dirs: Vec<String>,
    pub files: Vec<String>,
}

/// Companion-artifact rule (unity.md → Companion artifacts).
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanionRule {
    pub primary: String,
    pub companion: String,
}

/// An engine profile (unity.md). Loaded from a bundled JSON file.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineProfile {
    pub id: String,
    pub display_name: String,
    pub mcp_cli_binary: String,
    pub paths: ProfilePaths,
    pub markers: ProjectMarkers,
    pub companions: Vec<CompanionRule>,
    #[serde(default)]
    pub placeholders: Vec<String>,
    #[serde(default = "default_tool_prefix")]
    pub tool_name_prefix: String,
}

fn default_tool_prefix() -> String {
    "unity_open_mcp_".to_string()
}
