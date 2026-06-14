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

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};

use super::wizard::claude_desktop_config_path;
use crate::config::ai_toolkit::derived_mcp_index_path;

/// Local alias for [`derived_mcp_index_path`]. The wizard Step 4
/// also needs to validate the path against the toolkit
/// fingerprint set, but the **planning** / **writing** logic
/// here only needs the resolved string.
fn resolve_mcp_index_path(toolkit_root: &str, mcp_index_override: &str) -> Option<String> {
    derived_mcp_index_path(toolkit_root, mcp_index_override)
        .map(|p| p.to_string_lossy().into_owned())
}

/// MCP server key used in the parent config. Matches
/// `MCP_SERVER_KEY` in `ai_toolkit.ts` and the spec
/// "MCP server name in config: `unity-open-mcp` (recommended)"
/// (`mcp-tools.md` §Naming).
pub const MCP_SERVER_KEY: &str = "unity-open-mcp";

/// Sub-path inside the project where the Claude-compatible
/// skill file is copied. Always relative to the project root.
pub const CLAUDE_SKILL_REL: &str = ".claude/skills/unity-open-mcp/SKILL.md";

/// Sub-path inside the project where the OpenCode-specific
/// skill mirror is copied when OpenCode is selected. OpenCode
/// also reads `.claude/skills/` directly, but the wizard copies
/// to both locations for clients that look in either spot.
pub const OPENCODE_SKILL_REL: &str = ".opencode/skills/unity-open-mcp/SKILL.md";

/// Sub-path inside the toolkit root for the source skill file.
pub const TOOLKIT_SKILL_REL: &str = "skills/unity-open-mcp/SKILL.md";

/// Supported MCP client ids. Mirrors `McpClientId` in
/// `ai_toolkit.ts` and the radio group in `AiSetupWizard.svelte`
/// Step 4. `claude-code` and `manual` are intentionally not
/// backed by a writable JSON file.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum McpClientId {
    Cursor,
    ClaudeDesktop,
    ClaudeCode,
    OpencodeGlobal,
    OpencodeProject,
    Manual,
}

/// Per-client scope the wizard writes. The Step 4 UI combines
/// `McpClientId` with the Cursor project-scope toggle; the
/// Rust side collapses the pair into this single discriminant
/// so the planner and writer share one switch.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum McpScope {
    CursorGlobal,
    CursorProject,
    ClaudeDesktopGlobal,
    OpencodeGlobal,
    OpencodeProject,
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
    /// `{toolkitRoot}/mcp-server/dist/index.js`.
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
pub fn plan_mcp_config(params: McpConfigParams) -> Result<McpConfigPlan, McpConfigError> {
    let home = match dirs::home_dir() {
        Some(h) => h,
        None => {
            return Err(McpConfigError::new(
                "homeMissing",
                "Cannot resolve the home directory for a global MCP config target.",
            ));
        }
    };
    plan_mcp_config_at(&params, &home)
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
pub fn write_mcp_config(
    params: McpConfigParams,
) -> Result<McpConfigWriteResult, McpConfigError> {
    let home = match dirs::home_dir() {
        Some(h) => h,
        None => {
            return Err(McpConfigError::new(
                "homeMissing",
                "Cannot resolve the home directory for a global MCP config target.",
            ));
        }
    };
    write_mcp_config_at(&params, &home)
}

fn plan_mcp_config_at(params: &McpConfigParams, home: &Path) -> Result<McpConfigPlan, McpConfigError> {
    let scope = match resolve_scope(params) {
        Ok(s) => s,
        Err(McpScopeSkip::CliOnly) => {
            let resolved = resolve_mcp_index_path(&params.toolkit_root, &params.mcp_index_override)
                .ok_or_else(|| {
                    McpConfigError::new(
                        "mcpPathInvalid",
                        "Toolkit root is not set or the MCP override is empty.",
                    )
                })?;
            return Ok(McpConfigPlan {
                client: params.client,
                target_path: None,
                file_exists: false,
                would_write: false,
                preserved_keys: Vec::new(),
                proposed_json: None,
                command: Some(command_for(params, &resolved, true)),
                resolved_mcp_index: resolved,
            });
        }
        Err(McpScopeSkip::Manual) => {
            let resolved = resolve_mcp_index_path(&params.toolkit_root, &params.mcp_index_override)
                .ok_or_else(|| {
                    McpConfigError::new(
                        "mcpPathInvalid",
                        "Toolkit root is not set or the MCP override is empty.",
                    )
                })?;
            let json = build_full_config_json(
                params,
                &resolved,
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
                resolved_mcp_index: resolved,
            });
        }
    };
    let target = resolve_target_path(scope, &params.project_path, home).ok_or_else(|| {
        McpConfigError::new(
            "homeMissing",
            "Cannot resolve the home directory for a global MCP config target.",
        )
    })?;
    let resolved = resolve_mcp_index_path(&params.toolkit_root, &params.mcp_index_override)
        .ok_or_else(|| {
            McpConfigError::new(
                "mcpPathInvalid",
                "Toolkit root is not set or the MCP override is empty.",
            )
        })?;
    let file_exists = target.exists();
    let (existing_value, existing_keys) = if file_exists {
        read_existing_config(&target)?
    } else {
        (Value::Null, Vec::new())
    };
    let merged = build_full_config_json(params, &resolved, &target, existing_value.clone());
    let proposed_str = serde_json::to_string_pretty(&merged).unwrap_or_else(|_| merged.to_string());
    let would_write = !file_exists || merged_differs(&existing_value, &merged, scope);
    Ok(McpConfigPlan {
        client: params.client,
        target_path: Some(target.to_string_lossy().into_owned()),
        file_exists,
        would_write,
        preserved_keys: existing_keys,
        proposed_json: Some(proposed_str),
        command: None,
        resolved_mcp_index: resolved,
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
    let target = resolve_target_path(scope, &params.project_path, home).ok_or_else(|| {
        McpConfigError::new(
            "homeMissing",
            "Cannot resolve the home directory for a global MCP config target.",
        )
    })?;
    let resolved = resolve_mcp_index_path(&params.toolkit_root, &params.mcp_index_override)
        .ok_or_else(|| {
            McpConfigError::new(
                "mcpPathInvalid",
                "Toolkit root is not set or the MCP override is empty.",
            )
        })?;
    // Hard-block: refuse to write a config that points at a
    // non-existent MCP index. The Step 4 "Write config" button
    // is also disabled in this case, but the writer re-checks
    // so a stale UI cannot sneak past the gate.
    if !Path::new(&resolved).is_file() {
        return Err(McpConfigError::new(
            "mcpPathInvalid",
            format!(
                "MCP server entry point does not exist on disk: {}. Run `npm run build` in the toolkit's mcp-server/ folder.",
                resolved
            ),
        ));
    }
    let file_exists = target.exists();
    let (existing_value, _existing_keys) = if file_exists {
        read_existing_config(&target)?
    } else {
        (Value::Null, Vec::new())
    };
    let merged = build_full_config_json(params, &resolved, &target, existing_value.clone());
    let proposed_str = serde_json::to_string_pretty(&merged).unwrap_or_else(|_| merged.to_string());
    let would_write = !file_exists || merged_differs(&existing_value, &merged, scope);
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
        // leaves the original file untouched.
        let tmp_path = match target.extension().and_then(|e| e.to_str()) {
            Some(ext) => target.with_extension(format!("{}.tmp", ext)),
            None => target.with_extension("tmp"),
        };
        let pretty = match serde_json::to_string_pretty(&merged) {
            Ok(s) => s + "\n",
            Err(e) => {
                return Err(McpConfigError::new(
                    "serializeFailed",
                    format!("failed to serialize merged config: {}", e),
                ));
            }
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

fn resolve_scope(params: &McpConfigParams) -> Result<McpScope, McpScopeSkip> {
    match params.client {
        McpClientId::Cursor => Ok(if params.cursor_project_scope {
            McpScope::CursorProject
        } else {
            McpScope::CursorGlobal
        }),
        McpClientId::ClaudeDesktop => Ok(McpScope::ClaudeDesktopGlobal),
        McpClientId::OpencodeGlobal => Ok(McpScope::OpencodeGlobal),
        McpClientId::OpencodeProject => Ok(McpScope::OpencodeProject),
        McpClientId::ClaudeCode => Err(McpScopeSkip::CliOnly),
        McpClientId::Manual => Err(McpScopeSkip::Manual),
    }
}

fn resolve_target_path(scope: McpScope, project_path: &str, home: &Path) -> Option<PathBuf> {
    match scope {
        McpScope::CursorGlobal => Some(home.join(".cursor").join("mcp.json")),
        McpScope::CursorProject => Some(
            PathBuf::from(project_path)
                .join(".cursor")
                .join("mcp.json"),
        ),
        McpScope::ClaudeDesktopGlobal => Some(claude_desktop_config_path(home)),
        McpScope::OpencodeGlobal => Some(home.join(".config").join("opencode").join("opencode.json")),
        McpScope::OpencodeProject => Some(PathBuf::from(project_path).join("opencode.json")),
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
    let merge_key = merge_key_for(params.client);
    let (parent_key, child_key) = split_merge_key(&merge_key);
    let mut root: Map<String, Value> = match existing {
        Value::Object(m) => m,
        Value::Null => Map::new(),
        _ => Map::new(),
    };
    // Insert/overwrite only the `parent_key` → `child_key` path
    // we own. Every other top-level key, every other MCP
    // server, and every other entry under our parent key are
    // preserved verbatim.
    let parent_value = root
        .entry(parent_key.to_string())
        .or_insert_with(|| Value::Object(Map::new()));
    if !parent_value.is_object() {
        // Malformed parent — replace with an empty object
        // so the writer does not silently drop the user's
        // own `mcpServers` block. (The writer refused earlier
        // for invalid JSON, so this is only reachable when
        // the parent key exists but is a scalar.)
        *parent_value = Value::Object(Map::new());
    }
    if let Some(obj) = parent_value.as_object_mut() {
        obj.insert(child_key.to_string(), entry);
    }
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

fn build_entry_json(params: &McpConfigParams, resolved_index: &str) -> Value {
    let mut env = Map::new();
    env.insert(
        "UNITY_PROJECT_PATH".to_string(),
        Value::String(params.unity_project_path.clone()),
    );
    let port = if params.bridge_port.trim().is_empty() {
        "19120".to_string()
    } else {
        params.bridge_port.trim().to_string()
    };
    env.insert("UNITY_OPEN_MCP_BRIDGE_PORT".to_string(), Value::String(port));
    if params.include_unity_path && !params.unity_path.trim().is_empty() {
        env.insert(
            "UNITY_PATH".to_string(),
            Value::String(params.unity_path.trim().to_string()),
        );
    }
    match params.client {
        McpClientId::Cursor | McpClientId::ClaudeDesktop => json!({
            "command": "node",
            "args": [resolved_index],
            "env": Value::Object(env),
        }),
        McpClientId::OpencodeGlobal | McpClientId::OpencodeProject => json!({
            "type": "local",
            "command": ["node", resolved_index],
            "enabled": true,
            "environment": Value::Object(env),
        }),
        // CLI / manual clients never reach the file writer;
        // they only consume the JSON via the snippet panel.
        _ => json!({
            "command": "node",
            "args": [resolved_index],
            "env": Value::Object(env),
        }),
    }
}

fn merge_key_for(client: McpClientId) -> String {
    match client {
        McpClientId::Cursor | McpClientId::ClaudeDesktop => "mcpServers.unity-open-mcp".to_string(),
        McpClientId::OpencodeGlobal | McpClientId::OpencodeProject => "mcp.unity-open-mcp".to_string(),
        _ => String::new(),
    }
}

fn split_merge_key(merge_key: &str) -> (&str, &str) {
    match merge_key.split_once('.') {
        Some((parent, child)) => (parent, child),
        None => ("", ""),
    }
}

/// `true` when the merged value would actually change the
/// file on disk for this client. We compare the parent key's
/// `unity-open-mcp` child only — unrelated keys and other MCP
/// servers are not part of the "did we change anything" test.
fn merged_differs(existing: &Value, merged: &Value, scope: McpScope) -> bool {
    let merge_key = match scope {
        McpScope::CursorGlobal
        | McpScope::CursorProject
        | McpScope::ClaudeDesktopGlobal => "mcpServers.unity-open-mcp",
        McpScope::OpencodeGlobal | McpScope::OpencodeProject => "mcp.unity-open-mcp",
    };
    let (parent, child) = split_merge_key(merge_key);
    let before = existing
        .as_object()
        .and_then(|m| m.get(parent))
        .and_then(|v| v.as_object())
        .and_then(|m| m.get(child));
    let after = merged
        .as_object()
        .and_then(|m| m.get(parent))
        .and_then(|v| v.as_object())
        .and_then(|m| m.get(child));
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
/// client. Two env keys are required; the rest is derived
/// from the resolved MCP index path.
pub fn claude_mcp_add_command(
    unity_project_path: &str,
    bridge_port: &str,
    resolved_index: &str,
) -> String {
    let port = if bridge_port.trim().is_empty() {
        "19120".to_string()
    } else {
        bridge_port.trim().to_string()
    };
    format!(
        "claude mcp add {name} --env UNITY_PROJECT_PATH={project} --env UNITY_OPEN_MCP_BRIDGE_PORT={port} -- node {index}",
        name = MCP_SERVER_KEY,
        project = unity_project_path,
        port = port,
        index = resolved_index,
    )
}

fn command_for(params: &McpConfigParams, resolved_index: &str, _is_cli_only: bool) -> String {
    match params.client {
        McpClientId::ClaudeCode => claude_mcp_add_command(
            &params.unity_project_path,
            &params.bridge_port,
            resolved_index,
        ),
        _ => String::new(),
    }
}

// --- Skill copy ----------------------------------------------------------

/// One row in the skill-copy plan. The Done screen renders
/// this so the user can see which files would be created /
/// overwritten.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillCopyTarget {
    /// `"claude"` or `"opencode"`. Used by the UI for labels
    /// only — the target path carries the location.
    pub kind: SkillCopyKind,
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
}

/// `claude` = `.claude/skills/...` (always copied). `opencode`
/// = `.opencode/skills/...` (only copied when OpenCode was
/// selected in Step 4).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SkillCopyKind {
    Claude,
    Opencode,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillCopyPlan {
    pub project_path: String,
    pub toolkit_root: String,
    pub source_path: Option<String>,
    pub targets: Vec<SkillCopyTarget>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillCopyParams {
    pub project_path: String,
    pub toolkit_root: String,
    /// `true` when the user selected OpenCode in Step 4 (or
    /// the OpenCode project variant). Controls whether the
    /// `.opencode/...` mirror is included in the plan.
    pub opencode_selected: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillCopyResult {
    pub project_path: String,
    pub copied: Vec<SkillCopyTarget>,
    /// Targets that already existed and were **not** copied
    /// because the caller declined to overwrite. Surfaced on
    /// the Done screen so the user can see which files were
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
/// touching any file. The Done screen calls this to render
/// the per-target preview + "file exists" status.
#[tauri::command]
pub fn plan_skill_copy(params: SkillCopyParams) -> Result<SkillCopyPlan, SkillCopyError> {
    plan_skill_copy_at(&params)
}

/// Tauri command: apply the skill copy. For each target in
/// the plan, the writer creates a `.bak` of the existing
/// file (when one is present) and then overwrites — but only
/// when `overwrite_existing` is `true` for that target. The
/// caller is expected to have prompted the user (Done screen
/// renders an explicit confirmation) before passing
/// `overwrite_existing = true`.
#[tauri::command]
pub fn copy_skill_files(
    params: SkillCopyParams,
    overwrite_existing: bool,
) -> Result<SkillCopyResult, SkillCopyError> {
    copy_skill_files_at(&params, overwrite_existing)
}

fn plan_skill_copy_at(params: &SkillCopyParams) -> Result<SkillCopyPlan, SkillCopyError> {
    let project = PathBuf::from(&params.project_path);
    if !project.is_dir() {
        return Err(SkillCopyError::new(
            "notAUnityProject",
            "Project path is not a directory.",
        ));
    }
    let source_path = resolve_source_skill(&params.toolkit_root);
    let source_path_str = source_path
        .as_ref()
        .map(|p| p.to_string_lossy().into_owned());
    let mut targets = Vec::new();
    targets.push(build_skill_target(
        SkillCopyKind::Claude,
        &project,
        source_path.as_deref(),
    ));
    if params.opencode_selected {
        targets.push(build_skill_target(
            SkillCopyKind::Opencode,
            &project,
            source_path.as_deref(),
        ));
    }
    Ok(SkillCopyPlan {
        project_path: params.project_path.clone(),
        toolkit_root: params.toolkit_root.clone(),
        source_path: source_path_str,
        targets,
    })
}

fn build_skill_target(
    kind: SkillCopyKind,
    project: &Path,
    source: Option<&Path>,
) -> SkillCopyTarget {
    let relative = match kind {
        SkillCopyKind::Claude => CLAUDE_SKILL_REL,
        SkillCopyKind::Opencode => OPENCODE_SKILL_REL,
    };
    let target_path = project.join(relative);
    SkillCopyTarget {
        kind,
        target_path: target_path.to_string_lossy().into_owned(),
        relative_path: relative.to_string(),
        source_path: source.map(|p| p.to_string_lossy().into_owned()),
        exists: target_path.exists(),
    }
}

fn resolve_source_skill(toolkit_root: &str) -> Option<PathBuf> {
    let root = PathBuf::from(toolkit_root);
    if !root.is_dir() {
        return None;
    }
    let candidate = root.join(TOOLKIT_SKILL_REL);
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
    let source = match plan.source_path.as_deref() {
        Some(s) => PathBuf::from(s),
        None => {
            return Err(SkillCopyError::new(
                "sourceMissing",
                format!(
                    "Toolkit source skill file not found at {}/{}. Run the wizard with a valid toolkit root.",
                    params.toolkit_root, TOOLKIT_SKILL_REL
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

#[cfg(test)]
mod tests {
    use super::*;
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
    /// check passes.
    fn make_fake_toolkit(root: &Path) {
        let mcp_dir = root.join("mcp-server").join("dist");
        fs::create_dir_all(&mcp_dir).unwrap();
        fs::write(mcp_dir.join("index.js"), "module.exports = {};").unwrap();
    }

    fn make_cursor_params(_home: &Path, project: &Path, toolkit_root: &Path) -> McpConfigParams {
        McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: toolkit_root.to_string_lossy().into_owned(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: "19120".to_string(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::Cursor,
            cursor_project_scope: false,
        }
    }

    fn make_opencode_project_params(project: &Path, toolkit_root: &Path) -> McpConfigParams {
        McpConfigParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: toolkit_root.to_string_lossy().into_owned(),
            mcp_index_override: String::new(),
            unity_project_path: project.to_string_lossy().into_owned(),
            bridge_port: "19120".to_string(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::OpencodeProject,
            cursor_project_scope: false,
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
            "19120"
        );
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
            bridge_port: "19120".to_string(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::Cursor,
            cursor_project_scope: false,
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
            bridge_port: "19120".to_string(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::Cursor,
            cursor_project_scope: false,
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
            bridge_port: "19120".to_string(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::ClaudeCode,
            cursor_project_scope: false,
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
            bridge_port: "19120".to_string(),
            include_unity_path: false,
            unity_path: String::new(),
            client: McpClientId::ClaudeCode,
            cursor_project_scope: false,
        };
        let plan = plan_mcp_config_at(&params, project).unwrap();
        assert!(plan.target_path.is_none());
        assert!(plan.proposed_json.is_none());
        let cmd = plan.command.expect("claude-code renders a command");
        assert!(cmd.starts_with("claude mcp add unity-open-mcp"));
        assert!(cmd.contains("--env UNITY_PROJECT_PATH="));
        assert!(cmd.contains("--env UNITY_OPEN_MCP_BRIDGE_PORT=19120"));
        assert!(cmd.contains("-- node "));
        assert!(cmd.contains("index.js"));
    }

    #[test]
    fn claude_mcp_add_command_uses_default_port_when_blank() {
        let cmd = claude_mcp_add_command("/games/MyGame", "  ", "/u/mcp-server/dist/index.js");
        assert!(cmd.contains("UNITY_OPEN_MCP_BRIDGE_PORT=19120"));
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
    fn plan_skill_copy_includes_claude_target_only_when_opencode_not_selected() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        fs::create_dir_all(project).unwrap();
        let plan = plan_skill_copy_at(&SkillCopyParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            opencode_selected: false,
        })
        .unwrap();
        assert_eq!(plan.targets.len(), 1);
        assert_eq!(plan.targets[0].kind, SkillCopyKind::Claude);
        assert!(plan.targets[0].target_path.ends_with(".claude/skills/unity-open-mcp/SKILL.md"));
    }

    #[test]
    fn plan_skill_copy_includes_opencode_target_when_selected() {
        let dir = tempdir().unwrap();
        let project = dir.path();
        fs::create_dir_all(project).unwrap();
        let plan = plan_skill_copy_at(&SkillCopyParams {
            project_path: project.to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            opencode_selected: true,
        })
        .unwrap();
        assert_eq!(plan.targets.len(), 2);
        assert!(plan.targets.iter().any(|t| t.kind == SkillCopyKind::Opencode));
    }

    #[test]
    fn copy_skill_files_creates_claude_target_and_skips_existing_without_overwrite() {
        let project_dir = tempdir().unwrap();
        let project = project_dir.path();
        let root = tempdir().unwrap();
        let skill = root.path().join("skills").join("unity-open-mcp").join("SKILL.md");
        write_text(&skill, "# unity-open-mcp\n\nHello.\n");
        // Pre-existing target file the user has customised.
        let existing = project.join(".claude/skills/unity-open-mcp/SKILL.md");
        write_text(&existing, "# user's custom notes\n");
        let result = copy_skill_files_at(
            &SkillCopyParams {
                project_path: project.to_string_lossy().into_owned(),
                toolkit_root: root.path().to_string_lossy().into_owned(),
                opencode_selected: false,
            },
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
        let skill = root.path().join("skills").join("unity-open-mcp").join("SKILL.md");
        write_text(&skill, "# unity-open-mcp\n\nToolkit content.\n");
        let existing = project.join(".claude/skills/unity-open-mcp/SKILL.md");
        write_text(&existing, "# user's old notes\n");
        let result = copy_skill_files_at(
            &SkillCopyParams {
                project_path: project.to_string_lossy().into_owned(),
                toolkit_root: root.path().to_string_lossy().into_owned(),
                opencode_selected: true,
            },
            true,
        )
        .unwrap();
        assert_eq!(result.copied.len(), 2);
        assert_eq!(result.overwritten.len(), 1);
        assert_eq!(result.skipped.len(), 0);
        let backup = project.join(".claude/skills/unity-open-mcp/SKILL.md.bak");
        assert!(backup.exists());
        assert_eq!(fs::read_to_string(&backup).unwrap(), "# user's old notes\n");
        // OpenCode mirror was created (it did not exist).
        let opencode_target = project.join(".opencode/skills/unity-open-mcp/SKILL.md");
        assert!(opencode_target.exists());
        assert_eq!(
            fs::read_to_string(&opencode_target).unwrap(),
            "# unity-open-mcp\n\nToolkit content.\n"
        );
    }

    #[test]
    fn copy_skill_files_errors_when_source_missing() {
        let project_dir = tempdir().unwrap();
        let project = project_dir.path();
        let result = copy_skill_files_at(
            &SkillCopyParams {
                project_path: project.to_string_lossy().into_owned(),
                toolkit_root: "/repos/this/does/not/exist".to_string(),
                opencode_selected: false,
            },
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
}
