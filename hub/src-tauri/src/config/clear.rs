//! "Clear AI Setup" — the destructive inverse of the wizard.
//!
//! Removes every artifact the wizard (Steps 3 / 4 / 4b) writes for a
//! given project, best-effort with `.bak` backups first:
//!
//! - `Packages/manifest.json` — strips the bridge + verify package ids.
//! - MCP client configs, at every path the writer can target
//!   (`mcp_config::config_locations`): inside the Unity project
//!   unconditionally; at the repository root a commit-safe write uses,
//!   only the entry that resolves to this project; in global files, only
//!   the entry whose `UNITY_PROJECT_PATH` matches this project.
//! - The agent skill under every folder the wizard installs it in
//!   (`mcp_config::skill_roots`): each `SKILL.md`, the template reference
//!   pages beside it, and the `references/` and `unity-open-mcp/` folders
//!   once they are empty.
//!
//! Claude Code (CLI-only) and Manual have no on-disk artifact and are
//! reported as N/A rather than errors. Everything else that fails
//! (missing file, unreadable JSON, permission denied) is collected
//! into `errors` so a partial clear still succeeds for the parts that
//! could be removed.

use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

use crate::config::paths;
use super::mcp_config::{
    client_format, config_locations, entry_targets_project, merge_key_path, same_path, skill_roots,
    ClientFormat, ClientScope, ConfigLocation, McpClientId, MCP_SERVER_KEY, SKILL_REFERENCES_DIR,
    SKILL_REFERENCE_FILES, SKILL_REL_PATHS,
};
use super::wizard::{BRIDGE_PACKAGE_ID, VERIFY_PACKAGE_ID};
use crate::config::constants::PROJECT_PATH_ENV_VAR;

/// Which `unity-open-mcp` entry a clear target may remove.
enum EntryGuard {
    /// A config inside the Unity project: its entry is this project's.
    Unconditional,
    /// A global config shared across projects: only the entry whose
    /// `UNITY_PROJECT_PATH` is this project (or that names none).
    ProjectPath,
    /// A config at the repository root a commit-safe write uses: only the
    /// entry that resolves to this project from that root, so a sibling
    /// Unity project in the same repository keeps its setup.
    Workspace(PathBuf),
}

impl EntryGuard {
    fn admits(&self, entry: &Value, project_path: &str) -> bool {
        match self {
            EntryGuard::Unconditional => true,
            EntryGuard::ProjectPath => entry_matches_project(entry, project_path),
            EntryGuard::Workspace(root) => {
                entry_targets_project(entry, root, Path::new(project_path))
            }
        }
    }
}

/// One config file the clear pass visits, with the guard that decides
/// whether its entry belongs to this project.
struct ClearTarget {
    client: McpClientId,
    scope: ClientScope,
    path: PathBuf,
    guard: EntryGuard,
}

impl From<ConfigLocation> for ClearTarget {
    fn from(location: ConfigLocation) -> Self {
        let guard = match (location.workspace, location.scope) {
            (Some(root), _) => EntryGuard::Workspace(root),
            (None, ClientScope::Global) => EntryGuard::ProjectPath,
            (None, ClientScope::Project) => EntryGuard::Unconditional,
        };
        Self {
            client: location.client,
            scope: location.scope,
            path: location.path,
            guard,
        }
    }
}

/// One client config touched by the clear pass.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClearedClientConfig {
    /// Display label (e.g. "Cursor (global)").
    pub label: String,
    /// Absolute path of the config file that was (or would have been) modified.
    pub path: String,
    /// `true` when the `unity-open-mcp` entry was present and removed.
    pub removed: bool,
    /// `true` when a `.bak` backup was created next to the file.
    pub backed_up: bool,
}

/// Aggregate result of a clear pass.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClearAiSetupResult {
    /// `true` when bridge + verify were stripped from the manifest.
    pub manifest_cleared: bool,
    /// `.bak` path for the manifest, when a backup was created.
    pub manifest_backup_path: Option<String>,
    /// Per-client-config outcome.
    pub client_configs_cleared: Vec<ClearedClientConfig>,
    /// Absolute paths of the `SKILL.md` files that were deleted.
    pub skills_removed: Vec<String>,
    /// Absolute paths of the template reference files deleted from the
    /// `references/` folders beside them.
    pub skill_references_removed: Vec<String>,
    /// Non-fatal errors encountered (missing files are NOT errors).
    pub errors: Vec<String>,
}

/// Label for a target, matching the wizard's "Cursor (global)" style.
fn target_label(target: &ClearTarget) -> String {
    let name = match target.client {
        McpClientId::Cursor => "Cursor",
        McpClientId::ClaudeDesktop => "Claude Desktop",
        McpClientId::ClaudeCode => "Claude Code",
        McpClientId::OpencodeGlobal | McpClientId::OpencodeProject => "OpenCode",
        McpClientId::ZcodeGlobal | McpClientId::ZcodeProject => "ZCode",
        McpClientId::Manual => "Manual",
        McpClientId::Cline => "Cline",
        McpClientId::Codex => "Codex",
        McpClientId::Gemini => "Gemini",
        McpClientId::GithubCopilotCli => "GitHub Copilot CLI",
        McpClientId::KiloCode => "Kilo Code",
        McpClientId::Rider => "Rider (Junie)",
        McpClientId::UnityAi => "Unity AI",
        McpClientId::VscodeCopilot => "VS Code Copilot",
        McpClientId::VsCopilot => "Visual Studio Copilot",
        McpClientId::ZooCode => "ZooCode",
        McpClientId::Antigravity => "Antigravity",
        McpClientId::Custom => "Custom",
    };
    let suffix = match (&target.guard, target.scope) {
        (EntryGuard::Workspace(_), _) => "repository root",
        (_, ClientScope::Global) => "global",
        (_, ClientScope::Project) => "project",
    };
    format!("{name} ({suffix})")
}

/// `true` when the entry at `key_path` carries an `env.UNITY_PROJECT_PATH`
/// (or `environment.UNITY_PROJECT_PATH` for OpenCode) equal to `project_path`.
/// Returns `true` when the field is absent — a project entry without the
/// env marker is treated as belonging to this project so we still clear it.
fn entry_matches_project(entry: &Value, project_path: &str) -> bool {
    let env = entry
        .get("env")
        .or_else(|| entry.get("environment"))
        .and_then(|e| e.as_object());
    match env.and_then(|e| e.get(PROJECT_PATH_ENV_VAR)).and_then(Value::as_str) {
        Some(p) => same_path(p, project_path),
        None => true,
    }
}

/// Remove the `unity-open-mcp` leaf at `key_path` from `root` when `admits`
/// accepts it as this project's entry. Returns `true` when a removal
/// happened; [`prune_empty_along`] then drops the emptied parents so a
/// cleared project file does not leave `{"mcp":{"servers":{}}}`.
fn remove_entry(root: &mut Value, key_path: &[&str], admits: impl Fn(&Value) -> bool) -> bool {
    if key_path.is_empty() {
        return false;
    }
    // Resolve the leaf parent + capture the entry to test against the
    // project before mutating.
    let mut current = root;
    for segment in &key_path[..key_path.len() - 1] {
        let Some(child) = current
            .as_object_mut()
            .and_then(|o| o.get_mut(*segment))
        else {
            return false;
        };
        if !child.is_object() {
            return false;
        }
        current = child;
    }
    let leaf = key_path[key_path.len() - 1];
    let Some(obj) = current.as_object_mut() else {
        return false;
    };
    let take = obj.get(leaf).is_some_and(admits);
    if take {
        obj.remove(leaf);
        return true;
    }
    false
}

/// Recursively drop empty object children left behind after a leaf
/// removal. Walks the same key path in reverse, removing an
/// intermediate when it has become an empty object.
fn prune_empty_along(root: &mut Value, key_path: &[&str]) {
    // From the deepest intermediate up to the root, ask the per-level
    // helper to remove the candidate segment if it is an empty object.
    // Each level is its own function call so the mutable borrow ends
    // before the next iteration borrows `root` again.
    for depth in (1..key_path.len()).rev() {
        prune_empty_at_depth(root, key_path, depth);
    }
}

/// Remove `key_path[depth - 1]` from its parent (reached by walking
/// `key_path[..depth - 1]`) when that child is an empty object. Does
/// nothing when the descent or the test fails.
fn prune_empty_at_depth(root: &mut Value, key_path: &[&str], depth: usize) {
    let candidate = key_path[depth - 1];
    // Descend to the candidate's parent, then test + remove in two
    // non-overlapping scopes.
    let parent = {
        let mut current: &mut Value = root;
        for segment in &key_path[..depth - 1] {
            let Some(child) = current
                .as_object_mut()
                .and_then(|o| o.get_mut(*segment))
            else {
                return;
            };
            current = child;
        }
        current
    };
    let is_empty = parent
        .as_object()
        .and_then(|o| o.get(candidate))
        .map(|v| v.as_object().is_some_and(Map::is_empty))
        .unwrap_or(false);
    if is_empty {
        if let Some(obj) = parent.as_object_mut() {
            obj.remove(candidate);
        }
    }
}

/// Atomic write via `persistence::atomic_write_at` (unique staging name +
/// fsync + rename). C1 — the previous shape staged through a FIXED
/// `<target>.<ext>.tmp` filename, so two overlapping writers to the same
/// target shared a temp inode and could publish a byte-mix (the same H12
/// hazard `unique_tmp_path` exists to close). Writes pretty JSON with a
/// trailing newline to match the writer's output.
fn write_json_atomic(target: &Path, value: &Value) -> std::io::Result<()> {
    let pretty = match serde_json::to_string_pretty(value) {
        Ok(s) => s + "\n",
        Err(e) => return Err(std::io::Error::other(e)),
    };
    crate::config::persistence::atomic_write_at(target, &pretty)
}

/// Back up `target` to `<target>.bak` (extension-aware, matching
/// `mcp_config.rs`).
fn backup(target: &Path) -> std::io::Result<PathBuf> {
    let bak = target.with_extension(match target.extension().and_then(|e| e.to_str()) {
        Some(ext) => format!("{ext}.bak"),
        None => "bak".to_string(),
    });
    fs::copy(target, &bak)?;
    Ok(bak)
}

/// Clear one client config target in place. Appends to `result` and
/// collects non-fatal errors instead of aborting. Branches on the
/// client format: JSON clients go through the JSON merge/prune path;
/// TOML clients (Codex) parse via the `toml` crate, remove the entry
/// from the `mcp_servers` table, and re-serialize.
fn clear_client_target(
    target: &ClearTarget,
    project_path: &str,
    result: &mut ClearAiSetupResult,
) {
    let label = target_label(target);
    if !target.path.exists() {
        // Nothing to clear — record the candidate so the UI can show
        // "no entry found" rather than a silent skip.
        result.client_configs_cleared.push(ClearedClientConfig {
            label,
            path: target.path.to_string_lossy().into_owned(),
            removed: false,
            backed_up: false,
        });
        return;
    }
    let content = match fs::read_to_string(&target.path) {
        Ok(s) => s,
        Err(e) => {
            result
                .errors
                .push(format!("{label}: cannot read {}: {e}", target.path.display()));
            return;
        }
    };
    if content.trim().is_empty() {
        result.client_configs_cleared.push(ClearedClientConfig {
            label,
            path: target.path.to_string_lossy().into_owned(),
            removed: false,
            backed_up: false,
        });
        return;
    }
    // Dispatch on format. TOML (Codex) takes a dedicated path; every
    // other file-backed client is JSON.
    match client_format(target.client) {
        ClientFormat::Toml => clear_toml_target(target, &content, project_path, &label, result),
        _ => clear_json_target(target, &content, project_path, &label, result),
    }
}

/// JSON clear path — shared by every JSON client. Parses, removes the
/// `unity-open-mcp` leaf at the client's merge key, prunes empty
/// parents, backs up, and re-writes atomically.
fn clear_json_target(
    target: &ClearTarget,
    content: &str,
    project_path: &str,
    label: &str,
    result: &mut ClearAiSetupResult,
) {
    let mut value: Value = match serde_json::from_str(content) {
        Ok(v) => v,
        Err(e) => {
            result.errors.push(format!(
                "{label}: existing config at {} is not valid JSON: {e}",
                target.path.display()
            ));
            return;
        }
    };
    let key_path = merge_key_path(target.client);
    let removed = remove_entry(&mut value, &key_path, |entry| {
        target.guard.admits(entry, project_path)
    });
    if !removed {
        result.client_configs_cleared.push(ClearedClientConfig {
            label: label.to_string(),
            path: target.path.to_string_lossy().into_owned(),
            removed: false,
            backed_up: false,
        });
        return;
    }
    prune_empty_along(&mut value, &key_path);
    let backup_path = match backup(&target.path) {
        Ok(b) => b.to_string_lossy().into_owned(),
        Err(e) => {
            result.errors.push(format!(
                "{label}: cannot create backup at {}: {e}",
                target.path.display()
            ));
            return;
        }
    };
    // H16: the previous shape pushed `removed: true` even when the write
    // failed, so the wizard's "Removed N MCP config entries" summary
    // counted entries that are still on disk. Mirror the backup-error arm
    // and return early on write failure — no misleading `removed: true`
    // is recorded.
    if let Err(e) = write_json_atomic(&target.path, &value) {
        result.errors.push(format!(
            "{label}: failed to write {}: {e}",
            target.path.display()
        ));
        let _ = &backup_path;
        return;
    }
    result.client_configs_cleared.push(ClearedClientConfig {
        label: label.to_string(),
        path: target.path.to_string_lossy().into_owned(),
        removed: true,
        backed_up: true,
    });
}

/// TOML clear path (Codex `.codex/config.toml`). Parses the existing
/// file, removes the `[mcp_servers.unity-open-mcp]` table (subject to
/// the global `UNITY_PROJECT_PATH` guard), and re-serializes.
fn clear_toml_target(
    target: &ClearTarget,
    content: &str,
    project_path: &str,
    label: &str,
    result: &mut ClearAiSetupResult,
) {
    let mut root: toml::value::Table = match toml::from_str(content) {
        Ok(t) => t,
        Err(e) => {
            result.errors.push(format!(
                "{label}: existing config at {} is not valid TOML: {e}",
                target.path.display()
            ));
            return;
        }
    };
    let removed = remove_toml_entry(&mut root, |entry| target.guard.admits(entry, project_path));
    if !removed {
        result.client_configs_cleared.push(ClearedClientConfig {
            label: label.to_string(),
            path: target.path.to_string_lossy().into_owned(),
            removed: false,
            backed_up: false,
        });
        return;
    }
    // Prune an empty mcp_servers table.
    if let Some(toml::Value::Table(servers)) = root.get("mcp_servers") {
        if servers.is_empty() {
            root.remove("mcp_servers");
        }
    }
    let backup_path = match backup(&target.path) {
        Ok(b) => b.to_string_lossy().into_owned(),
        Err(e) => {
            result.errors.push(format!(
                "{label}: cannot create backup at {}: {e}",
                target.path.display()
            ));
            return;
        }
    };
    // H16 (TOML path): same fix as the JSON path — return early on write
    // failure so `removed: true` is not recorded for an entry still on disk.
    let body = toml::to_string_pretty(&toml::Value::Table(root.clone()))
        .unwrap_or_default();
    if let Err(e) = write_text_atomic(&target.path, &body) {
        result.errors.push(format!(
            "{label}: failed to write {}: {e}",
            target.path.display()
        ));
        let _ = &backup_path;
        return;
    }
    result.client_configs_cleared.push(ClearedClientConfig {
        label: label.to_string(),
        path: target.path.to_string_lossy().into_owned(),
        removed: true,
        backed_up: true,
    });
}

/// Remove the `[mcp_servers.unity-open-mcp]` table from `root` when `admits`
/// accepts it (read as JSON, like every other entry). Returns `true` when a
/// removal happened.
fn remove_toml_entry(root: &mut toml::value::Table, admits: impl Fn(&Value) -> bool) -> bool {
    let Some(servers_val) = root.get_mut("mcp_servers") else {
        return false;
    };
    let toml::Value::Table(servers) = servers_val else {
        return false;
    };
    let take = servers
        .get(MCP_SERVER_KEY)
        .and_then(|entry| serde_json::to_value(entry).ok())
        .is_some_and(|entry| admits(&entry));
    if take {
        servers.remove(MCP_SERVER_KEY);
        true
    } else {
        false
    }
}

/// Atomic text write, mirroring `write_json_atomic` (C1: same unique-
/// staging-name conversion — the fixed `.tmp` name had the H12 collision
/// hazard).
fn write_text_atomic(target: &Path, body: &str) -> std::io::Result<()> {
    crate::config::persistence::atomic_write_at(target, body)
}

/// Strip bridge + verify from `Packages/manifest.json`. Preserves all
/// other keys and formatting via serde_json's object model; a `.bak`
/// is left next to the original when a change is applied.
fn clear_manifest(project_path: &str, result: &mut ClearAiSetupResult) {
    let manifest = PathBuf::from(project_path).join("Packages").join("manifest.json");
    if !manifest.is_file() {
        return;
    }
    let content = match fs::read_to_string(&manifest) {
        Ok(s) => s,
        Err(e) => {
            result
                .errors
                .push(format!("manifest: cannot read {}: {e}", manifest.display()));
            return;
        }
    };
    let mut value: Value = match serde_json::from_str(&content) {
        Ok(v) => v,
        Err(e) => {
            result.errors.push(format!(
                "manifest: {} is not valid JSON: {e}",
                manifest.display()
            ));
            return;
        }
    };
    let deps = match value.get_mut("dependencies").and_then(|d| d.as_object_mut()) {
        Some(d) => d,
        None => return,
    };
    let had_bridge = deps.remove(BRIDGE_PACKAGE_ID).is_some();
    let had_verify = deps.remove(VERIFY_PACKAGE_ID).is_some();
    if !had_bridge && !had_verify {
        return;
    }
    let bak = match backup(&manifest) {
        Ok(b) => b,
        Err(e) => {
            result
                .errors
                .push(format!("manifest: cannot create backup: {e}"));
            return;
        }
    };
    match write_json_atomic(&manifest, &value) {
        Ok(()) => {
            result.manifest_cleared = true;
            result.manifest_backup_path = Some(bak.to_string_lossy().into_owned());
        }
        Err(e) => {
            result
                .errors
                .push(format!("manifest: failed to write {}: {e}", manifest.display()));
        }
    }
}

/// Delete the skill the wizard installed under every folder it installs the
/// skill in for this project: each known `SKILL.md`, the template reference
/// files beside it, then the `references/` and `unity-open-mcp/` folders
/// when nothing else is left in them. Any other file there (a `SKILL.md.bak`,
/// the user's own notes) is kept, and so is the folder holding it. The
/// references go even when `SKILL.md` is already gone, so a half-removed
/// skill is finished off. A symlinked skill or `references/` folder is not a
/// copy the wizard made, and nothing is deleted through it.
fn clear_skills(project_path: &str, home: &Path, result: &mut ClearAiSetupResult) {
    for root in skill_roots(project_path, home) {
        for rel in SKILL_REL_PATHS {
            let skill = root.join(rel);
            let Some(skill_dir) = skill.parent() else { continue };
            if !is_real_dir(skill_dir) {
                continue;
            }
            if skill.is_file() {
                match fs::remove_file(&skill) {
                    Ok(()) => result.skills_removed.push(skill.to_string_lossy().into_owned()),
                    Err(e) => result
                        .errors
                        .push(format!("skill: cannot remove {}: {e}", skill.display())),
                }
            }
            clear_skill_references(skill_dir, result);
            remove_dir_if_empty(skill_dir);
        }
    }
}

/// Delete the template reference files from `skill_dir/references/`, then
/// the folder when that empties it. Only regular files named like a
/// template page are removed.
fn clear_skill_references(skill_dir: &Path, result: &mut ClearAiSetupResult) {
    let references = skill_dir.join(SKILL_REFERENCES_DIR);
    if !is_real_dir(&references) {
        return;
    }
    for name in SKILL_REFERENCE_FILES {
        let file = references.join(name);
        if !fs::symlink_metadata(&file).is_ok_and(|m| m.is_file()) {
            continue;
        }
        match fs::remove_file(&file) {
            Ok(()) => result
                .skill_references_removed
                .push(file.to_string_lossy().into_owned()),
            Err(e) => result
                .errors
                .push(format!("skill: cannot remove {}: {e}", file.display())),
        }
    }
    remove_dir_if_empty(&references);
}

/// `true` when `path` is a directory itself, not a symlink to one.
fn is_real_dir(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok_and(|m| m.is_dir())
}

/// Best-effort removal of a folder the clear emptied: `remove_dir` refuses
/// a folder that still holds anything, which then stays.
fn remove_dir_if_empty(dir: &Path) {
    let _ = fs::remove_dir(dir);
}

/// Non-Tauri entry point; testable without spinning up the command surface.
pub fn clear_ai_setup_at(project_path: &str, home: &Path) -> ClearAiSetupResult {
    let mut result = ClearAiSetupResult::default();
    if project_path.trim().is_empty() {
        result.errors.push("projectPath is empty.".to_string());
        return result;
    }
    clear_manifest(project_path, &mut result);
    for location in config_locations(project_path, home) {
        clear_client_target(&ClearTarget::from(location), project_path, &mut result);
    }
    clear_skills(project_path, home, &mut result);
    result
}

/// Tauri command. Best-effort across all artifacts; per-target failures
/// are collected into `errors` rather than aborting the whole pass.
///
/// Runs on the blocking pool (via `spawn_blocking`) so the manifest
/// rewrite + per-config-file merges (each an atomic write + `fsync`)
/// cannot stall the WebView main thread on a slow/cloud-synced volume.
#[tauri::command]
pub async fn clear_ai_setup(project_path: String) -> Result<ClearAiSetupResult, String> {
    let home = paths::home_dir()
        .ok_or_else(|| "Cannot resolve the home directory.".to_string())?;
    tauri::async_runtime::spawn_blocking(move || clear_ai_setup_at(&project_path, &home))
        .await
        .map_err(|e| format!("clear_ai_setup task failed: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use super::super::mcp_config::MCP_SERVER_KEY;
    use serde_json::json;

    fn entry(project: &str) -> Value {
        json!({
            "command": "npx",
            "args": ["-y", "unity-open-mcp@0.8.2"],
            "env": { "UNITY_PROJECT_PATH": project }
        })
    }

    #[test]
    fn remove_entry_global_guard_matches_project() {
        let mut root = json!({
            "mcpServers": {
                "unity-open-mcp": entry("/p/demo"),
                "other-server": { "command": "x" }
            }
        });
        let key = vec!["mcpServers", MCP_SERVER_KEY];
        let removed = remove_entry(&mut root, &key, |e| EntryGuard::ProjectPath.admits(e, "/p/demo"));
        assert!(removed);
        assert!(root["mcpServers"]["unity-open-mcp"].is_null());
        assert_eq!(root["mcpServers"]["other-server"]["command"], "x");
    }

    #[test]
    fn remove_entry_global_guard_skips_other_project() {
        let mut root = json!({
            "mcpServers": { "unity-open-mcp": entry("/p/other") }
        });
        let key = vec!["mcpServers", MCP_SERVER_KEY];
        let removed = remove_entry(&mut root, &key, |e| EntryGuard::ProjectPath.admits(e, "/p/demo"));
        assert!(!removed);
        assert_eq!(
            root["mcpServers"]["unity-open-mcp"]["env"]["UNITY_PROJECT_PATH"],
            "/p/other"
        );
    }

    #[test]
    fn remove_entry_global_guard_ignores_separator_noise() {
        let mut root = json!({
            "mcpServers": { "unity-open-mcp": entry("/p/./demo//") }
        });
        let key = vec!["mcpServers", MCP_SERVER_KEY];
        assert!(remove_entry(&mut root, &key, |e| EntryGuard::ProjectPath.admits(e, "/p/demo")));
    }

    #[test]
    fn remove_entry_workspace_guard_reads_an_inline_subpath() {
        let inline = |subpath: &str| {
            json!({
                "mcpServers": { "unity-open-mcp": {
                    "command": "npx",
                    "args": [
                        "-y",
                        "unity-open-mcp",
                        "--project-from-cwd",
                        format!("--unity-subpath={subpath}")
                    ]
                } }
            })
        };
        let key = vec!["mcpServers", MCP_SERVER_KEY];
        let guard = EntryGuard::Workspace(PathBuf::from("/repo"));
        let mut ours = inline("Client");
        assert!(remove_entry(&mut ours, &key, |e| guard.admits(e, "/repo/Client")));
        let mut sibling = inline("Server");
        assert!(!remove_entry(&mut sibling, &key, |e| guard.admits(e, "/repo/Client")));
    }

    #[test]
    fn remove_entry_project_scope_unconditional() {
        let mut root = json!({
            "mcp": { "servers": { "unity-open-mcp": entry("/p/other") } }
        });
        let key = vec!["mcp", "servers", MCP_SERVER_KEY];
        let removed = remove_entry(&mut root, &key, |e| EntryGuard::Unconditional.admits(e, "/p/demo"));
        assert!(removed);
    }

    #[test]
    fn prune_empty_along_drops_empty_intermediates() {
        let mut root = json!({ "mcp": { "servers": {}, "kept": 1 } });
        prune_empty_along(&mut root, &["mcp", "servers", "unity-open-mcp"]);
        assert!(root["mcp"]["servers"].is_null());
        assert_eq!(root["mcp"]["kept"], 1);
    }

    #[test]
    fn entry_without_env_marker_is_cleared() {
        let mut root = json!({ "mcpServers": { "unity-open-mcp": { "command": "x" } } });
        let key = vec!["mcpServers", MCP_SERVER_KEY];
        assert!(remove_entry(&mut root, &key, |e| EntryGuard::ProjectPath.admits(e, "/p/demo")));
    }

    #[test]
    fn clear_ai_setup_at_strips_manifest_and_configs() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path().to_path_buf();
        let project = home.join("demo");
        fs::create_dir_all(project.join("Packages")).unwrap();
        fs::write(
            project.join("Packages").join("manifest.json"),
            json!({
                "dependencies": {
                    "com.unity.ugui": "1.0.0",
                    BRIDGE_PACKAGE_ID: "https://example/bridge#bridge-v1.0.0",
                    VERIFY_PACKAGE_ID: "https://example/verify#verify-v1.0.0",
                }
            })
            .to_string(),
        )
        .unwrap();
        fs::create_dir_all(project.join(".zcode").join("cli")).unwrap();
        fs::write(
            project.join(".zcode").join("cli").join("config.json"),
            json!({
                "mcp": { "servers": { "unity-open-mcp": entry(project.to_str().unwrap()) } }
            })
            .to_string(),
        )
        .unwrap();
        // A global Cursor file shared with another project.
        fs::create_dir_all(home.join(".cursor")).unwrap();
        fs::write(
            home.join(".cursor").join("mcp.json"),
            json!({
                "mcpServers": {
                    "unity-open-mcp": entry("/p/other"),
                    "unity-open-mcp-demo": entry(project.to_str().unwrap())
                }
            })
            .to_string(),
        )
        .unwrap();
        fs::create_dir_all(project.join(".agents").join("skills").join("unity-open-mcp")).unwrap();
        fs::write(
            project
                .join(".agents")
                .join("skills")
                .join("unity-open-mcp")
                .join("SKILL.md"),
            "# skill",
        )
        .unwrap();

        let result = clear_ai_setup_at(project.to_str().unwrap(), &home);
        assert!(result.manifest_cleared);
        assert!(result.errors.is_empty(), "{:?}", result.errors);

        let manifest: Value =
            serde_json::from_str(&fs::read_to_string(project.join("Packages").join("manifest.json")).unwrap())
                .unwrap();
        assert!(manifest["dependencies"][BRIDGE_PACKAGE_ID].is_null());
        assert!(manifest["dependencies"][VERIFY_PACKAGE_ID].is_null());
        assert_eq!(manifest["dependencies"]["com.unity.ugui"], "1.0.0");

        let zcode: Value =
            serde_json::from_str(&fs::read_to_string(project.join(".zcode").join("cli").join("config.json")).unwrap())
                .unwrap();
        assert!(zcode["mcp"]["servers"].is_null() || zcode["mcp"].is_null());

        let cursor_global: Value =
            serde_json::from_str(&fs::read_to_string(home.join(".cursor").join("mcp.json")).unwrap()).unwrap();
        // The other-project entry is preserved; the demo entry we never
        // wrote under the canonical key is untouched.
        assert_eq!(
            cursor_global["mcpServers"]["unity-open-mcp"]["env"]["UNITY_PROJECT_PATH"],
            "/p/other"
        );

        assert!(
            !project
                .join(".agents")
                .join("skills")
                .join("unity-open-mcp")
                .join("SKILL.md")
                .exists()
        );
        assert!(result.manifest_backup_path.is_some());
    }

    /// `<root>/<client dir>/skills/unity-open-mcp`, as the wizard installs it.
    fn skill_dir(root: &Path, client_dir: &str) -> PathBuf {
        root.join(client_dir).join("skills").join("unity-open-mcp")
    }

    /// Lay out an installed skill: `SKILL.md` plus every template reference.
    fn install_skill(dir: &Path) {
        fs::create_dir_all(dir.join(SKILL_REFERENCES_DIR)).unwrap();
        fs::write(dir.join("SKILL.md"), "# skill").unwrap();
        for name in SKILL_REFERENCE_FILES {
            fs::write(dir.join(SKILL_REFERENCES_DIR).join(name), "# reference").unwrap();
        }
    }

    fn file_names(dir: &Path) -> Vec<String> {
        let mut names: Vec<String> = fs::read_dir(dir)
            .unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        names.sort_unstable();
        names
    }

    #[test]
    fn clear_removes_the_skill_references_and_their_folders() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path().join("home");
        let project = tmp.path().join("demo");
        fs::create_dir_all(&home).unwrap();
        let agents = skill_dir(&project, ".agents");
        let claude = skill_dir(&project, ".claude");
        install_skill(&agents);
        install_skill(&claude);

        let result = clear_ai_setup_at(project.to_str().unwrap(), &home);
        assert!(result.errors.is_empty(), "{:?}", result.errors);
        assert_eq!(result.skills_removed.len(), 2);
        assert_eq!(result.skill_references_removed.len(), 2 * SKILL_REFERENCE_FILES.len());
        assert!(result
            .skill_references_removed
            .contains(&agents.join(SKILL_REFERENCES_DIR).join(SKILL_REFERENCE_FILES[0]).to_string_lossy().into_owned()));
        for dir in [&agents, &claude] {
            assert!(!dir.exists(), "{}", dir.display());
            // The client's own `skills/` folder is not the wizard's to remove.
            assert!(dir.parent().unwrap().is_dir());
        }
    }

    #[test]
    fn clear_keeps_user_files_and_the_folders_that_hold_them() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path().join("home");
        let project = tmp.path().join("demo");
        fs::create_dir_all(&home).unwrap();
        // A note of the user's among the references.
        let agents = skill_dir(&project, ".agents");
        install_skill(&agents);
        fs::write(agents.join(SKILL_REFERENCES_DIR).join("team-notes.md"), "ours").unwrap();
        // The backup a skill overwrite leaves beside `SKILL.md`.
        let claude = skill_dir(&project, ".claude");
        install_skill(&claude);
        fs::write(claude.join("SKILL.md.bak"), "customized").unwrap();

        let result = clear_ai_setup_at(project.to_str().unwrap(), &home);
        assert!(result.errors.is_empty(), "{:?}", result.errors);
        assert_eq!(result.skill_references_removed.len(), 2 * SKILL_REFERENCE_FILES.len());
        assert_eq!(file_names(&agents), vec![SKILL_REFERENCES_DIR]);
        assert_eq!(file_names(&agents.join(SKILL_REFERENCES_DIR)), vec!["team-notes.md"]);
        assert_eq!(file_names(&claude), vec!["SKILL.md.bak"]);
    }

    #[test]
    fn clear_finishes_a_skill_whose_skill_md_is_already_gone() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path().join("home");
        let project = tmp.path().join("demo");
        fs::create_dir_all(&home).unwrap();
        let cursor = skill_dir(&project, ".cursor");
        install_skill(&cursor);
        fs::remove_file(cursor.join("SKILL.md")).unwrap();

        let result = clear_ai_setup_at(project.to_str().unwrap(), &home);
        assert!(result.errors.is_empty(), "{:?}", result.errors);
        assert!(result.skills_removed.is_empty());
        assert_eq!(result.skill_references_removed.len(), SKILL_REFERENCE_FILES.len());
        assert!(!cursor.exists());
    }

    #[cfg(unix)]
    #[test]
    fn clear_deletes_nothing_through_a_symlinked_skill_folder() {
        use std::os::unix::fs::symlink;
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path().join("home");
        let project = tmp.path().join("demo");
        fs::create_dir_all(&home).unwrap();
        // A skill folder linked to a checkout's template...
        let template = tmp.path().join("toolkit").join("skills").join("unity-open-mcp");
        install_skill(&template);
        let agents = skill_dir(&project, ".agents");
        fs::create_dir_all(agents.parent().unwrap()).unwrap();
        symlink(&template, &agents).unwrap();
        // ...and a real skill folder whose references are linked elsewhere.
        let shared = tmp.path().join("shared-references");
        fs::create_dir_all(&shared).unwrap();
        fs::write(shared.join(SKILL_REFERENCE_FILES[0]), "shared").unwrap();
        let claude = skill_dir(&project, ".claude");
        fs::create_dir_all(&claude).unwrap();
        fs::write(claude.join("SKILL.md"), "# skill").unwrap();
        symlink(&shared, claude.join(SKILL_REFERENCES_DIR)).unwrap();

        let result = clear_ai_setup_at(project.to_str().unwrap(), &home);
        assert!(result.errors.is_empty(), "{:?}", result.errors);
        assert!(result.skill_references_removed.is_empty());
        assert_eq!(result.skills_removed, vec![claude.join("SKILL.md").to_string_lossy().into_owned()]);
        assert_eq!(file_names(&template).len(), 2);
        assert_eq!(file_names(&template.join(SKILL_REFERENCES_DIR)).len(), SKILL_REFERENCE_FILES.len());
        assert!(shared.join(SKILL_REFERENCE_FILES[0]).is_file());
        assert!(fs::symlink_metadata(claude.join(SKILL_REFERENCES_DIR)).unwrap().file_type().is_symlink());
    }
}
