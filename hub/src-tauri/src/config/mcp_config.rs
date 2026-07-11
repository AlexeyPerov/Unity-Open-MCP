//! M4 Plan 4 — wizard Step 4 MCP client config merge / write.
//!
//! Owns the **write-side** of the wizard Step 4 MCP config flow:
//!
//! - [`plan_mcp_config`] — read the existing client config (if
//!   any) and compute the merged value the writer would produce,
//!   without touching the file. Step 4 calls this to show a
//!   diff / preview.
//! - [`write_mcp_config`] — apply the merge. Creates a `.bak`
//!   next to the existing file (when one exists) and writes the
//!   merged value with an atomic rename. **Only** the
//!   `unity-open-mcp` entry under the per-client merge key
//!   (`mcpServers.unity-open-mcp` for Cursor / Claude Desktop,
//!   `mcp.unity-open-mcp` for OpenCode) is touched; every other
//!   key, every other MCP server, and every ordering choice
//!   outside our entry are preserved verbatim.
//! - [`plan_skill_copy`] / [`copy_skill_files`] — the Done-time
//!   skill copy behaviour per `hub-wizard.md` §Skill copy
//!   behavior. Copies `{toolkitRoot}/skills/unity-open-mcp/SKILL.md`
//!   into the project's `.claude/skills/unity-open-mcp/SKILL.md`
//!   (always) and, when OpenCode is selected, the
//!   `.opencode/skills/...` mirror.
//!
//! Client-id resolution and the env-var contract live in
//! `ai_toolkit.rs` / `ai_toolkit.ts`; this module is purely
//! about merging, writing, and copying files.
//!
//! Skill install paths are NOT hardcoded here. They come from the
//! single-source manifest at `<toolkitRoot>/skills/client-paths.json`
//! (see [`ClientPathsManifest`]). The MCP-client → skill-target
//! mapping lives in the same manifest, so adding a client means
//! editing the manifest, not Rust or TypeScript constants.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

use std::process::Stdio;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};

use super::wizard::claude_desktop_config_path;
use crate::config::ai_toolkit::derived_mcp_index_path;
use crate::config::bridge_port::{parse_override, resolve_port};
use crate::config::constants::{
    NPM_PACKAGE_LATEST, PORT_ENV_VAR, PROJECT_PATH_ENV_VAR, UNITY_PATH_ENV_VAR,
};
use crate::config::paths;

/// Hard deadline for the `node`-based skill generation spawn. The
/// generate-skill tool loads the MCP server, reads the project, and
/// writes one SKILL.md per target — heavier than `node --version`,
/// but still bounded. A hang here (corporate security tool, broken
/// node shim, a wedged MCP-server boot) previously blocked the
/// wizard's main thread indefinitely via the sync command. The spawn
/// now polls `try_wait` against this deadline and kills the child on
/// timeout, surfacing a real error instead.
const GENERATE_SKILL_TIMEOUT: Duration = Duration::from_secs(60);

/// Local alias for [`derived_mcp_index_path`]. The wizard Step 4
/// also needs to validate the path against the toolkit
/// fingerprint set, but the **planning** / **writing** logic
/// here only needs the resolved string.
fn resolve_mcp_index_path(toolkit_root: &str, mcp_index_override: &str) -> Option<String> {
    derived_mcp_index_path(toolkit_root, mcp_index_override)
        .map(|p| p.to_string_lossy().into_owned())
}

/// Resolve the local `mcp-server/dist/index.js` path for local
/// launch modes. Returns:
/// - `Ok(Some(path))` when a local mode needs a real index path
///   and one could be derived (toolkit root set, or override set).
/// - `Ok(None)` for npm launch modes (`Npx` / `Global`) — no
///   on-disk path is required, so the caller uses a sentinel.
/// - `Err` when a local mode was requested but no path could be
///   derived (no toolkit root, empty override). The caller turns
///   this into a typed `mcpPathInvalid` error.
fn resolve_index_for_mode(
    params: &McpConfigParams,
) -> Result<Option<String>, McpConfigError> {
    if !params.launch_mode.requires_local_index() {
        return Ok(None);
    }
    // `LocalOverride` only honors the override field; `Local` honors
    // the override when set, otherwise derives from the toolkit root.
    let override_set = !params.mcp_index_override.trim().is_empty();
    if params.launch_mode == McpLaunchMode::LocalOverride && !override_set {
        return Err(McpConfigError::new(
            "mcpPathInvalid",
            "Local-override launch mode requires a custom mcp-server/dist/index.js path.",
        ));
    }
    resolve_mcp_index_path(&params.toolkit_root, &params.mcp_index_override).map(Some).ok_or_else(|| {
        McpConfigError::new(
            "mcpPathInvalid",
            "Toolkit root is not set or the MCP override is empty.",
        )
    })
}

/// Sentinel surfaced in `McpConfigPlan.resolved_mcp_index` for the
/// npm-based launch modes. Keeps the field non-empty (so the UI's
/// "what did we resolve" line still reads cleanly) without
/// inventing a fake filesystem path.
const NPM_RESOLVED_LABEL: &str = "npm: unity-open-mcp@latest";

/// MCP server key used in the parent config. Matches
/// `MCP_SERVER_KEY` in `ai_toolkit.ts` and the spec
/// "MCP server name in config: `unity-open-mcp` (recommended)"
/// (`mcp-tools.md` §Naming).
pub const MCP_SERVER_KEY: &str = "unity-open-mcp";

/// Relative path of the skill client-paths manifest under the
/// toolkit root. The single source of truth for project-relative
/// skill install paths and the MCP-client → skill-target mapping.
/// Consumed by [`load_client_paths_manifest`] below and by the
/// mcp-server (`mcp-server/src/skill/client-paths.ts`).
pub const CLIENT_PATHS_MANIFEST_REL: &str = "skills/client-paths.json";

/// Supported MCP client ids. Mirrors `McpClientId` in
/// `ai_toolkit.ts` and the Step 4 picker catalog in
/// `hub/src/lib/components/wizard/constants.ts`
/// (`MCP_CLIENT_OPTIONS`). `claude-code` is CLI-only (renders a `claude mcp add`
/// command); `manual` is clipboard-only. Every other variant is
/// backed by a writable config file (JSON, or TOML for `Codex`).
///
/// The catalog covers the Ivan-named client surface (14+ agents)
/// plus the Open MCP originals. Adding a client is a three-step
/// change: extend this enum, add a row to every `match` below
/// (`client_format`, `resolve_target_path`, `merge_key_path`,
/// `build_entry_json`), and add the skill target to
/// `skills/client-paths.json`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum McpClientId {
    Cursor,
    ClaudeDesktop,
    ClaudeCode,
    OpencodeGlobal,
    OpencodeProject,
    ZcodeGlobal,
    ZcodeProject,
    Manual,
    // --- Ivan-parity breadth (M27 Plan 5) ---
    /// Cline — VS Code globalStorage JSON (`mcpServers`).
    Cline,
    /// Codex — project `.codex/config.toml` (`[mcp_servers.*]`).
    Codex,
    /// Gemini CLI — project `.gemini/settings.json`.
    Gemini,
    /// GitHub Copilot CLI — project `.mcp.json` (stdio, no `type`).
    GithubCopilotCli,
    /// Kilo Code — project `.kilocode/mcp.json`.
    KiloCode,
    /// Rider (Junie) — project `.junie/mcp/mcp.json`.
    Rider,
    /// Unity AI — project `UserSettings/mcp.json`.
    UnityAi,
    /// VS Code Copilot — project `.vscode/mcp.json` (`servers` key).
    VscodeCopilot,
    /// Visual Studio Copilot — project `.vs/mcp.json` (`servers` key).
    VsCopilot,
    /// ZooCode — project `.roo/mcp.json`.
    ZooCode,
    /// Antigravity — global `~/.gemini/antigravity/mcp_config.json`.
    Antigravity,
    /// Custom — clipboard-only snippet (alias of Manual semantics
    /// with a different UI affordance; maps to all skill folders).
    Custom,
}

/// Config format a client writes. Drives whether the planner / writer
/// takes the JSON merge path or the Codex TOML path. `CliOnly` and
/// `ClipboardOnly` never reach the file writer.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum ClientFormat {
    Json,
    Toml,
    CliOnly,
    ClipboardOnly,
}

/// The on-disk scope a client targets. Mirrors the old `McpScope` enum
/// but derived from `McpClientId` (+ the Cursor project toggle) so the
/// catalog stays the single switch. `Global` applies the
/// `UNITY_PROJECT_PATH` clear guard; `Project` clears unconditionally.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum ClientScope {
    Global,
    Project,
}

/// Resolve the format family for a client. The TOML branch is currently
/// Codex-only; every other file-backed client writes JSON.
pub(crate) fn client_format(client: McpClientId) -> ClientFormat {
    match client {
        McpClientId::ClaudeCode => ClientFormat::CliOnly,
        McpClientId::Manual | McpClientId::Custom => ClientFormat::ClipboardOnly,
        McpClientId::Codex => ClientFormat::Toml,
        _ => ClientFormat::Json,
    }
}

/// `true` when the client writes a file the user might share across
/// projects (global home-dir or OS-config-dir target). The clear pass
/// uses this to decide whether the `UNITY_PROJECT_PATH` guard applies.
pub(crate) fn client_is_global(client: McpClientId) -> bool {
    matches!(
        client,
        McpClientId::ClaudeDesktop
            | McpClientId::OpencodeGlobal
            | McpClientId::ZcodeGlobal
            | McpClientId::Cline
            | McpClientId::Antigravity
    )
}

/// How the MCP server should be launched. The wizard Step 2
/// picks between the bundled npm package (default, zero-clone
/// onboarding) and the local toolkit checkout (contributor
/// flow). `LocalOverride` is the Step 4 advanced escape hatch
/// for a custom `mcp-server/dist/index.js` path.
///
/// - `Npx` — `command: npx, args: ["-y", "unity-open-mcp@latest"]`.
///   Resolves the published package from the npm cache; no
///   toolkit root required.
/// - `Global` — `command: unity-open-mcp, args: []`. Assumes the
///   user ran `npm i -g unity-open-mcp`.
/// - `Local` — `command: node, args: [<toolkitRoot>/mcp-server/dist/index.js]`.
///   The M4 launch shape, used by the contributor checkout path.
/// - `LocalOverride` — same as `Local` but the index path is
///   taken from `mcp_index_override` verbatim (the Step 4
///   "Custom mcp-server/dist/index.js" field).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub enum McpLaunchMode {
    #[default]
    Npx,
    Global,
    Local,
    LocalOverride,
}

impl McpLaunchMode {
    /// `true` when the launch command needs a resolved
    /// `mcp-server/dist/index.js` path on disk. Only the two
    /// local modes validate the path; the npm-based modes
    /// point at the published binary and never touch disk.
    pub fn requires_local_index(self) -> bool {
        matches!(self, McpLaunchMode::Local | McpLaunchMode::LocalOverride)
    }
}

/// Inputs to the MCP config merge. Mirrors the Step 4 form
/// state: which client, where the MCP server lives, and the
/// env-var inputs the writer embeds. `cursor_project_scope`
/// is honored only when `client = Cursor`; for every other
/// client it is ignored.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpConfigParams {
    pub project_path: String,
    pub toolkit_root: String,
    /// Optional override for the MCP `index.js` path (Plan 1
    /// "Advanced override"). When empty, the planner derives
    /// `{toolkitRoot}/mcp-server/dist/index.js`. Only consulted
    /// when [`McpConfigParams::launch_mode`] is `LocalOverride`.
    #[serde(default)]
    pub mcp_index_override: String,
    /// Project path for the `UNITY_PROJECT_PATH` env var.
    pub unity_project_path: String,
    /// Bridge HTTP port for `UNITY_OPEN_MCP_BRIDGE_PORT`.
    #[serde(default)]
    pub bridge_port: String,
    /// `true` when the user opted in to batch routing; the
    /// writer only adds `UNITY_PATH` to the env block then.
    #[serde(default)]
    pub include_unity_path: bool,
    /// Optional explicit Unity Editor path. Only emitted when
    /// `include_unity_path = true`.
    #[serde(default)]
    pub unity_path: String,
    pub client: McpClientId,
    /// `true` to write Cursor's project-scoped config
    /// (`.cursor/mcp.json`); `false` for the global default
    /// (`~/.cursor/mcp.json`). Ignored for non-Cursor clients.
    #[serde(default)]
    pub cursor_project_scope: bool,
    /// How the MCP server is launched. Defaults to `Npx`
    /// (bundled npm package). The wizard Step 2 toggle picks
    /// `Local` for the local-checkout path; the Step 4 advanced
    /// override field promotes it to `LocalOverride`.
    #[serde(default)]
    pub launch_mode: McpLaunchMode,
}

/// The merge plan the Step 4 UI previews. Includes the
/// target file path, whether the file already exists, the
/// pre-merge value (when present), the post-merge value
/// (always), and the list of top-level keys the writer will
/// preserve verbatim.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpConfigPlan {
    /// The client id that produced this plan.
    pub client: McpClientId,
    /// Resolved absolute path of the config file the writer
    /// would touch. `null` for `claude-code` and `manual`
    /// (no file).
    pub target_path: Option<String>,
    /// `true` when the file exists on disk. `false` for
    /// CLI-only clients.
    pub file_exists: bool,
    /// `true` when the resolved MCP server entry would be a
    /// meaningful change (either the file is missing or the
    /// `unity-open-mcp` key is missing or differs from the
    /// proposed value). `false` when the file already has the
    /// exact entry the wizard would write — the writer still
    /// re-emits the file (no-op merge, no backup) so the plan
    /// is consistent with the run button's enabled state.
    pub would_write: bool,
    /// Top-level JSON keys that already exist in the on-disk
    /// file. The UI surfaces this so the user can see the
    /// wizard is leaving unrelated keys alone.
    pub preserved_keys: Vec<String>,
    /// The exact JSON the writer would emit, pretty-printed.
    /// `None` for `claude-code` (CLI command) and `manual`
    /// (clipboard-only); those clients populate
    /// [`McpConfigPlan::command`] instead.
    pub proposed_json: Option<String>,
    /// For `claude-code` only: the exact `claude mcp add`
    /// command the user should paste. `None` for every other
    /// client.
    pub command: Option<String>,
    /// The MCP index.js path the wizard resolved for this
    /// run. Surfaced for debugging / "what did I just write"
    /// UX.
    pub resolved_mcp_index: String,
}

/// What the writer actually did. Surfaces the backup path
/// (so Step 4 can show "Backup saved to <…>" before the
/// write, per `hub-wizard.md` Step 4 Safety row) plus the
/// post-merge file path and the final JSON the file now
/// contains.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpConfigWriteResult {
    pub client: McpClientId,
    pub target_path: String,
    /// Absolute path to the pre-write `.bak` file. Empty
    /// when no backup was created (no existing file or pure
    /// no-op write).
    pub backup_path: String,
    pub would_write: bool,
    pub proposed_json: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpConfigError {
    pub kind: String,
    pub message: String,
}

impl McpConfigError {
    fn new(kind: &str, message: impl Into<String>) -> Self {
        Self {
            kind: kind.to_string(),
            message: message.into(),
        }
    }
}

/// Tauri command: compute a merge plan for a Step 4 client
/// without writing anything. The wizard Step 4 calls this
/// whenever the form state (client, project-scope toggle,
/// override path, bridge port) changes so the diff is
/// always live.
#[tauri::command]
pub async fn plan_mcp_config(params: McpConfigParams) -> Result<McpConfigPlan, McpConfigError> {
    // Resolve home before spawning so the error path does not have to
    // cross the thread boundary (the pool task returns the inner result).
    let home = match paths::home_dir() {
        Some(h) => h,
        None => {
            return Err(McpConfigError::new(
                "homeMissing",
                "Cannot resolve the home directory for a global MCP config target.",
            ));
        }
    };
    tauri::async_runtime::spawn_blocking(move || plan_mcp_config_at(&params, &home))
        .await
        .map_err(|e| McpConfigError::new("planFailed", format!("task failed: {e}")))?
}

/// Tauri command: apply the merge. Refuses when:
/// - the resolved MCP index path does not exist or is not a
///   regular file (per `hub-wizard.md` Step 4 "MCP path
///   validation");
/// - the target file's parent directory is not writable;
/// - the client is `claude-code` or `manual` (no file is
///   written for those — Step 4 should have rendered the
///   CLI command or a snippet instead).
///
/// On success, leaves a `<target>.bak` next to the original
/// when one existed and the merge actually mutates the
/// `unity-open-mcp` entry.
#[tauri::command]
pub async fn write_mcp_config(
    params: McpConfigParams,
) -> Result<McpConfigWriteResult, McpConfigError> {
    let home = match paths::home_dir() {
        Some(h) => h,
        None => {
            return Err(McpConfigError::new(
                "homeMissing",
                "Cannot resolve the home directory for a global MCP config target.",
            ));
        }
    };
    tauri::async_runtime::spawn_blocking(move || write_mcp_config_at(&params, &home))
        .await
        .map_err(|e| McpConfigError::new("writeFailed", format!("task failed: {e}")))?
}

fn plan_mcp_config_at(params: &McpConfigParams, home: &Path) -> Result<McpConfigPlan, McpConfigError> {
    let scope = match resolve_scope(params) {
        Ok(s) => s,
        Err(McpScopeSkip::CliOnly) => {
            let resolved = resolve_index_for_mode(params)?;
            let resolved_str = resolved.as_deref().unwrap_or(NPM_RESOLVED_LABEL).to_string();
            return Ok(McpConfigPlan {
                client: params.client,
                target_path: None,
                file_exists: false,
                would_write: false,
                preserved_keys: Vec::new(),
                proposed_json: None,
                command: Some(command_for(params, &resolved_str, true)),
                resolved_mcp_index: resolved_str,
            });
        }
        Err(McpScopeSkip::Manual) => {
            let resolved = resolve_index_for_mode(params)?;
            let resolved_str = resolved.as_deref().unwrap_or(NPM_RESOLVED_LABEL).to_string();
            let json = build_full_config_json(
                params,
                &resolved_str,
                &manual_root_for(params),
                Value::Null,
            );
            let proposed_str =
                serde_json::to_string_pretty(&json).unwrap_or_else(|_| json.to_string());
            return Ok(McpConfigPlan {
                client: params.client,
                target_path: None,
                file_exists: false,
                would_write: true,
                preserved_keys: Vec::new(),
                proposed_json: Some(proposed_str),
                command: None,
                resolved_mcp_index: resolved_str,
            });
        }
    };
    let target = resolve_target_path(params.client, scope, &params.project_path, home)
        .ok_or_else(|| {
            McpConfigError::new(
                "homeMissing",
                "Cannot resolve the home directory for a global MCP config target.",
            )
        })?;
    let resolved = resolve_index_for_mode(params)?;
    let resolved_str = resolved.as_deref().unwrap_or(NPM_RESOLVED_LABEL).to_string();
    let file_exists = target.exists();
    // TOML clients skip JSON parsing (see write_mcp_config_at for the
    // rationale); the TOML builder reads the raw file itself.
    let (existing_value, existing_keys) = if file_exists {
        match client_format(params.client) {
            ClientFormat::Toml => (Value::Null, Vec::new()),
            _ => read_existing_config(&target)?,
        }
    } else {
        (Value::Null, Vec::new())
    };
    let merged = build_full_config_json(params, &resolved_str, &target, existing_value.clone());
    let proposed_str = match client_format(params.client) {
        ClientFormat::Toml => build_codex_toml(params, &resolved_str, &target, existing_value.clone()),
        _ => serde_json::to_string_pretty(&merged).unwrap_or_else(|_| merged.to_string()),
    };
    let would_write = if matches!(client_format(params.client), ClientFormat::Toml) {
        !file_exists
            || match fs::read_to_string(&target) {
                Ok(existing_text) => existing_text.trim_end() != proposed_str.trim_end(),
                Err(_) => true,
            }
    } else {
        !file_exists || merged_differs(&existing_value, &merged, params.client, scope)
    };
    Ok(McpConfigPlan {
        client: params.client,
        target_path: Some(target.to_string_lossy().into_owned()),
        file_exists,
        would_write,
        preserved_keys: existing_keys,
        proposed_json: Some(proposed_str),
        command: None,
        resolved_mcp_index: resolved_str,
    })
}

fn write_mcp_config_at(
    params: &McpConfigParams,
    home: &Path,
) -> Result<McpConfigWriteResult, McpConfigError> {
    let scope = match resolve_scope(params) {
        Ok(s) => s,
        Err(McpScopeSkip::CliOnly { .. }) | Err(McpScopeSkip::Manual) => {
            return Err(McpConfigError::new(
                "noFileTarget",
                format!(
                    "{:?} does not back a writable config file; the wizard renders a CLI command or a clipboard snippet instead.",
                    params.client
                ),
            ));
        }
    };
    let target = resolve_target_path(params.client, scope, &params.project_path, home)
        .ok_or_else(|| {
            McpConfigError::new(
                "homeMissing",
                "Cannot resolve the home directory for a global MCP config target.",
            )
        })?;
    let resolved = resolve_index_for_mode(params)?;
    let resolved_str = resolved.as_deref().unwrap_or(NPM_RESOLVED_LABEL).to_string();
    // Hard-block: refuse to write a config that points at a
    // non-existent MCP index. Only the local launch modes reference
    // an on-disk path; the npm modes (`Npx` / `Global`) point at the
    // published binary and skip this check. The Step 4 "Write config"
    // button is also disabled in the local case, but the writer
    // re-checks so a stale UI cannot sneak past the gate.
    if params.launch_mode.requires_local_index() {
        let path = resolved
            .as_deref()
            .ok_or_else(|| McpConfigError::new("mcpPathInvalid", "No local MCP index resolved."))?;
        if !Path::new(path).is_file() {
            return Err(McpConfigError::new(
                "mcpPathInvalid",
                format!(
                    "MCP server entry point does not exist on disk: {}. Run `npm run build` in the toolkit's mcp-server/ folder.",
                    path
                ),
            ));
        }
    }
    let file_exists = target.exists();
    // TOML clients (Codex) skip JSON parsing — the TOML builder reads
    // the raw file itself and parses via the `toml` crate. For TOML we
    // only need to know whether the file exists (for the backup + diff
    // decision); the merge body is computed from raw text.
    let (existing_value, _existing_keys) = if file_exists {
        match client_format(params.client) {
            ClientFormat::Toml => (Value::Null, Vec::new()),
            _ => read_existing_config(&target)?,
        }
    } else {
        (Value::Null, Vec::new())
    };
    let merged = build_full_config_json(params, &resolved_str, &target, existing_value.clone());
    let proposed_str = match client_format(params.client) {
        ClientFormat::Toml => build_codex_toml(params, &resolved_str, &target, existing_value.clone()),
        _ => serde_json::to_string_pretty(&merged).unwrap_or_else(|_| merged.to_string()),
    };
    // For TOML clients, the JSON projection (`merged`) does not reflect
    // the on-disk TOML; compare the proposed TOML body against the raw
    // existing text instead. A byte-level diff is conservative — any
    // change in our entry changes the serialized body.
    let would_write = if matches!(client_format(params.client), ClientFormat::Toml) {
        !file_exists
            || match fs::read_to_string(&target) {
                Ok(existing_text) => existing_text.trim_end() != proposed_str.trim_end(),
                Err(_) => true,
            }
    } else {
        !file_exists || merged_differs(&existing_value, &merged, params.client, scope)
    };
    let mut backup_path = String::new();
    if would_write && file_exists {
        let candidate = target.with_extension(
            match target.extension().and_then(|e| e.to_str()) {
                Some(ext) => format!("{}.bak", ext),
                None => "bak".to_string(),
            },
        );
        if let Err(e) = fs::copy(&target, &candidate) {
            return Err(McpConfigError::new(
                "backupFailed",
                format!("cannot create backup at {}: {}", candidate.display(), e),
            ));
        }
        backup_path = candidate.to_string_lossy().into_owned();
    }
    if would_write {
        if let Some(parent) = target.parent() {
            if !parent.exists() {
                if let Err(e) = fs::create_dir_all(parent) {
                    return Err(McpConfigError::new(
                        "writeFailed",
                        format!("cannot create parent folder for {}: {}", target.display(), e),
                    ));
                }
            }
        }
        // Atomic write: tmp + rename. A failure mid-write
        // leaves the original file untouched. The serialized body
        // is already in `proposed_str` (JSON pretty or Codex TOML).
        let pretty = match client_format(params.client) {
            // TOML body already carries its own trailing newline.
            ClientFormat::Toml => proposed_str.clone(),
            _ => proposed_str.clone() + "\n",
        };
        let tmp_path = match target.extension().and_then(|e| e.to_str()) {
            Some(ext) => target.with_extension(format!("{}.tmp", ext)),
            None => target.with_extension("tmp"),
        };
        {
            let mut tmp = fs::File::create(&tmp_path).map_err(|e| {
                McpConfigError::new("writeFailed", format!("cannot create tmp file: {}", e))
            })?;
            tmp.write_all(pretty.as_bytes()).map_err(|e| {
                McpConfigError::new("writeFailed", format!("cannot write tmp file: {}", e))
            })?;
            tmp.sync_all().ok();
        }
        fs::rename(&tmp_path, &target).map_err(|e| {
            McpConfigError::new("writeFailed", format!("cannot rename tmp to target: {}", e))
        })?;
    }
    Ok(McpConfigWriteResult {
        client: params.client,
        target_path: target.to_string_lossy().into_owned(),
        backup_path,
        would_write,
        proposed_json: proposed_str,
    })
}

enum McpScopeSkip {
    CliOnly,
    Manual,
}

/// Collapse `(client, cursor_project_scope)` into the on-disk scope.
/// Cursor is the only client whose project-vs-global choice is a UI
/// toggle; every other client has a fixed scope from the catalog
/// (`client_is_global` distinguishes the global-only clients from the
/// project-only clients).
fn resolve_scope(params: &McpConfigParams) -> Result<ClientScope, McpScopeSkip> {
    match client_format(params.client) {
        ClientFormat::CliOnly => Err(McpScopeSkip::CliOnly),
        ClientFormat::ClipboardOnly => Err(McpScopeSkip::Manual),
        ClientFormat::Json | ClientFormat::Toml => {
            if params.client == McpClientId::Cursor {
                // Cursor honors the project-scope toggle (default global).
                Ok(if params.cursor_project_scope {
                    ClientScope::Project
                } else {
                    ClientScope::Global
                })
            } else {
                Ok(if client_is_global(params.client) {
                    ClientScope::Global
                } else {
                    ClientScope::Project
                })
            }
        }
    }
}

/// Resolve the on-disk config target for a client. Returns `None` for
/// CLI/clipboard-only clients (the caller surfaces a snippet instead).
/// Project-scoped paths are resolved relative to `project_path`; global
/// paths are resolved under `home` (or the OS config dir for Claude
/// Desktop / Cline).
fn resolve_target_path(
    client: McpClientId,
    scope: ClientScope,
    project_path: &str,
    home: &Path,
) -> Option<PathBuf> {
    let project = PathBuf::from(project_path);
    match client {
        McpClientId::Cursor => match scope {
            ClientScope::Global => Some(home.join(".cursor").join("mcp.json")),
            ClientScope::Project => Some(project.join(".cursor").join("mcp.json")),
        },
        McpClientId::ClaudeDesktop => Some(claude_desktop_config_path(home)),
        McpClientId::OpencodeGlobal => {
            Some(home.join(".config").join("opencode").join("opencode.json"))
        }
        McpClientId::OpencodeProject => Some(project.join("opencode.json")),
        McpClientId::ZcodeGlobal => Some(home.join(".zcode").join("cli").join("config.json")),
        McpClientId::ZcodeProject => Some(project.join(".zcode").join("cli").join("config.json")),
        McpClientId::Cline => Some(cline_settings_path(home)),
        McpClientId::Codex => Some(project.join(".codex").join("config.toml")),
        McpClientId::Gemini => Some(project.join(".gemini").join("settings.json")),
        McpClientId::GithubCopilotCli => Some(project.join(".mcp.json")),
        McpClientId::KiloCode => Some(project.join(".kilocode").join("mcp.json")),
        McpClientId::Rider => Some(project.join(".junie").join("mcp").join("mcp.json")),
        McpClientId::UnityAi => Some(project.join("UserSettings").join("mcp.json")),
        McpClientId::VscodeCopilot => Some(project.join(".vscode").join("mcp.json")),
        McpClientId::VsCopilot => Some(project.join(".vs").join("mcp.json")),
        McpClientId::ZooCode => Some(project.join(".roo").join("mcp.json")),
        McpClientId::Antigravity => Some(
            home.join(".gemini")
                .join("antigravity")
                .join("mcp_config.json"),
        ),
        // CLI-only / clipboard-only clients have no file target.
        McpClientId::ClaudeCode | McpClientId::Manual | McpClientId::Custom => None,
    }
}

fn read_existing_config(path: &Path) -> Result<(Value, Vec<String>), McpConfigError> {
    let content = fs::read_to_string(path).map_err(|e| {
        McpConfigError::new(
            "readFailed",
            format!("cannot read existing config at {}: {}", path.display(), e),
        )
    })?;
    if content.trim().is_empty() {
        // Treat an empty file as a no-op root: the writer
        // overwrites with the merged value.
        return Ok((Value::Null, Vec::new()));
    }
    let value: Value = serde_json::from_str(&content).map_err(|e| {
        McpConfigError::new(
            "invalidJson",
            format!(
                "existing config at {} is not valid JSON: {}",
                path.display(),
                e
            ),
        )
    })?;
    let keys = value
        .as_object()
        .map(|m| m.keys().cloned().collect())
        .unwrap_or_default();
    Ok((value, keys))
}

fn build_full_config_json(
    params: &McpConfigParams,
    resolved_index: &str,
    target: &Path,
    existing: Value,
) -> Value {
    let entry = build_entry_json(params, resolved_index);
    let key_path = merge_key_path(params.client);
    let mut root_value: Value = match existing {
        Value::Object(_) | Value::Null => existing,
        _ => Value::Null,
    };
    if !root_value.is_object() {
        root_value = Value::Object(Map::new());
    }
    // Insert/overwrite only the key-path we own (e.g.
    // `mcpServers.unity-open-mcp` for Cursor, `mcp.servers.unity-open-mcp`
    // for ZCode). Every other top-level key, every other MCP server, and
    // every sibling under each path segment are preserved verbatim. Walk
    // down the path, ensuring each intermediate is an object (replacing a
    // non-object scalar so we never silently drop the user's own block),
    // then set the entry at the leaf.
    insert_by_path(&mut root_value, &key_path, entry);
    let mut root = match root_value {
        Value::Object(m) => m,
        _ => Map::new(),
    };
    // Ensure the target's eventual JSON serialization looks
    // sane even when there is no `$schema` field; OpenCode
    // tooling reads it back from the file we just wrote.
    if matches!(params.client, McpClientId::OpencodeGlobal | McpClientId::OpencodeProject)
        && !root.contains_key("$schema")
    {
        root.insert(
            "$schema".to_string(),
            Value::String("https://opencode.ai/config.json".to_string()),
        );
    }
    let _ = target; // path is consumed by the caller, not the JSON builder
    Value::Object(root)
}

/// The `(command, args)` pair the launch entry embeds, keyed off
/// the launch mode. `Local` / `LocalOverride` resolve to a real
/// `mcp-server/dist/index.js` path; `Npx` / `Global` resolve to
/// the published npm binary. The env block is the same across
/// every mode and is layered on by [`build_entry_json`].
///
/// `resolved_index` is the on-disk index path (already validated
/// by the caller for local modes). For npm modes it is passed
/// through unchanged so it can surface in `McpConfigPlan.resolved_mcp_index`
/// for debugging, but it is NOT used in the emitted command.
fn launch_command_parts(mode: McpLaunchMode, resolved_index: &str) -> (String, Vec<String>) {
    match mode {
        McpLaunchMode::Npx => (
            "npx".to_string(),
            vec!["-y".to_string(), NPM_PACKAGE_LATEST.to_string()],
        ),
        McpLaunchMode::Global => ("unity-open-mcp".to_string(), Vec::new()),
        McpLaunchMode::Local | McpLaunchMode::LocalOverride => (
            "node".to_string(),
            vec![resolved_index.to_string()],
        ),
    }
}

fn build_entry_json(params: &McpConfigParams, resolved_index: &str) -> Value {
    let env = build_env_map(params);
    let (command, args) = launch_command_parts(params.launch_mode, resolved_index);
    let args_value: Value = args.into_iter().map(Value::String).collect();
    match params.client {
        McpClientId::Cursor | McpClientId::ClaudeDesktop => json!({
            "command": command,
            "args": args_value,
            "env": Value::Object(env),
        }),
        McpClientId::OpencodeGlobal | McpClientId::OpencodeProject => {
            // OpenCode's `command` is an array (argv form); prepend the
            // resolved command so `npx -y unity-open-mcp@latest` becomes
            // `["npx", "-y", "unity-open-mcp@latest"]` and
            // `node /path/index.js` stays a two-element array.
            let mut cmd_array = vec![Value::String(command)];
            if let Value::Array(arr) = args_value {
                cmd_array.extend(arr);
            }
            json!({
                "type": "local",
                "command": Value::Array(cmd_array),
                "enabled": true,
                "environment": Value::Object(env),
            })
        }
        McpClientId::ZcodeGlobal | McpClientId::ZcodeProject => json!({
            "type": "stdio",
            "command": command,
            "args": args_value,
            "env": Value::Object(env),
        }),
        // The `type: "stdio"` family — Cline, Codex (when JSON-projected),
        // Gemini, Kilo Code, Rider, Unity AI, VS Code Copilot, VS Copilot,
        // ZooCode. Ivan's configurators emit `type:"stdio"` + `command` +
        // `args` + `env` for this family; some add `disabled:false` /
        // `enabled:true`, but those are optional defaults so we keep the
        // minimal shape.
        McpClientId::Cline
        | McpClientId::Gemini
        | McpClientId::KiloCode
        | McpClientId::Rider
        | McpClientId::UnityAi
        | McpClientId::VscodeCopilot
        | McpClientId::VsCopilot
        | McpClientId::ZooCode => json!({
            "type": "stdio",
            "command": command,
            "args": args_value,
            "env": Value::Object(env),
        }),
        // Codex's JSON projection (used for the preview when the UI
        // shows JSON; the on-disk write is TOML via build_codex_toml).
        // Ivan's Codex envelope: `enabled`, `command`, `args`, env under
        // `env`. The real file is TOML — see [`build_codex_toml`].
        McpClientId::Codex => json!({
            "enabled": true,
            "command": command,
            "args": args_value,
            "env": Value::Object(env),
        }),
        // Antigravity uses `disabled:false` + `command` + `args` + `env`,
        // no `type`.
        McpClientId::Antigravity => json!({
            "disabled": false,
            "command": command,
            "args": args_value,
            "env": Value::Object(env),
        }),
        // GitHub Copilot CLI: `command` + `args` + `tools:["*"]`, no `type`.
        McpClientId::GithubCopilotCli => json!({
            "command": command,
            "args": args_value,
            "tools": ["*"],
            "env": Value::Object(env),
        }),
        // CLI / clipboard clients never reach the file writer;
        // they only consume the JSON via the snippet panel.
        _ => json!({
            "command": command,
            "args": args_value,
            "env": Value::Object(env),
        }),
    }
}

/// Build the env-var map shared by every envelope. `UNITY_PROJECT_PATH`
/// and `UNITY_OPEN_MCP_BRIDGE_PORT` are always present; `UNITY_PATH` is
/// only emitted when the user opted in. Extracted so the Codex TOML
/// builder can layer the same env into `[mcp_servers.<name>.env]`.
fn build_env_map(params: &McpConfigParams) -> Map<String, Value> {
    let mut env = Map::new();
    env.insert(
        PROJECT_PATH_ENV_VAR.to_string(),
        Value::String(params.unity_project_path.clone()),
    );
    let port = resolve_port(&params.unity_project_path, parse_override(&params.bridge_port))
        .to_string();
    env.insert(PORT_ENV_VAR.to_string(), Value::String(port));
    if params.include_unity_path && !params.unity_path.trim().is_empty() {
        env.insert(
            UNITY_PATH_ENV_VAR.to_string(),
            Value::String(params.unity_path.trim().to_string()),
        );
    }
    env
}

/// Resolve the Cline global settings JSON path (VS Code globalStorage).
/// OS-specific per Ivan's `ClineConfigurator`:
/// - macOS: `~/Library/Application Support/Code/User/globalStorage/saoudrizwan.claude-dev/settings/cline_mcp_settings.json`
/// - Windows: `%APPDATA%\Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json`
/// - Linux: `~/.config/Code/User/globalStorage/saoudrizwan.claude-dev/settings/cline_mcp_settings.json`
pub(crate) fn cline_settings_path(home: &Path) -> PathBuf {
    let base = if cfg!(target_os = "macos") {
        home.join("Library")
            .join("Application Support")
            .join("Code")
    } else if cfg!(target_os = "windows") {
        dirs::config_dir()
            .unwrap_or_else(|| home.to_path_buf())
            .join("Code")
    } else {
        home.join(".config").join("Code")
    };
    base.join("User")
        .join("globalStorage")
        .join("saoudrizwan.claude-dev")
        .join("settings")
        .join("cline_mcp_settings.json")
}

/// Build the Codex TOML config body for a project `.codex/config.toml`.
/// Uses the `toml` crate to parse the existing file (when present) into
/// a `toml::Value`, merge our `[mcp_servers.unity-open-mcp]` table in,
/// and re-serialize — preserving every unrelated top-level key and every
/// sibling MCP server. This mirrors Ivan's `TomlAiAgentConfig` merge
/// semantics (replace only the named entry, keep the rest verbatim).
///
/// `existing_json` is the JSON projection the planner built from the
/// same file; for TOML clients we ignore it and re-parse the raw text
/// from disk so nested tables round-trip correctly.
///
/// The emitted table:
/// ```toml
/// [mcp_servers.unity-open-mcp]
/// enabled = true
/// command = "npx"
/// args = ["-y", "unity-open-mcp@latest"]
///
/// [mcp_servers.unity-open-mcp.env]
/// UNITY_PROJECT_PATH = "..."
/// UNITY_OPEN_MCP_BRIDGE_PORT = "..."
/// ```
fn build_codex_toml(
    params: &McpConfigParams,
    resolved_index: &str,
    target: &Path,
    _existing_json: Value,
) -> String {
    // Parse the existing TOML (if any) into a toml::Value so we can
    // merge without clobbering unrelated keys / servers.
    let mut root: toml::value::Table = if target.exists() {
        match fs::read_to_string(target) {
            Ok(text) if !text.trim().is_empty() => {
                toml::from_str::<toml::value::Table>(&text).unwrap_or_default()
            }
            _ => toml::value::Table::new(),
        }
    } else {
        toml::value::Table::new()
    };

    // Build the unity-open-mcp entry as a toml::Value.
    let env = build_env_map(params);
    let (command, args) = launch_command_parts(params.launch_mode, resolved_index);
    let mut entry = toml::value::Table::new();
    entry.insert("enabled".to_string(), toml::Value::Boolean(true));
    entry.insert("command".to_string(), toml::Value::String(command));
    let args_arr: toml::value::Array = args
        .into_iter()
        .map(toml::Value::String)
        .collect();
    entry.insert("args".to_string(), toml::Value::Array(args_arr));
    if !env.is_empty() {
        let mut env_table = toml::value::Table::new();
        for (k, v) in &env {
            if let Some(s) = v.as_str() {
                env_table.insert(k.clone(), toml::Value::String(s.to_string()));
            }
        }
        entry.insert("env".to_string(), toml::Value::Table(env_table));
    }

    // Insert/replace under [mcp_servers.<name>].
    let mcp_servers = root
        .entry("mcp_servers".to_string())
        .or_insert_with(|| toml::Value::Table(toml::value::Table::new()));
    if let toml::Value::Table(servers) = mcp_servers {
        servers.insert(MCP_SERVER_KEY.to_string(), toml::Value::Table(entry));
    }

    toml::to_string_pretty(&toml::Value::Table(root)).unwrap_or_default()
}

/// The dotted JSON path (relative to the config root) where this
/// client's `unity-open-mcp` entry lives. Returned as a key-path
/// vector so it generalizes to clients whose path nests deeper
/// than two levels — notably ZCode, whose envelope is
/// `mcp.servers.<name>` (three levels), versus Cursor/Claude
/// (`mcpServers.<name>`, two levels) and OpenCode
/// (`mcp.<name>`, two levels). Empty for CLI/clipboard-only clients.
pub(crate) fn merge_key_path(client: McpClientId) -> Vec<&'static str> {
    match client {
        McpClientId::Cursor
        | McpClientId::ClaudeDesktop
        | McpClientId::Cline
        | McpClientId::GithubCopilotCli
        | McpClientId::Antigravity
        | McpClientId::Gemini
        | McpClientId::KiloCode
        | McpClientId::Rider
        | McpClientId::UnityAi
        | McpClientId::ZooCode => vec!["mcpServers", MCP_SERVER_KEY],
        McpClientId::OpencodeGlobal | McpClientId::OpencodeProject => vec!["mcp", MCP_SERVER_KEY],
        McpClientId::ZcodeGlobal | McpClientId::ZcodeProject => {
            vec!["mcp", "servers", MCP_SERVER_KEY]
        }
        // VS Code Copilot and Visual Studio Copilot use a `servers` key
        // (not `mcpServers`) at the project root.
        McpClientId::VscodeCopilot | McpClientId::VsCopilot => vec!["servers", MCP_SERVER_KEY],
        // Codex writes TOML; the JSON path is only used for the diff
        // projection. The real table is `[mcp_servers.<name>]`.
        McpClientId::Codex => vec!["mcp_servers", MCP_SERVER_KEY],
        // CLI / clipboard-only clients have no merge key.
        _ => Vec::new(),
    }
}

/// Walk `root` along `path`, returning a reference to the leaf
/// value if every segment exists as an object key.
fn get_by_path<'a>(root: &'a Value, path: &[&str]) -> Option<&'a Value> {
    let mut current = root;
    for key in path {
        current = current.as_object()?.get(*key)?;
    }
    Some(current)
}

/// Insert `value` at `path` under `root`, creating an object at each
/// intermediate segment (and replacing a non-object scalar so the user's
/// own block is never silently dropped). A no-op when `path` is empty.
fn insert_by_path(root: &mut Value, path: &[&str], value: Value) {
    if path.is_empty() {
        return;
    }
    if !root.is_object() {
        *root = Value::Object(Map::new());
    }
    // The last segment is the leaf; the rest are intermediates we descend.
    let (segments, leaf) = path.split_at(path.len() - 1);
    let mut cursor = root;
    for segment in segments {
        let obj = cursor.as_object_mut().expect("ensured object above");
        let child = obj
            .entry((*segment).to_string())
            .or_insert_with(|| Value::Object(Map::new()));
        if !child.is_object() {
            *child = Value::Object(Map::new());
        }
        cursor = child;
    }
    if let Some(obj) = cursor.as_object_mut() {
        obj.insert(leaf[0].to_string(), value);
    }
}

/// `true` when the merged value would actually change the
/// file on disk for this client. We compare the parent key's
/// `unity-open-mcp` child only — unrelated keys and other MCP
/// servers are not part of the "did we change anything" test.
/// For TOML clients (Codex) we compare the JSON projection the
/// builder produced; the on-disk diff is TOML but the leaf
/// comparison is equivalent.
fn merged_differs(existing: &Value, merged: &Value, client: McpClientId, _scope: ClientScope) -> bool {
    let key_path = merge_key_path(client);
    if key_path.is_empty() {
        // No merge key → treat any existing content as "differs" so a
        // clipboard-only client's preview always re-renders.
        return true;
    }
    let before = get_by_path(existing, &key_path);
    let after = get_by_path(merged, &key_path);
    before != after
}

fn manual_root_for(params: &McpConfigParams) -> PathBuf {
    // The Manual / copy-JSON case is rendered as a snippet
    // without a backing file. We still build a "would-be"
    // parent object so the snippet matches what a real
    // Cursor / Claude Desktop merge would emit.
    PathBuf::from(&params.unity_project_path).join("manual-nonwritable")
}

/// Render the `claude mcp add` command for the Claude Code
/// client. Two env keys are required; the launch invocation is
/// keyed off the launch mode — `npx -y unity-open-mcp@latest`
/// for the bundled package, `unity-open-mcp` for a global
/// install, and `node <path>` for a local checkout.
pub fn claude_mcp_add_command(
    unity_project_path: &str,
    bridge_port: &str,
    launch_mode: McpLaunchMode,
    resolved_index: &str,
) -> String {
    let port = resolve_port(unity_project_path, parse_override(bridge_port)).to_string();
    let invocation = match launch_mode {
        McpLaunchMode::Npx => format!("npx -y {}", NPM_PACKAGE_LATEST),
        McpLaunchMode::Global => "unity-open-mcp".to_string(),
        McpLaunchMode::Local | McpLaunchMode::LocalOverride => {
            format!("node {}", resolved_index)
        }
    };
    format!(
        "claude mcp add {name} --env UNITY_PROJECT_PATH={project} --env UNITY_OPEN_MCP_BRIDGE_PORT={port} -- {invocation}",
        name = MCP_SERVER_KEY,
        project = unity_project_path,
        port = port,
        invocation = invocation,
    )
}

fn command_for(params: &McpConfigParams, resolved_index: &str, _is_cli_only: bool) -> String {
    match params.client {
        McpClientId::ClaudeCode => claude_mcp_add_command(
            &params.unity_project_path,
            &params.bridge_port,
            params.launch_mode,
            resolved_index,
        ),
        _ => String::new(),
    }
}

// --- Skill copy ----------------------------------------------------------
//
// Project-relative skill install paths and the MCP-client → skill-target
// mapping come from the single-source manifest at
// `<toolkitRoot>/skills/client-paths.json` (see [`ClientPathsManifest`]).
// Do not add per-client path constants here — edit the manifest.

/// In-memory mirror of `skills/client-paths.json`. The Hub and the
/// mcp-server (`unity_open_mcp_generate_skill`) resolve identical paths
/// from the same file, so a new client is added once in the manifest.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientPathsManifest {
    pub skill_id: String,
    pub template_relative_path: String,
    /// Map of client key → `{ relativePath }`. Keys are the canonical
    /// client identifiers also used by `unity_open_mcp_generate_skill`.
    pub clients: std::collections::BTreeMap<String, ClientPathEntry>,
    /// Map of `McpClientId`-equivalent wire key → client keys.
    pub mcp_client_mapping: std::collections::BTreeMap<String, Vec<String>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientPathEntry {
    pub relative_path: String,
}

/// Load the manifest from `<toolkitRoot>/skills/client-paths.json`.
/// Returns a typed error so the wizard can surface "manifest missing /
/// malformed" instead of silently falling back to stale constants.
pub fn load_client_paths_manifest(toolkit_root: &str) -> Result<ClientPathsManifest, SkillCopyError> {
    let root = PathBuf::from(toolkit_root);
    let path = root.join(CLIENT_PATHS_MANIFEST_REL);
    let body = fs::read_to_string(&path).map_err(|e| {
        SkillCopyError::new(
            "manifestMissing",
            format!(
                "cannot read skill client-paths manifest at {}: {}. The toolkit root should contain skills/client-paths.json.",
                path.display(),
                e
            ),
        )
    })?;
    let manifest: ClientPathsManifest = serde_json::from_str(&body).map_err(|e| {
        SkillCopyError::new(
            "manifestInvalid",
            format!("skills/client-paths.json is not valid: {}", e),
        )
    })?;
    Ok(manifest)
}

/// Wire key for an [`McpClientId`], matching the keys used in the
/// manifest's `mcpClientMapping` (kebab-case, matching `ai_toolkit.ts`).
fn mcp_client_wire_key(client: McpClientId) -> &'static str {
    match client {
        McpClientId::Cursor => "cursor",
        McpClientId::ClaudeDesktop => "claude-desktop",
        McpClientId::ClaudeCode => "claude-code",
        McpClientId::OpencodeGlobal => "opencode-global",
        McpClientId::OpencodeProject => "opencode-project",
        McpClientId::ZcodeGlobal => "zcode-global",
        McpClientId::ZcodeProject => "zcode-project",
        McpClientId::Manual => "manual",
        McpClientId::Cline => "cline",
        McpClientId::Codex => "codex",
        McpClientId::Gemini => "gemini",
        McpClientId::GithubCopilotCli => "github-copilot-cli",
        McpClientId::KiloCode => "kilo-code",
        McpClientId::Rider => "rider",
        McpClientId::UnityAi => "unity-ai",
        McpClientId::VscodeCopilot => "vscode-copilot",
        McpClientId::VsCopilot => "vs-copilot",
        McpClientId::ZooCode => "zoocode",
        McpClientId::Antigravity => "antigravity",
        McpClientId::Custom => "custom",
    }
}

/// One row in the skill-copy plan. The wizard renders this so the user
/// can see which files would be created / overwritten.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillCopyTarget {
    /// Manifest client key (e.g. `claude`, `opencode`, `agents`).
    /// Used by the UI for labels; the target path carries the location.
    pub kind: String,
    /// Absolute target path (the file the wizard will create
    /// or overwrite).
    pub target_path: String,
    /// Target path relative to the project root, for display
    /// (e.g. `.claude/skills/unity-open-mcp/SKILL.md`).
    pub relative_path: String,
    /// Absolute source path under the toolkit root. `null`
    /// when the source skill file is missing on disk.
    pub source_path: Option<String>,
    /// `true` when the target file already exists on disk.
    pub exists: bool,
    /// `true` when the target exists and its bytes match the source
    /// skill file byte-for-byte — i.e. copying would be a no-op. The
    /// wizard renders an "already up to date" tag in that case, mirroring
    /// the MCP config step. Always `false` when the target or source is
    /// missing.
    pub up_to_date: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillCopyPlan {
    pub project_path: String,
    pub toolkit_root: String,
    pub source_path: Option<String>,
    pub targets: Vec<SkillCopyTarget>,
}

/// Inputs to the skill-copy plan. Targets are derived from the
/// selected MCP client via the manifest's `mcpClientMapping` (single
/// source of truth) — not from ad-hoc booleans.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillCopyParams {
    pub project_path: String,
    pub toolkit_root: String,
    /// The MCP client selected in the wizard Step 4. Drives which
    /// skill targets are included via the manifest mapping.
    pub mcp_client: McpClientId,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillCopyResult {
    pub project_path: String,
    pub copied: Vec<SkillCopyTarget>,
    /// Targets that already existed and were **not** copied
    /// because the caller declined to overwrite. Surfaced on
    /// the wizard so the user can see which files were
    /// left untouched.
    pub skipped: Vec<SkillCopyTarget>,
    /// Targets the user explicitly asked to overwrite. Used
    /// by the UI to render a "Replaced N files" line.
    pub overwritten: Vec<SkillCopyTarget>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillCopyError {
    pub kind: String,
    pub message: String,
}

impl SkillCopyError {
    fn new(kind: &str, message: impl Into<String>) -> Self {
        Self {
            kind: kind.to_string(),
            message: message.into(),
        }
    }
}

/// Tauri command: compute the skill-copy plan without
/// touching any file. The wizard calls this to render
/// the per-target preview + "file exists" status.
#[tauri::command]
pub async fn plan_skill_copy(params: SkillCopyParams) -> Result<SkillCopyPlan, SkillCopyError> {
    tauri::async_runtime::spawn_blocking(move || plan_skill_copy_at(&params))
        .await
        .map_err(|e| SkillCopyError::new("planFailed", format!("task failed: {e}")))?
}

/// Tauri command: apply the skill copy. For each target in
/// the plan, the writer creates a `.bak` of the existing
/// file (when one is present) and then overwrites — but only
/// when `overwrite_existing` is `true` for that target. The
/// caller is expected to have prompted the user (wizard renders
/// an explicit confirmation) before passing
/// `overwrite_existing = true`.
#[tauri::command]
pub async fn copy_skill_files(
    params: SkillCopyParams,
    overwrite_existing: bool,
) -> Result<SkillCopyResult, SkillCopyError> {
    tauri::async_runtime::spawn_blocking(move || copy_skill_files_at(&params, overwrite_existing))
        .await
        .map_err(|e| SkillCopyError::new("copyFailed", format!("task failed: {e}")))?
}

fn plan_skill_copy_at(params: &SkillCopyParams) -> Result<SkillCopyPlan, SkillCopyError> {
    let project = PathBuf::from(&params.project_path);
    if !project.is_dir() {
        return Err(SkillCopyError::new(
            "notAUnityProject",
            "Project path is not a directory.",
        ));
    }
    let manifest = load_client_paths_manifest(&params.toolkit_root)?;
    let source_path = resolve_source_skill(&params.toolkit_root, &manifest);
    let source_path_str = source_path
        .as_ref()
        .map(|p| p.to_string_lossy().into_owned());

    // Derive client keys from the manifest's mcpClientMapping for the
    // selected MCP client. Unknown mappings yield an empty plan so the
    // wizard can show a clear "no skill targets for this client" state.
    let wire_key = mcp_client_wire_key(params.mcp_client);
    let client_keys: Vec<&str> = manifest
        .mcp_client_mapping
        .get(wire_key)
        .map(|v| v.iter().map(String::as_str).collect())
        .unwrap_or_default();

    let mut targets = Vec::new();
    let mut seen: std::collections::BTreeSet<String> = std::collections::BTreeSet::new();
    for key in client_keys {
        if !seen.insert(key.to_string()) {
            continue;
        }
        if let Some(entry) = manifest.clients.get(key) {
            targets.push(build_skill_target(
                key,
                &entry.relative_path,
                &project,
                source_path.as_deref(),
            ));
        }
    }

    Ok(SkillCopyPlan {
        project_path: params.project_path.clone(),
        toolkit_root: params.toolkit_root.clone(),
        source_path: source_path_str,
        targets,
    })
}

fn build_skill_target(
    kind: &str,
    relative: &str,
    project: &Path,
    source: Option<&Path>,
) -> SkillCopyTarget {
    let target_path = project.join(relative);
    let exists = target_path.exists();
    // "Up to date" mirrors the MCP config step: a copy would be a no-op
    // when the target exists and matches the source byte-for-byte. We
    // read both files here (cheap — the skill file is a single small
    // markdown doc) so the plan answers the question without a separate
    // command. Missing source or read errors leave it `false`.
    let up_to_date = match (exists, source) {
        (true, Some(src)) => matches_opt(src, &target_path).unwrap_or(false),
        _ => false,
    };
    SkillCopyTarget {
        kind: kind.to_string(),
        target_path: target_path.to_string_lossy().into_owned(),
        relative_path: relative.to_string(),
        source_path: source.map(|p| p.to_string_lossy().into_owned()),
        exists,
        up_to_date,
    }
}

/// Returns `true` when `a` and `b` both exist and have identical bytes.
/// Any IO error (missing file, permission) collapses to `None` so the
/// caller can treat it as "not up to date" rather than failing the plan.
fn matches_opt(a: &Path, b: &Path) -> Option<bool> {
    Some(std::fs::read(a).ok()?.eq(&std::fs::read(b).ok()?))
}

fn resolve_source_skill(
    toolkit_root: &str,
    manifest: &ClientPathsManifest,
) -> Option<PathBuf> {
    let root = PathBuf::from(toolkit_root);
    if !root.is_dir() {
        return None;
    }
    let candidate = root.join(&manifest.template_relative_path);
    if candidate.is_file() {
        Some(candidate)
    } else {
        None
    }
}

fn copy_skill_files_at(
    params: &SkillCopyParams,
    overwrite_existing: bool,
) -> Result<SkillCopyResult, SkillCopyError> {
    let plan = plan_skill_copy_at(params)?;
    let manifest = load_client_paths_manifest(&params.toolkit_root)?;
    let source = match plan.source_path.as_deref() {
        Some(s) => PathBuf::from(s),
        None => {
            return Err(SkillCopyError::new(
                "sourceMissing",
                format!(
                    "Toolkit source skill file not found at {}/{}. Run the wizard with a valid toolkit root.",
                    params.toolkit_root, manifest.template_relative_path
                ),
            ));
        }
    };
    let mut copied: Vec<SkillCopyTarget> = Vec::new();
    let mut skipped: Vec<SkillCopyTarget> = Vec::new();
    let mut overwritten: Vec<SkillCopyTarget> = Vec::new();
    for target in plan.targets {
        let target_path = PathBuf::from(&target.target_path);
        if let Some(parent) = target_path.parent() {
            if !parent.exists() {
                fs::create_dir_all(parent).map_err(|e| {
                    SkillCopyError::new(
                        "writeFailed",
                        format!("cannot create parent folder for {}: {}", target_path.display(), e),
                    )
                })?;
            }
        }
        if target.exists && !overwrite_existing {
            skipped.push(target);
            continue;
        }
        if target.exists {
            let backup = target_path.with_extension("md.bak");
            if let Err(e) = fs::copy(&target_path, &backup) {
                return Err(SkillCopyError::new(
                    "backupFailed",
                    format!(
                        "cannot create backup at {}: {}",
                        backup.display(),
                        e
                    ),
                ));
            }
        }
        if let Err(e) = fs::copy(&source, &target_path) {
            return Err(SkillCopyError::new(
                "writeFailed",
                format!(
                    "cannot copy skill to {}: {}",
                    target_path.display(),
                    e
                ),
            ));
        }
        let mut recorded = target.clone();
        recorded.exists = true;
        if target.exists {
            overwritten.push(recorded.clone());
        }
        copied.push(recorded);
    }
    Ok(SkillCopyResult {
        project_path: params.project_path.clone(),
        copied,
        skipped,
        overwritten,
    })
}

// --- Generate project skill ------------------------------------------------
//
// Surfaces `unity_open_mcp_generate_skill` from the wizard, alongside
// the template copy. Generate runs the local MCP server CLI
// (`node <toolkit>/mcp-server/dist/index.js run-tool
// unity_open_mcp_generate_skill`) with `write: true`, so it composes
// the template workflow playbook with this project's inventory
// (Unity version, installed packages, key types) into one SKILL.md
// per selected client. No live Unity bridge is required — the tool
// is server-routed and reads the project from disk.

/// Inputs to `generate_project_skill`. Mirrors `SkillCopyParams` plus
/// the Step 4 `mcp_index_override` escape hatch so the same MCP entry
/// resolution path is reused.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GenerateSkillParams {
    pub project_path: String,
    pub toolkit_root: String,
    #[serde(default)]
    pub mcp_index_override: String,
    pub mcp_client: McpClientId,
}

/// One client the generator wrote the skill into. Mirrors the
/// mcp-server `SkillWriteTarget` shape.
#[derive(Debug, Clone, Serialize)]
pub struct GenerateSkillTarget {
    pub client: String,
    pub relative_path: String,
    pub absolute_path: String,
    pub existed: bool,
}

/// Outcome of `generate_project_skill`. Parses the tool's JSON result
/// (`result.project` + `result.written`) and carries a bounded preview
/// of the generated skill so the wizard can show what was written.
#[derive(Debug, Clone, Serialize)]
pub struct GenerateSkillResult {
    pub project_path: String,
    pub unity_version: String,
    pub bridge_version: Option<String>,
    pub verify_version: Option<String>,
    pub targets: Vec<GenerateSkillTarget>,
    pub inventory_preview: String,
}

/// Error surface for `generate_project_skill`. Kinds match the
/// `SkillCopyError` convention (`{kind,message}`) so the wizard's
/// `describeSkillCopyError` maps cleanly.
#[derive(Debug, Clone, Serialize)]
pub struct GenerateSkillError {
    pub kind: String,
    pub message: String,
}

impl GenerateSkillError {
    fn new(kind: &str, message: impl Into<String>) -> Self {
        Self {
            kind: kind.to_string(),
            message: message.into(),
        }
    }
}

/// Cap on the inventory preview embedded in the JSON envelope. The
/// full skill is written to disk; this only bounds the in-response
/// echo.
const MAX_INVENTORY_PREVIEW_CHARS: usize = 6000;

/// Derive the manifest client keys for a chosen `McpClientId` (e.g.
/// `Cursor` → `["cursor"]`, `ZcodeProject` → `["agents"]`). Extracted
/// from `plan_skill_copy_at` so `generate_project_skill` reuses the
/// exact same mapping as the template-copy planner.
fn client_keys_for_mcp_client(
    manifest: &ClientPathsManifest,
    client: McpClientId,
) -> Vec<String> {
    let wire_key = mcp_client_wire_key(client);
    manifest
        .mcp_client_mapping
        .get(wire_key)
        .map(|v| {
            let mut seen = std::collections::BTreeSet::new();
            v.iter()
                .filter(|k| {
                    manifest.clients.contains_key(*k) && seen.insert(k.to_string())
                })
                .cloned()
                .collect()
        })
        .unwrap_or_default()
}

/// Build the `--args` JSON blob the CLI forwards to the tool. Exposed
/// for unit testing (the spawn itself has no Rust mock harness).
fn build_generate_skill_args_json(client_keys: &[String]) -> String {
    json!({
        "write": true,
        "clients": client_keys,
        "include_workflow": true,
    })
    .to_string()
}

/// Truncate a skill preview for the JSON envelope, cutting on a line
/// boundary so the tail note lands cleanly.
fn truncate_inventory_preview(skill: &str) -> String {
    if skill.len() <= MAX_INVENTORY_PREVIEW_CHARS {
        return skill.to_string();
    }
    let cut = &skill[..MAX_INVENTORY_PREVIEW_CHARS];
    let head = match cut.rfind('\n') {
        Some(i) if i > 0 => &skill[..i],
        _ => cut,
    };
    format!(
        "{}\n\n… ({} more chars; full content written to disk)",
        head,
        skill.len() - head.len()
    )
}

/// Tauri command: generate a project-specific skill file by invoking
/// the local MCP server CLI's `run-tool unity_open_mcp_generate_skill`
/// subcommand. No live Unity bridge is required. Mirrors the
/// validation + capture style of `write_mcp_config_at` /
/// `copy_skill_files_at`.
#[tauri::command]
pub async fn generate_project_skill(
    params: GenerateSkillParams,
) -> Result<GenerateSkillResult, GenerateSkillError> {
    tauri::async_runtime::spawn_blocking(move || generate_project_skill_at(&params))
        .await
        .map_err(|e| GenerateSkillError::new("spawnFailed", format!("task failed: {e}")))?
}

/// Sync inner implementation — see [`plan_manifest_merge_at`] in
/// `wizard.rs` for why the command body is split this way. Spawns
/// `node` to run the MCP server's `unity_open_mcp_generate_skill`
/// tool, polling `try_wait` against [`GENERATE_SKILL_TIMEOUT`] so a
/// wedged boot is killed and reported instead of blocking forever.
pub(crate) fn generate_project_skill_at(
    params: &GenerateSkillParams,
) -> Result<GenerateSkillResult, GenerateSkillError> {
    let project = PathBuf::from(&params.project_path);
    if !project.is_dir() {
        return Err(GenerateSkillError::new(
            "notAUnityProject",
            "Project path is not a directory.",
        ));
    }

    // Resolve + validate the MCP entry, mirroring write_mcp_config_at.
    let index_path = match resolve_mcp_index_path(&params.toolkit_root, &params.mcp_index_override)
    {
        Some(p) => PathBuf::from(p),
        None => {
            return Err(GenerateSkillError::new(
                "mcpPathInvalid",
                "Cannot resolve the MCP server entry path. Set a toolkit root or mcp index override.",
            ));
        }
    };
    if !index_path.is_file() {
        return Err(GenerateSkillError::new(
            "mcpPathInvalid",
            format!(
                "MCP server entry not found at {}. Run `npm run build` in the toolkit's mcp-server/ folder.",
                index_path.display()
            ),
        ));
    }

    // Derive client keys from the manifest (same path as plan_skill_copy_at).
    let manifest = load_client_paths_manifest(&params.toolkit_root).map_err(|e| {
        GenerateSkillError::new(
            "manifestInvalid",
            format!("skills/client-paths.json problem: {}", e.message),
        )
    })?;
    let client_keys = client_keys_for_mcp_client(&manifest, params.mcp_client);
    if client_keys.is_empty() {
        return Err(GenerateSkillError::new(
            "noClientTargets",
            format!(
                "No skill folder is mapped for the selected MCP client. Pick a different client or use Manual."
            ),
        ));
    }
    let args_json = build_generate_skill_args_json(&client_keys);

    // Spawn node with PATH enrichment (mirrors probe_node / run_project_sync_version).
    // The child is spawned with piped stdio and polled against a deadline
    // instead of using the blocking `Command::output()`, which would hang
    // forever if the MCP server boot is wedged. On timeout the child is
    // killed and reaped.
    let mut cmd = crate::config::command_runner::node_command();
    cmd.arg(&index_path)
        .args(["run-tool", "unity_open_mcp_generate_skill"])
        .arg("--args")
        .arg(&args_json)
        .arg("--json")
        .arg("--project")
        .arg(&params.project_path)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let mut child = cmd.spawn().map_err(|e| {
        GenerateSkillError::new(
            "spawnFailed",
            format!("failed to spawn node for skill generation: {}", e),
        )
    })?;

    let deadline = Instant::now() + GENERATE_SKILL_TIMEOUT;
    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break Some(status),
            Ok(None) => {
                if Instant::now() >= deadline {
                    let _ = child.kill();
                    let _ = child.wait();
                    break None;
                }
                std::thread::sleep(Duration::from_millis(100));
            }
            Err(e) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(GenerateSkillError::new(
                    "spawnFailed",
                    format!("node wait failed: {}", e),
                ));
            }
        }
    };

    // Drain stdout/stderr after the child has settled. The piped handles
    // cannot block now — the process is done or killed.
    let mut stdout = String::new();
    let mut stderr = String::new();
    if let Some(mut out) = child.stdout.take() {
        let _ = std::io::Read::read_to_string(&mut out, &mut stdout);
    }
    if let Some(mut err) = child.stderr.take() {
        let _ = std::io::Read::read_to_string(&mut err, &mut stderr);
    }

    let output = match status {
        None => {
            return Err(GenerateSkillError::new(
                "spawnFailed",
                format!(
                    "skill generation did not complete within {}s; check the MCP server boot or node install",
                    GENERATE_SKILL_TIMEOUT.as_secs()
                ),
            ));
        }
        Some(status) if !status.success() => {
            let mut msg = String::new();
            if !stderr.trim().is_empty() {
                msg.push_str(stderr.trim());
            }
            if !stdout.trim().is_empty() {
                if !msg.is_empty() {
                    msg.push(' ');
                }
                msg.push_str(stdout.trim());
            }
            if msg.is_empty() {
                msg = format!("node exited with status {}", status);
            }
            return Err(GenerateSkillError::new("cliError", msg));
        }
        Some(status) => {
            // success — fall through with the drained stdout. `status`
            // is bound here as `ExitStatus` (this arm only matches when
            // the earlier `!status.success()` guard did not).
            std::process::Output {
                status,
                stdout: stdout.into_bytes(),
                stderr: stderr.into_bytes(),
            }
        }
    };

    // `output` is only constructed in the success arm above (failure and
    // timeout already returned), so its stdout is the tool's JSON result.
    let stdout = String::from_utf8_lossy(&output.stdout);
    let parsed: Value = serde_json::from_str(stdout.trim()).map_err(|e| {
        GenerateSkillError::new(
            "cliError",
            format!("could not parse run-tool output as JSON: {}", e),
        )
    })?;

    if parsed.get("isError").and_then(Value::as_bool) == Some(true) {
        let detail = parsed
            .get("result")
            .and_then(|r| r.get("error"))
            .and_then(|e| e.get("message"))
            .and_then(Value::as_str)
            .unwrap_or("unknown tool error");
        return Err(GenerateSkillError::new("toolError", detail.to_string()));
    }

    let result = parsed.get("result").cloned().unwrap_or(Value::Null);
    let project_state = result.get("project").cloned().unwrap_or(Value::Null);
    let unity_version = project_state
        .get("unityVersion")
        .and_then(Value::as_str)
        .unwrap_or("unknown")
        .to_string();
    let bridge_version = project_state
        .get("bridgeVersion")
        .and_then(Value::as_str)
        .map(String::from);
    let verify_version = project_state
        .get("verifyVersion")
        .and_then(Value::as_str)
        .map(String::from);

    let mut targets = Vec::new();
    if let Some(written) = result.get("written").and_then(Value::as_array) {
        for entry in written {
            let client = entry
                .get("client")
                .and_then(Value::as_str)
                .unwrap_or("")
                .to_string();
            let relative_path = entry
                .get("relativePath")
                .and_then(Value::as_str)
                .unwrap_or("")
                .to_string();
            let existed = entry
                .get("existed")
                .and_then(Value::as_bool)
                .unwrap_or(false);
            let absolute_path = if relative_path.is_empty() {
                String::new()
            } else {
                project.join(&relative_path).to_string_lossy().into_owned()
            };
            targets.push(GenerateSkillTarget {
                client,
                relative_path,
                absolute_path,
                existed,
            });
        }
    }

    let inventory_preview = result
        .get("skill")
        .and_then(Value::as_str)
        .map(|s| truncate_inventory_preview(s))
        .unwrap_or_default();

    Ok(GenerateSkillResult {
        project_path: params.project_path.clone(),
        unity_version,
        bridge_version,
        verify_version,
        targets,
        inventory_preview,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::bridge_port::compute_port;
    use crate::config::wizard::contains_mcp_key;
    use std::fs;
    use tempfile::tempdir;

    fn write_text(path: &Path, body: &str) {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).unwrap();
        }
        fs::write(path, body).unwrap();
    }

    /// Make a fake toolkit root with a real `index.js` under
    /// `mcp-server/dist/` so the writer's `mcpPathInvalid`
    /// check passes. Also seeds a `skills/client-paths.json`
    /// manifest (single source of truth) and a template skill
    /// so the skill-copy planner has everything it needs.
    fn make_fake_toolkit(root: &Path) {
        let mcp_dir = root.join("mcp-server").join("dist");
        fs::create_dir_all(&mcp_dir).unwrap();
        fs::write(mcp_dir.join("index.js"), "module.exports = {};").unwrap();
        make_fake_skill_manifest(root);
    }

    /// Seed a `skills/client-paths.json` + template SKILL.md that
    /// mirror the checked-in manifest. Skill-copy tests build their
    /// own toolkit root, so they need a local manifest copy.
    fn make_fake_skill_manifest(root: &Path) {
        let skills_dir = root.join("skills").join("unity-open-mcp");
        fs::create_dir_all(&skills_dir).unwrap();
        fs::write(
            root.join(CLIENT_PATHS_MANIFEST_REL),
            r#"{
  "skillId": "unity-open-mcp",
  "templateRelativePath": "skills/unity-open-mcp/SKILL.md",
  "clients": {
    "cursor": { "relativePath": ".cursor/skills/unity-open-mcp/SKILL.md" },
    "claude": { "relativePath": ".claude/skills/unity-open-mcp/SKILL.md" },
    "opencode": { "relativePath": ".opencode/skills/unity-open-mcp/SKILL.md" },
    "agents": { "relativePath": ".agents/skills/unity-open-mcp/SKILL.md" }
  },
  "mcpClientMapping": {
    "cursor": ["cursor"],
    "claude-desktop": ["claude"],
    "claude-code": ["claude"],
    "opencode-global": ["opencode"],
    "opencode-project": ["opencode"],
    "zcode-global": ["agents"],
    "zcode-project": ["agents"],
    "manual": ["cursor", "claude", "opencode", "agents"]
  }
}
"#,
        )
        .unwrap();
    }

    /// Build skill-copy params for the given MCP client against a
    /// toolkit root. Uses the manifest-driven `mcp_client` field.
    fn make_skill_params(project: &Path, toolkit_root: &Path, client: McpClientId) -> SkillCopyParams {
        SkillCopyParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: toolkit_root.to_string_lossy().into_owned(),
            mcp_client: client,
        }
    }

    fn make_cursor_params(_home: &Path, project: &Path, toolkit_root: &Path) -> McpConfigParams {
        McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: toolkit_root.to_string_lossy().into_owned(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::Cursor,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Local,
        }
    }

    fn make_opencode_project_params(project: &Path, toolkit_root: &Path) -> McpConfigParams {
        McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: toolkit_root.to_string_lossy().into_owned(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::OpencodeProject,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Local,
        }
    }

    fn make_zcode_params(
        client: McpClientId,
        home: &Path,
        project: &Path,
        toolkit_root: &Path,
    ) -> McpConfigParams {
        McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: toolkit_root.to_string_lossy().into_owned(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Local,
        }
    }

    /// Generic params builder for any client (M27 Plan 5 envelopes).
    /// Uses npx launch mode so no toolkit root validation is required.
    fn make_client_params(client: McpClientId, project: &Path) -> McpConfigParams {
        McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: String::new(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Npx,
        }
    }

    #[test]
    fn plan_cursor_global_targets_home_dot_cursor() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_toolkit(toolkit.path());
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = make_cursor_params(home, &project, toolkit.path());
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("cursor has a target");
        assert!(target.ends_with(".cursor/mcp.json"));
        assert!(!plan.file_exists);
        assert!(plan.would_write);
        // The proposed JSON must use mcpServers.unity-open-mcp.
        let proposed = plan.proposed_json.unwrap();
        let parsed: Value = serde_json::from_str(&proposed).unwrap();
        let entry = parsed
            .get("mcpServers")
            .and_then(|m| m.get("unity-open-mcp"))
            .expect("mcpServers.unity-open-mcp present");
        assert_eq!(entry.get("command").unwrap(), "node");
        let expected_index = toolkit.path().join("mcp-server/dist/index.js");
        assert_eq!(
            entry.get("args").unwrap().as_array().unwrap()[0],
            Value::String(expected_index.to_string_lossy().into_owned())
        );
        assert_eq!(
            entry
                .get("env")
                .and_then(|e| e.get("UNITY_OPEN_MCP_BRIDGE_PORT"))
                .unwrap(),
            // Blank bridge_port → per-project hash, computed from the
            // fixture project path (cross-side formula in bridge_port.rs).
            &compute_port(&project.to_string_lossy()).to_string()
        );
    }

    #[test]
    fn plan_npx_mode_emits_npx_command_without_toolkit_root() {
        // Default onboarding path: no toolkit root required, launch via npx.
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: String::new(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::Cursor,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Npx,
        };
        // npx mode must NOT require a toolkit root: the planner resolves
        // even when toolkit_root is empty.
        let plan = plan_mcp_config_at(&params, home).expect("npx plan resolves without toolkit");
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        let entry = proposed
            .get("mcpServers")
            .and_then(|m| m.get("unity-open-mcp"))
            .expect("entry present");
        assert_eq!(entry.get("command").unwrap(), "npx");
        let args = entry.get("args").unwrap().as_array().unwrap();
        assert_eq!(args[0], "-y");
        assert_eq!(args[1], "unity-open-mcp@latest");
        // Sentinel label, not a filesystem path.
        assert_eq!(plan.resolved_mcp_index, NPM_RESOLVED_LABEL);
    }

    #[test]
    fn plan_global_mode_emits_bare_binary_command() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: String::new(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::OpencodeProject,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Global,
        };
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        let unity = proposed
            .get("mcp")
            .and_then(|m| m.get("unity-open-mcp"))
            .unwrap();
        let cmd = unity.get("command").unwrap().as_array().unwrap();
        // OpenCode prepends the binary to the args array.
        assert_eq!(cmd[0], "unity-open-mcp");
        assert_eq!(cmd.len(), 1, "global mode has no args");
    }

    #[test]
    fn write_npx_mode_skips_mcp_path_validation() {
        // The writer must NOT hard-block on a missing on-disk index for the
        // npx launch mode — there is no local path to validate.
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: String::new(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::Cursor,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Npx,
        };
        let result = write_mcp_config_at(&params, home).expect("npx write succeeds without toolkit");
        assert!(result.would_write, "npx entry is a fresh write");
    }

    #[test]
    fn plan_cursor_project_toggle_routes_to_project_dot_cursor() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_toolkit(toolkit.path());
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let mut params = make_cursor_params(home, &project, toolkit.path());
        params.cursor_project_scope = true;
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("cursor project has a target");
        assert!(target.contains("/proj/.cursor/mcp.json"));
    }

    #[test]
    fn plan_opencode_project_uses_mcp_key_and_preserves_unrelated_servers() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_toolkit(toolkit.path());
        fs::create_dir_all(project.join("Assets")).unwrap();
        let project_opencode = project.join("opencode.json");
        write_text(
            &project_opencode,
            r#"{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "other-server": {
      "type": "local",
      "command": ["node", "/somewhere/else.js"]
    }
  }
}
"#,
        );
        let params = make_opencode_project_params(project, toolkit.path());
        let plan = plan_mcp_config_at(&params, project).unwrap();
        assert!(plan.file_exists);
        assert!(plan.would_write);
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        let mcp = proposed.get("mcp").and_then(|m| m.as_object()).unwrap();
        // The unrelated server survived the merge.
        assert!(mcp.contains_key("other-server"));
        assert!(mcp.contains_key("unity-open-mcp"));
        let unity = mcp.get("unity-open-mcp").unwrap();
        assert_eq!(unity.get("type").unwrap(), "local");
        let cmd = unity.get("command").unwrap().as_array().unwrap();
        assert_eq!(cmd[0], "node");
        let expected_index = toolkit.path().join("mcp-server/dist/index.js");
        assert_eq!(cmd[1], expected_index.to_string_lossy().into_owned());
        assert_eq!(unity.get("enabled").unwrap(), true);
        // OpenCode uses `environment`, not `env`.
        assert!(unity.get("environment").is_some());
        assert!(unity.get("env").is_none());
    }

    #[test]
    fn write_cursor_global_creates_backup_and_merges_preserving_others() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_toolkit(toolkit.path());
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        // Pre-existing cursor config with an unrelated server.
        let cursor_path = home.join(".cursor").join("mcp.json");
        write_text(
            &cursor_path,
            r#"{
  "mcpServers": {
    "another-server": {
      "command": "node",
      "args": ["/elsewhere/x.js"]
    }
  }
}
"#,
        );
        let params = make_cursor_params(home, &project, toolkit.path());
        let result = write_mcp_config_at(&params, home).unwrap();
        assert!(!result.backup_path.is_empty());
        assert!(Path::new(&result.backup_path).exists());
        let written: Value =
            serde_json::from_str(&fs::read_to_string(&cursor_path).unwrap()).unwrap();
        let servers = written.get("mcpServers").unwrap().as_object().unwrap();
        assert!(servers.contains_key("another-server"));
        assert!(servers.contains_key("unity-open-mcp"));
    }

    #[test]
    fn write_cursor_global_noop_when_already_matches() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_toolkit(toolkit.path());
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let cursor_path = home.join(".cursor").join("mcp.json");
        // Seed the file with the exact value the writer would
        // produce (no advanced override, default port).
        let seed = McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: toolkit.path().to_string_lossy().into_owned(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::Cursor,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Local,
        };
        let plan = plan_mcp_config_at(&seed, home).unwrap();
        write_text(&cursor_path, &plan.proposed_json.clone().unwrap());
        let result = write_mcp_config_at(&seed, home).unwrap();
        assert!(!result.would_write, "should report no-op");
        assert!(result.backup_path.is_empty(), "no backup when no-op");
    }

    #[test]
    fn write_refuses_when_mcp_index_path_missing() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        let params = McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: "/repos/this/does/not/exist".to_string(),
            mcp_index_override: "/nope/index.js".to_string(),
            unity_project_path: dir.path().to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::Cursor,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Local,
        };
        let err = write_mcp_config_at(&params, project).unwrap_err();
        assert_eq!(err.kind, "mcpPathInvalid");
    }

    #[test]
    fn write_refuses_for_claude_code_no_file_target() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        let params = McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::ClaudeCode,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Local,
        };
        let err = write_mcp_config_at(&params, project).unwrap_err();
        assert_eq!(err.kind, "noFileTarget");
    }

    #[test]
    fn plan_claude_code_returns_command_not_file() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        fs::create_dir_all(project).unwrap();
        // The MCP index path must resolve to a real file or
        // the planner errors out with mcpPathInvalid. Create
        // a fake toolkit root with a fake index.js so the
        // validation passes for this test.
        let fake_root = tempdir().unwrap();
        let mcp_dir = fake_root.path().join("mcp-server").join("dist");
        fs::create_dir_all(&mcp_dir).unwrap();
        fs::write(mcp_dir.join("index.js"), "module.exports = {};").unwrap();
        let params = McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: fake_root.path().to_string_lossy().into_owned(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: String::new(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::ClaudeCode,
            cursor_project_scope: false,
            launch_mode: McpLaunchMode::Local,
        };
        let plan = plan_mcp_config_at(&params, project).unwrap();
        assert!(plan.target_path.is_none());
        assert!(plan.proposed_json.is_none());
        let cmd = plan.command.expect("claude-code renders a command");
        assert!(cmd.starts_with("claude mcp add unity-open-mcp"));
        assert!(cmd.contains("--env UNITY_PROJECT_PATH="));
        // Blank bridge_port → the per-project hash port for this fixture.
        let expected_port = compute_port(&project.to_string_lossy());
        assert!(cmd.contains(&format!(
            "--env UNITY_OPEN_MCP_BRIDGE_PORT={expected_port}"
        )));
        assert!(cmd.contains("-- node "));
        assert!(cmd.contains("index.js"));
    }

    #[test]
    fn claude_mcp_add_command_uses_computed_port_when_blank() {
        let cmd = claude_mcp_add_command(
            "/games/MyGame",
            "  ",
            McpLaunchMode::Local,
            "/u/mcp-server/dist/index.js",
        );
        // Blank → derived from the project path (cross-side formula).
        assert!(cmd.contains(&format!(
            "UNITY_OPEN_MCP_BRIDGE_PORT={}",
            compute_port("/games/MyGame")
        )));
    }

    #[test]
    fn claude_mcp_add_command_honors_explicit_override() {
        let cmd = claude_mcp_add_command(
            "/games/MyGame",
            "19199",
            McpLaunchMode::Local,
            "/u/mcp-server/dist/index.js",
        );
        // Explicit override wins over the per-project hash.
        assert!(cmd.contains("UNITY_OPEN_MCP_BRIDGE_PORT=19199"));
    }

    #[test]
    fn claude_mcp_add_command_npx_mode_omits_node_prefix() {
        let cmd = claude_mcp_add_command(
            "/games/MyGame",
            "19199",
            McpLaunchMode::Npx,
            "/unused/local/index.js",
        );
        assert!(cmd.contains("-- npx -y unity-open-mcp@latest"));
        assert!(!cmd.contains("-- node "));
    }

    #[test]
    fn claude_mcp_add_command_global_mode_uses_bare_binary() {
        let cmd = claude_mcp_add_command(
            "/games/MyGame",
            "19199",
            McpLaunchMode::Global,
            "/unused/local/index.js",
        );
        assert!(cmd.contains("-- unity-open-mcp"));
        assert!(!cmd.contains("npx"));
    }

    #[test]
    fn opencode_plan_emits_schema_field_when_missing() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_toolkit(toolkit.path());
        fs::create_dir_all(project).unwrap();
        // No existing opencode.json.
        let params = make_opencode_project_params(project, toolkit.path());
        let plan = plan_mcp_config_at(&params, project).unwrap();
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        assert_eq!(
            proposed.get("$schema").and_then(|v| v.as_str()),
            Some("https://opencode.ai/config.json")
        );
    }

    #[test]
    fn plan_skill_copy_for_cursor_maps_to_cursor_target_only() {
        // T1.5.6: Cursor selection must NOT push an unconditional
        // `.claude/skills/` target. It maps to `.cursor/skills/` only.
        let dir = tempdir().unwrap();
        let project = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_skill_manifest(toolkit.path());
        fs::create_dir_all(project).unwrap();
        let plan = plan_skill_copy_at(&make_skill_params(project, toolkit.path(), McpClientId::Cursor))
            .unwrap();
        assert_eq!(plan.targets.len(), 1);
        assert_eq!(plan.targets[0].kind, "cursor");
        assert!(plan.targets[0].target_path.ends_with(".cursor/skills/unity-open-mcp/SKILL.md"));
    }

    #[test]
    fn plan_skill_copy_for_claude_desktop_maps_to_claude_only() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_skill_manifest(toolkit.path());
        fs::create_dir_all(project).unwrap();
        let plan =
            plan_skill_copy_at(&make_skill_params(project, toolkit.path(), McpClientId::ClaudeDesktop))
                .unwrap();
        assert_eq!(plan.targets.len(), 1);
        assert_eq!(plan.targets[0].kind, "claude");
        assert!(plan.targets[0].target_path.ends_with(".claude/skills/unity-open-mcp/SKILL.md"));
    }

    #[test]
    fn plan_skill_copy_for_opencode_maps_to_opencode_only() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_skill_manifest(toolkit.path());
        fs::create_dir_all(project).unwrap();
        let plan = plan_skill_copy_at(&make_skill_params(
            project,
            toolkit.path(),
            McpClientId::OpencodeProject,
        ))
        .unwrap();
        assert_eq!(plan.targets.len(), 1);
        assert_eq!(plan.targets[0].kind, "opencode");
        assert!(plan.targets[0].target_path.ends_with(".opencode/skills/unity-open-mcp/SKILL.md"));
    }

    #[test]
    fn plan_skill_copy_for_zcode_maps_to_agents_only() {
        // T1.5.6: ZCode selection must push `.agents/skills/` and
        // never `.claude/skills/`.
        let dir = tempdir().unwrap();
        let project = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_skill_manifest(toolkit.path());
        fs::create_dir_all(project).unwrap();
        let plan =
            plan_skill_copy_at(&make_skill_params(project, toolkit.path(), McpClientId::ZcodeProject))
                .unwrap();
        assert_eq!(plan.targets.len(), 1);
        let agents = plan
            .targets
            .iter()
            .find(|t| t.kind == "agents")
            .expect("agents target present");
        assert!(agents.target_path.ends_with(".agents/skills/unity-open-mcp/SKILL.md"));
        // No unconditional Claude target leaks in for ZCode.
        assert!(!plan.targets.iter().any(|t| t.kind == "claude"));
    }

    #[test]
    fn plan_skill_copy_for_manual_includes_all_clients() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_skill_manifest(toolkit.path());
        fs::create_dir_all(project).unwrap();
        let plan =
            plan_skill_copy_at(&make_skill_params(project, toolkit.path(), McpClientId::Manual))
                .unwrap();
        let kinds: Vec<&str> = plan.targets.iter().map(|t| t.kind.as_str()).collect();
        assert!(kinds.contains(&"cursor"));
        assert!(kinds.contains(&"claude"));
        assert!(kinds.contains(&"opencode"));
        assert!(kinds.contains(&"agents"));
    }

    #[test]
    fn plan_skill_copy_errors_when_manifest_missing() {
        // A toolkit root without the manifest surfaces a clear error
        // instead of silently falling back to stale constants.
        let dir = tempdir().unwrap();
        let project = dir.path();
        let toolkit = tempdir().unwrap();
        // No make_fake_skill_manifest — manifest is absent.
        fs::create_dir_all(project).unwrap();
        let err = plan_skill_copy_at(&make_skill_params(project, toolkit.path(), McpClientId::Cursor))
            .unwrap_err();
        assert_eq!(err.kind, "manifestMissing");
    }

    #[test]
    fn copy_skill_files_creates_claude_target_and_skips_existing_without_overwrite() {
        let project_dir = tempdir().unwrap();
        let project = project_dir.path();
        let root = tempdir().unwrap();
        make_fake_skill_manifest(root.path());
        let skill = root.path().join("skills").join("unity-open-mcp").join("SKILL.md");
        write_text(&skill, "# unity-open-mcp\n\nHello.\n");
        // Pre-existing target file the user has customised.
        let existing = project.join(".claude/skills/unity-open-mcp/SKILL.md");
        write_text(&existing, "# user's custom notes\n");
        let result = copy_skill_files_at(
            &make_skill_params(project, root.path(), McpClientId::ClaudeDesktop),
            false,
        )
        .unwrap();
        assert_eq!(result.copied.len(), 0);
        assert_eq!(result.skipped.len(), 1);
        // Original content is preserved.
        assert_eq!(
            fs::read_to_string(&existing).unwrap(),
            "# user's custom notes\n"
        );
    }

    #[test]
    fn copy_skill_files_overwrites_with_backup_when_confirmed() {
        let project_dir = tempdir().unwrap();
        let project = project_dir.path();
        let root = tempdir().unwrap();
        make_fake_skill_manifest(root.path());
        let skill = root.path().join("skills").join("unity-open-mcp").join("SKILL.md");
        write_text(&skill, "# unity-open-mcp\n\nToolkit content.\n");
        let existing = project.join(".claude/skills/unity-open-mcp/SKILL.md");
        write_text(&existing, "# user's old notes\n");
        // Opencode selection drives two targets (none) — use manual to
        // exercise the multi-target overwrite path while keeping a
        // pre-existing Claude file.
        let result = copy_skill_files_at(
            &make_skill_params(project, root.path(), McpClientId::Manual),
            true,
        )
        .unwrap();
        // All four client targets copied; the pre-existing Claude one
        // was backed up and replaced.
        assert!(result.copied.len() >= 2);
        assert_eq!(result.overwritten.len(), 1);
        assert_eq!(result.skipped.len(), 0);
        let backup = project.join(".claude/skills/unity-open-mcp/SKILL.md.bak");
        assert!(backup.exists());
        assert_eq!(fs::read_to_string(&backup).unwrap(), "# user's old notes\n");
        // The Claude target now holds the toolkit content.
        let claude_target = project.join(".claude/skills/unity-open-mcp/SKILL.md");
        assert_eq!(
            fs::read_to_string(&claude_target).unwrap(),
            "# unity-open-mcp\n\nToolkit content.\n"
        );
    }

    #[test]
    fn copy_skill_files_errors_when_source_missing() {
        let project_dir = tempdir().unwrap();
        let project = project_dir.path();
        let toolkit = tempdir().unwrap();
        // Manifest present but no template SKILL.md on disk.
        make_fake_skill_manifest(toolkit.path());
        let result = copy_skill_files_at(
            &make_skill_params(project, toolkit.path(), McpClientId::ClaudeDesktop),
            true,
        )
        .unwrap_err();
        assert_eq!(result.kind, "sourceMissing");
    }

    #[test]
    fn mcp_path_check_uses_existing_heuristic() {
        // Sanity check: a real on-disk config file with a
        // `unity-open-mcp` entry is detected by the same
        // contains_mcp_key helper the heuristic uses, so the
        // Done screen's MCP status stays consistent after a
        // wizard write.
        let dir = tempdir().unwrap();
        let target = dir.path().join("mcp.json");
        write_text(
            &target,
            r#"{"mcpServers":{"unity-open-mcp":{"command":"node","args":["/x"]}}}"#,
        );
        assert!(contains_mcp_key(&target));
    }

    #[test]
    fn plan_zcode_global_targets_home_dot_zcode() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_toolkit(toolkit.path());
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = make_zcode_params(McpClientId::ZcodeGlobal, home, &project, toolkit.path());
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("zcode global has a target");
        assert!(target.ends_with(".zcode/cli/config.json"));
        // ZCode nests three levels: mcp.servers.unity-open-mcp.
        let proposed: Value = serde_json::from_str(&plan.proposed_json.clone().unwrap()).unwrap();
        let entry = proposed
            .get("mcp")
            .and_then(|m| m.get("servers"))
            .and_then(|s| s.get(MCP_SERVER_KEY))
            .expect("mcp.servers.unity-open-mcp present");
        assert_eq!(entry.get("type").unwrap(), "stdio");
        assert_eq!(entry.get("command").unwrap(), "node");
        assert!(entry.get("env").is_some());
    }

    #[test]
    fn plan_zcode_project_targets_project_dot_zcode() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_toolkit(toolkit.path());
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = make_zcode_params(McpClientId::ZcodeProject, home, &project, toolkit.path());
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("zcode project has a target");
        // Project scope: file under <project>/.zcode/cli/config.json, NOT ~/.zcode.
        assert!(target.contains("proj/.zcode/cli/config.json"));
        // Project scope writes under the project dir, not the home dir.
        let proj_zcode = project.join(".zcode").join("cli").join("config.json");
        assert_eq!(
            std::path::Path::new(&target),
            proj_zcode.as_path()
        );
    }

    #[test]
    fn plan_zcode_preserves_unrelated_servers_and_three_level_nesting() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_toolkit(toolkit.path());
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        // Pre-existing ZCode config with an unrelated server and a sibling key.
        let zcode_path = home.join(".zcode").join("cli").join("config.json");
        write_text(
            &zcode_path,
            r#"{
  "mcp": {
    "servers": {
      "another-server": {
        "type": "http",
        "url": "https://example.com/mcp"
      }
    }
  },
  "ui": { "theme": "dark" }
}
"#,
        );
        let params = make_zcode_params(McpClientId::ZcodeGlobal, home, &project, toolkit.path());
        let plan = plan_mcp_config_at(&params, home).unwrap();
        assert!(plan.file_exists);
        assert!(plan.would_write);
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        let servers = proposed
            .get("mcp")
            .and_then(|m| m.get("servers"))
            .and_then(|s| s.as_object())
            .unwrap();
        // The unrelated server survived the three-level merge.
        assert!(servers.contains_key("another-server"));
        assert!(servers.contains_key(MCP_SERVER_KEY));
        // An unrelated top-level key is preserved too.
        assert_eq!(
            proposed.get("ui").and_then(|u| u.get("theme")).and_then(|t| t.as_str()),
            Some("dark")
        );
        // ZCode does NOT inject the OpenCode $schema.
        assert!(proposed.get("$schema").is_none());
    }

    #[test]
    fn write_zcode_global_merges_into_mcp_servers_key() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let toolkit = tempdir().unwrap();
        make_fake_toolkit(toolkit.path());
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let zcode_path = home.join(".zcode").join("cli").join("config.json");
        write_text(&zcode_path, r#"{"mcp":{"servers":{"existing":{"type":"stdio","command":"x"}}}}"#);
        let params = make_zcode_params(McpClientId::ZcodeGlobal, home, &project, toolkit.path());
        let result = write_mcp_config_at(&params, home).unwrap();
        assert!(result.would_write);
        let written: Value =
            serde_json::from_str(&fs::read_to_string(&zcode_path).unwrap()).unwrap();
        let servers = written.get("mcp").unwrap().get("servers").unwrap().as_object().unwrap();
        assert!(servers.contains_key("existing"));
        assert!(servers.contains_key(MCP_SERVER_KEY));
    }

    #[test]
    fn load_client_paths_manifest_parses_checked_in_shape() {
        // The manifest shipped at the repo root parses cleanly into
        // the typed struct and exposes the four canonical clients.
        let repo_root = env!("CARGO_MANIFEST_DIR");
        // hub/ is two levels under the repo root.
        let manifest_root = Path::new(repo_root)
            .parent()
            .and_then(|p| p.parent())
            .expect("repo root");
        let manifest =
            load_client_paths_manifest(&manifest_root.to_string_lossy()).unwrap_or_else(|e| {
                panic!("checked-in skills/client-paths.json should parse: {:?}", e)
            });
        assert_eq!(manifest.skill_id, "unity-open-mcp");
        assert_eq!(
            manifest.template_relative_path,
            "skills/unity-open-mcp/SKILL.md"
        );
        for key in ["cursor", "claude", "opencode", "agents"] {
            assert!(manifest.clients.contains_key(key), "missing client {}", key);
        }
        // ZCode maps to `.agents/skills/`, never `.claude/skills/`.
        assert_eq!(manifest.mcp_client_mapping["zcode-global"], vec!["agents"]);
        assert_eq!(manifest.mcp_client_mapping["zcode-project"], vec!["agents"]);
        // Cursor maps to `.cursor/skills/`, never Claude.
        assert_eq!(manifest.mcp_client_mapping["cursor"], vec!["cursor"]);
    }

    // --- generate_project_skill helpers -----------------------------------

    #[test]
    fn client_keys_for_mcp_client_maps_cursor_and_zcode() {
        let repo_root = env!("CARGO_MANIFEST_DIR");
        let manifest_root = Path::new(repo_root)
            .parent()
            .and_then(|p| p.parent())
            .expect("repo root");
        let manifest =
            load_client_paths_manifest(&manifest_root.to_string_lossy()).expect("manifest");

        // Cursor → ["cursor"], ZCode (either scope) → ["agents"].
        assert_eq!(
            client_keys_for_mcp_client(&manifest, McpClientId::Cursor),
            vec!["cursor"]
        );
        assert_eq!(
            client_keys_for_mcp_client(&manifest, McpClientId::ZcodeProject),
            vec!["agents"]
        );
        assert_eq!(
            client_keys_for_mcp_client(&manifest, McpClientId::ZcodeGlobal),
            vec!["agents"]
        );
        // Manual → all four, deduped + ordered as in the manifest.
        let manual = client_keys_for_mcp_client(&manifest, McpClientId::Manual);
        assert_eq!(manual.len(), 4);
        assert!(manual.contains(&"cursor".to_string()));
        assert!(manual.contains(&"agents".to_string()));
    }

    #[test]
    fn build_generate_skill_args_json_carries_write_clients_workflow() {
        let raw = build_generate_skill_args_json(&["claude".to_string()]);
        let v: Value = serde_json::from_str(&raw).expect("args json parses");
        assert_eq!(v["write"], json!(true));
        assert_eq!(v["include_workflow"], json!(true));
        assert_eq!(v["clients"], json!(["claude"]));
    }

    #[test]
    fn truncate_inventory_preview_keeps_short_strings_verbatim() {
        let s = "short preview\n";
        assert_eq!(truncate_inventory_preview(s), s);
    }

    #[test]
    fn truncate_inventory_preview_cuts_on_line_boundary_with_tail_note() {
        // Build a string well past the cap with newline separators so the
        // line-boundary cut is exercised.
        let chunk = "line of inventory text\n".repeat(400);
        let out = truncate_inventory_preview(&chunk);
        assert!(out.len() < chunk.len() + 200);
        assert!(out.contains("more chars; full content written to disk"));
        // The cut lands on a newline (the head ends with a newline, then
        // the appended blank-line + note).
        assert!(out.ends_with("disk)"));
    }

    #[test]
    fn generate_project_skill_errors_when_project_path_not_a_dir() {
        let dir = tempdir().unwrap();
        let toolkit = dir.path().join("toolkit");
        make_fake_toolkit(&toolkit);
        let params = GenerateSkillParams {
            project_path: dir.path().join("does-not-exist").to_string_lossy().into_owned(),
            toolkit_root: toolkit.to_string_lossy().into_owned(),
            mcp_index_override: String::new(),
            mcp_client: McpClientId::Cursor,
        };
        let err = generate_project_skill_at(&params).expect_err("should error");
        assert_eq!(err.kind, "notAUnityProject");
    }

    #[test]
    fn generate_project_skill_errors_when_mcp_entry_missing() {
        let dir = tempdir().unwrap();
        // Toolkit root exists but has NO mcp-server/dist/index.js.
        let toolkit = dir.path().join("toolkit");
        make_fake_skill_manifest(&toolkit);
        let project = dir.path().join("project");
        fs::create_dir_all(&project).unwrap();
        let params = GenerateSkillParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: toolkit.to_string_lossy().into_owned(),
            mcp_index_override: String::new(),
            mcp_client: McpClientId::Cursor,
        };
        let err = generate_project_skill_at(&params).expect_err("should error");
        assert_eq!(err.kind, "mcpPathInvalid");
    }

    #[test]
    fn generate_project_skill_errors_when_no_client_targets_mapped() {
        // The checked-in manifest maps every McpClientId variant, so
        // build a synthetic manifest missing the cursor mapping to
        // exercise the empty-targets guard.
        let dir = tempdir().unwrap();
        let toolkit = dir.path().join("toolkit");
        let mcp_dir = toolkit.join("mcp-server").join("dist");
        fs::create_dir_all(&mcp_dir).unwrap();
        fs::write(mcp_dir.join("index.js"), "module.exports = {};").unwrap();
        let skills_dir = toolkit.join("skills").join("unity-open-mcp");
        fs::create_dir_all(&skills_dir).unwrap();
        fs::write(
            toolkit.join(CLIENT_PATHS_MANIFEST_REL),
            r#"{
  "skillId": "unity-open-mcp",
  "templateRelativePath": "skills/unity-open-mcp/SKILL.md",
  "clients": {
    "cursor": { "relativePath": ".cursor/skills/unity-open-mcp/SKILL.md" }
  },
  "mcpClientMapping": {
    "zcode-global": ["agents"]
  }
}
"#,
        )
        .unwrap();
        let project = dir.path().join("project");
        fs::create_dir_all(&project).unwrap();
        let params = GenerateSkillParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: toolkit.to_string_lossy().into_owned(),
            mcp_index_override: String::new(),
            mcp_client: McpClientId::Cursor,
        };
        let err = generate_project_skill_at(&params).expect_err("should error");
        assert_eq!(err.kind, "noClientTargets");
    }

    // --- M27 Plan 5: new client envelopes -----------------------------------

    #[test]
    fn plan_codex_targets_project_codex_config_toml() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = make_client_params(McpClientId::Codex, &project);
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("codex has a target");
        assert!(target.ends_with(".codex/config.toml"));
        // The proposed body is TOML, not JSON.
        let body = plan.proposed_json.as_deref().unwrap_or("");
        assert!(body.contains("[mcp_servers.unity-open-mcp]"));
        assert!(body.contains("enabled = true"));
        assert!(body.contains("command = \"npx\""));
        assert!(body.contains("UNITY_PROJECT_PATH"));
    }

    #[test]
    fn write_codex_toml_merges_preserving_unrelated_servers() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        fs::create_dir_all(project).unwrap();
        let codex_dir = project.join(".codex");
        fs::create_dir_all(&codex_dir).unwrap();
        // Pre-existing codex config with a sibling server + a top-level key.
        fs::write(
            codex_dir.join("config.toml"),
            "model = \"gpt-5\"\n\n[mcp_servers.other]\ncommand = \"foo\"\n",
        )
        .unwrap();
        let params = make_client_params(McpClientId::Codex, project);
        let result = write_mcp_config_at(&params, project).unwrap();
        assert!(result.would_write);
        let body = fs::read_to_string(codex_dir.join("config.toml")).unwrap();
        // The unrelated server survived.
        assert!(body.contains("[mcp_servers.other]"));
        // Our entry was added.
        assert!(body.contains("[mcp_servers.unity-open-mcp]"));
        // The top-level scalar key survived.
        assert!(body.contains("model = \"gpt-5\""));
    }

    #[test]
    fn plan_cline_targets_vscode_globalstorage() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = make_client_params(McpClientId::Cline, &project);
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("cline has a target");
        assert!(target.contains("globalStorage/saoudrizwan.claude-dev/settings/cline_mcp_settings.json"));
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        let entry = proposed
            .get("mcpServers")
            .and_then(|m| m.get(MCP_SERVER_KEY))
            .expect("mcpServers.unity-open-mcp present");
        assert_eq!(entry.get("type").unwrap(), "stdio");
        assert_eq!(entry.get("command").unwrap(), "npx");
    }

    #[test]
    fn plan_gemini_targets_project_gemini_settings() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = make_client_params(McpClientId::Gemini, &project);
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("gemini has a target");
        assert!(target.ends_with(".gemini/settings.json"));
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        assert!(proposed
            .get("mcpServers")
            .and_then(|m| m.get(MCP_SERVER_KEY))
            .is_some());
    }

    #[test]
    fn plan_vscode_copilot_uses_servers_key() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = make_client_params(McpClientId::VscodeCopilot, &project);
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("vscode-copilot has a target");
        assert!(target.ends_with(".vscode/mcp.json"));
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        // VS Code Copilot uses `servers`, NOT `mcpServers`.
        assert!(proposed.get("servers").and_then(|m| m.get(MCP_SERVER_KEY)).is_some());
        assert!(proposed.get("mcpServers").is_none());
    }

    #[test]
    fn plan_vs_copilot_uses_servers_key_under_vs_dir() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = make_client_params(McpClientId::VsCopilot, &project);
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("vs-copilot has a target");
        assert!(target.ends_with(".vs/mcp.json"));
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        assert!(proposed.get("servers").and_then(|m| m.get(MCP_SERVER_KEY)).is_some());
    }

    #[test]
    fn plan_github_copilot_cli_emits_tools_wildcard() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = make_client_params(McpClientId::GithubCopilotCli, &project);
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("github-copilot-cli has a target");
        assert!(target.ends_with(".mcp.json"));
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        let entry = proposed
            .get("mcpServers")
            .and_then(|m| m.get(MCP_SERVER_KEY))
            .unwrap();
        // GitHub Copilot CLI carries a `tools:["*"]` hint, no `type`.
        assert_eq!(entry.get("tools").unwrap(), &json!(["*"]));
        assert!(entry.get("type").is_none());
    }

    #[test]
    fn plan_antigravity_targets_global_gemini_antigravity() {
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let params = make_client_params(McpClientId::Antigravity, &project);
        let plan = plan_mcp_config_at(&params, home).unwrap();
        let target = plan.target_path.expect("antigravity has a target");
        assert!(target.contains(".gemini/antigravity/mcp_config.json"));
        let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
        let entry = proposed
            .get("mcpServers")
            .and_then(|m| m.get(MCP_SERVER_KEY))
            .unwrap();
        // Antigravity uses `disabled:false`, no `type`.
        assert_eq!(entry.get("disabled").unwrap(), false);
        assert!(entry.get("type").is_none());
    }

    #[test]
    fn plan_kilocode_rider_unityai_zoocode_all_use_mcp_servers() {
        // Shared `mcpServers` + `type:stdio` envelope for the Tier B
        // stdio family — one assertion per client proves the merge key
        // and envelope are consistent across the catalog.
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        for client in [
            McpClientId::KiloCode,
            McpClientId::Rider,
            McpClientId::UnityAi,
            McpClientId::ZooCode,
        ] {
            let params = make_client_params(client, &project);
            let plan = plan_mcp_config_at(&params, home).unwrap();
            let proposed: Value = serde_json::from_str(&plan.proposed_json.unwrap()).unwrap();
            let entry = proposed
                .get("mcpServers")
                .and_then(|m| m.get(MCP_SERVER_KEY))
                .unwrap_or_else(|| panic!("{:?}: mcpServers.unity-open-mcp missing", client));
            assert_eq!(
                entry.get("type").unwrap(),
                "stdio",
                "{:?}: type field",
                client
            );
            assert!(entry.get("command").is_some(), "{:?}: command missing", client);
            assert!(entry.get("env").is_some(), "{:?}: env missing", client);
        }
    }

    #[test]
    fn write_vscode_copilot_merges_under_servers_key() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        fs::create_dir_all(project).unwrap();
        let vscode_dir = project.join(".vscode");
        fs::create_dir_all(&vscode_dir).unwrap();
        // Pre-existing VS Code mcp.json with an unrelated server under `servers`.
        fs::write(
            vscode_dir.join("mcp.json"),
            r#"{"servers":{"other":{"type":"stdio","command":"x"}}}"#,
        )
        .unwrap();
        let params = make_client_params(McpClientId::VscodeCopilot, project);
        let result = write_mcp_config_at(&params, project).unwrap();
        assert!(result.would_write);
        let written: Value =
            serde_json::from_str(&fs::read_to_string(vscode_dir.join("mcp.json")).unwrap()).unwrap();
        let servers = written.get("servers").unwrap().as_object().unwrap();
        // Unrelated server preserved + our entry added.
        assert!(servers.contains_key("other"));
        assert!(servers.contains_key(MCP_SERVER_KEY));
    }

    #[test]
    fn clear_removes_codex_toml_entry() {
        use crate::config::clear::clear_ai_setup_at;
        let dir = tempdir().unwrap();
        let home = dir.path();
        let project = home.join("proj");
        fs::create_dir_all(&project).unwrap();
        let codex_dir = project.join(".codex");
        fs::create_dir_all(&codex_dir).unwrap();
        // Seed our entry + an unrelated server.
        fs::write(
            codex_dir.join("config.toml"),
            "[mcp_servers.unity-open-mcp]\ncommand = \"npx\"\n\n[mcp_servers.other]\ncommand = \"foo\"\n",
        )
        .unwrap();
        let result = clear_ai_setup_at(project.to_str().unwrap(), home);
        let body = fs::read_to_string(codex_dir.join("config.toml")).unwrap();
        // Our entry removed; the unrelated server survived.
        assert!(!body.contains("unity-open-mcp"));
        assert!(body.contains("[mcp_servers.other]"));
        assert!(result.errors.is_empty(), "{:?}", result.errors);
    }

    #[test]
    fn mcp_client_wire_key_covers_every_variant() {
        // Every variant must resolve to a non-empty wire key so the
        // skill-copy manifest lookup never silently misses a client.
        for client in [
            McpClientId::Cursor,
            McpClientId::ClaudeDesktop,
            McpClientId::ClaudeCode,
            McpClientId::OpencodeGlobal,
            McpClientId::OpencodeProject,
            McpClientId::ZcodeGlobal,
            McpClientId::ZcodeProject,
            McpClientId::Manual,
            McpClientId::Cline,
            McpClientId::Codex,
            McpClientId::Gemini,
            McpClientId::GithubCopilotCli,
            McpClientId::KiloCode,
            McpClientId::Rider,
            McpClientId::UnityAi,
            McpClientId::VscodeCopilot,
            McpClientId::VsCopilot,
            McpClientId::ZooCode,
            McpClientId::Antigravity,
            McpClientId::Custom,
        ] {
            assert!(!mcp_client_wire_key(client).is_empty());
        }
    }

}
