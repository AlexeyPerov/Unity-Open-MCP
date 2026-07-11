//! "Clear AI Setup" — the destructive inverse of the wizard.
//!
//! Removes every artifact the wizard (Steps 3 / 4 / 4b) writes for a
//! given project, best-effort with `.bak` backups first:
//!
//! - `Packages/manifest.json` — strips the bridge + verify package ids.
//! - MCP client configs (project-scoped unconditionally; global files
//!   only the entry whose `UNITY_PROJECT_PATH` matches this project).
//! - Agent-skill `SKILL.md` files for the four known client-relative
//!   skill dirs.
//!
//! Claude Code (CLI-only) and Manual have no on-disk artifact and are
//! reported as N/A rather than errors. Everything else that fails
//! (missing file, unreadable JSON, permission denied) is collected
//! into `errors` so a partial clear still succeeds for the parts that
//! could be removed.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

use crate::config::paths;
use super::mcp_config::{
    cline_settings_path, client_format, client_is_global, merge_key_path, ClientFormat,
    ClientScope, McpClientId, MCP_SERVER_KEY,
};
use super::wizard::{claude_desktop_config_path, BRIDGE_PACKAGE_ID, VERIFY_PACKAGE_ID};
use crate::config::constants::PROJECT_PATH_ENV_VAR;

/// Every client-relative skill path the wizard can copy. Mirrors
/// `skills/client-paths.json` so clear runs without a toolkit root;
/// keep this list in sync if the manifest ever grows a new target.
/// Generated from the catalog so a new client is picked up here for free.
const SKILL_REL_PATHS: &[&str] = &[
    ".cursor/skills/unity-open-mcp/SKILL.md",
    ".claude/skills/unity-open-mcp/SKILL.md",
    ".opencode/skills/unity-open-mcp/SKILL.md",
    ".agents/skills/unity-open-mcp/SKILL.md",
    ".cline/skills/unity-open-mcp/SKILL.md",
    ".gemini/skills/unity-open-mcp/SKILL.md",
    ".kilocode/skills/unity-open-mcp/SKILL.md",
    ".roo/skills/unity-open-mcp/SKILL.md",
    ".agent/skills/unity-open-mcp/SKILL.md",
    ".junie/skills/unity-open-mcp/SKILL.md",
    ".vscode/skills/unity-open-mcp/SKILL.md",
    ".vs/skills/unity-open-mcp/SKILL.md",
    ".github/skills/unity-open-mcp/SKILL.md",
];

/// `(client, scope, target_path)` triples the clear pass visits. The
/// `scope` carries the global-vs-project distinction so we know when
/// the `UNITY_PROJECT_PATH` guard must apply.
struct ClearTarget {
    client: McpClientId,
    scope: ClientScope,
    path: PathBuf,
}

fn is_global_scope(scope: ClientScope) -> bool {
    matches!(scope, ClientScope::Global)
}

/// Resolve every client config target the writer knows about, derived
/// from the catalog. Cursor is special-cased (both global and project
/// scopes are visited); every other file-backed client visits its
/// single canonical scope. CLI-only (`ClaudeCode`) and clipboard-only
/// (`Manual`/`Custom`) clients are intentionally absent — they have no
/// backing file.
fn all_client_targets(project_path: &str, home: &Path) -> Vec<ClearTarget> {
    let project = PathBuf::from(project_path);
    let mut out = Vec::new();
    // Every file-backed client except Cursor (which has two scopes).
    for client in FILE_BACKED_CLIENTS {
        if *client == McpClientId::Cursor {
            continue;
        }
        let scope = if client_is_global(*client) {
            ClientScope::Global
        } else {
            ClientScope::Project
        };
        if let Some(path) = resolve_clear_path(client, scope, &project, home) {
            out.push(ClearTarget {
                client: *client,
                scope,
                path,
            });
        }
    }
    // Cursor visits both scopes so a global write and a project write
    // are both cleared in one pass.
    if let Some(global) = resolve_clear_path(&McpClientId::Cursor, ClientScope::Global, &project, home)
    {
        out.push(ClearTarget {
            client: McpClientId::Cursor,
            scope: ClientScope::Global,
            path: global,
        });
    }
    if let Some(proj) = resolve_clear_path(&McpClientId::Cursor, ClientScope::Project, &project, home)
    {
        out.push(ClearTarget {
            client: McpClientId::Cursor,
            scope: ClientScope::Project,
            path: proj,
        });
    }
    out
}

/// Every file-backed client in the catalog. Kept as a const slice so the
/// clear pass enumerates them without re-deriving from `McpClientId`.
const FILE_BACKED_CLIENTS: &[McpClientId] = &[
    McpClientId::Cursor,
    McpClientId::ClaudeDesktop,
    McpClientId::OpencodeGlobal,
    McpClientId::OpencodeProject,
    McpClientId::ZcodeGlobal,
    McpClientId::ZcodeProject,
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
];

/// Resolve the on-disk path for a clear target. Mirrors
/// `mcp_config::resolve_target_path` but lives here so clear runs
/// without importing the writer's full surface. The two must stay in
/// sync — a new client added to the writer must appear here too.
fn resolve_clear_path(
    client: &McpClientId,
    scope: ClientScope,
    project: &Path,
    home: &Path,
) -> Option<PathBuf> {
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
        // CLI / clipboard-only clients have no file target.
        McpClientId::ClaudeCode | McpClientId::Manual | McpClientId::Custom => None,
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
    /// Project-relative skill paths that were deleted.
    pub skills_removed: Vec<String>,
    /// Non-fatal errors encountered (missing files are NOT errors).
    pub errors: Vec<String>,
}

/// Label for a scope, matching the wizard's "Cursor (global)" style.
fn scope_label(client: McpClientId, scope: ClientScope) -> String {
    let name = match client {
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
    let suffix = if is_global_scope(scope) { "global" } else { "project" };
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

/// Path equality tolerant of trailing slashes / redundant separators.
fn same_path(a: &str, b: &str) -> bool {
    fn norm(p: &str) -> String {
        p.trim_end_matches('/').trim_end_matches('\\').to_string()
    }
    norm(a) == norm(b)
}

/// Remove the `unity-open-mcp` leaf at `key_path` from `root` when it
/// matches the project (global files) or unconditionally (project files).
/// Returns `true` when a removal happened. Also prunes now-empty parent
/// containers so a cleared project file does not leave `{"mcp":{"servers":{}}}`.
fn remove_entry(root: &mut Value, key_path: &[&str], project_path: &str, guard: bool) -> bool {
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
    let take = match obj.get(leaf) {
        Some(entry) => !guard || entry_matches_project(entry, project_path),
        None => false,
    };
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

/// Atomic write mirroring `mcp_config.rs`: tmp + rename. Writes
/// pretty JSON with a trailing newline to match the writer's output.
fn write_json_atomic(target: &Path, value: &Value) -> std::io::Result<()> {
    let tmp_path = match target.extension().and_then(|e| e.to_str()) {
        Some(ext) => target.with_extension(format!("{ext}.tmp")),
        None => target.with_extension("tmp"),
    };
    let pretty = match serde_json::to_string_pretty(value) {
        Ok(s) => s + "\n",
        Err(e) => return Err(std::io::Error::other(e)),
    };
    {
        let mut tmp = fs::File::create(&tmp_path)?;
        tmp.write_all(pretty.as_bytes())?;
        tmp.sync_all().ok();
    }
    fs::rename(&tmp_path, target)
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
    let label = scope_label(target.client, target.scope);
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
    let guard = is_global_scope(target.scope);
    let removed = remove_entry(&mut value, &key_path, project_path, guard);
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
    let backed_up = match backup(&target.path) {
        Ok(b) => {
            let p = b.to_string_lossy().into_owned();
            match write_json_atomic(&target.path, &value) {
                Ok(()) => true,
                Err(e) => {
                    result.errors.push(format!(
                        "{label}: failed to write {}: {e}",
                        target.path.display()
                    ));
                    let _ = &p;
                    false
                }
            }
        }
        Err(e) => {
            result.errors.push(format!(
                "{label}: cannot create backup at {}: {e}",
                target.path.display()
            ));
            return;
        }
    };
    result.client_configs_cleared.push(ClearedClientConfig {
        label: label.to_string(),
        path: target.path.to_string_lossy().into_owned(),
        removed: true,
        backed_up,
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
    let guard = is_global_scope(target.scope);
    let removed = remove_toml_entry(&mut root, project_path, guard);
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
    let backed_up = match backup(&target.path) {
        Ok(b) => {
            let p = b.to_string_lossy().into_owned();
            let body = toml::to_string_pretty(&toml::Value::Table(root.clone()))
                .unwrap_or_default();
            match write_text_atomic(&target.path, &body) {
                Ok(()) => true,
                Err(e) => {
                    result.errors.push(format!(
                        "{label}: failed to write {}: {e}",
                        target.path.display()
                    ));
                    let _ = &p;
                    false
                }
            }
        }
        Err(e) => {
            result.errors.push(format!(
                "{label}: cannot create backup at {}: {e}",
                target.path.display()
            ));
            return;
        }
    };
    result.client_configs_cleared.push(ClearedClientConfig {
        label: label.to_string(),
        path: target.path.to_string_lossy().into_owned(),
        removed: true,
        backed_up,
    });
}

/// `true` when a TOML `[mcp_servers.<name>]` entry carries an
/// `env.UNITY_PROJECT_PATH` matching `project_path`. Absent field →
/// treat as belonging to this project (clear it).
fn toml_entry_matches_project(entry: &toml::Value, project_path: &str) -> bool {
    let Some(env) = entry.get("env").and_then(|e| e.as_table()) else {
        return true;
    };
    match env.get(PROJECT_PATH_ENV_VAR).and_then(|v| v.as_str()) {
        Some(p) => same_path(p, project_path),
        None => true,
    }
}

/// Remove the `[mcp_servers.unity-open-mcp]` table from `root` when it
/// matches the project (global files) or unconditionally (project files).
/// Returns `true` when a removal happened.
fn remove_toml_entry(root: &mut toml::value::Table, project_path: &str, guard: bool) -> bool {
    let Some(servers_val) = root.get_mut("mcp_servers") else {
        return false;
    };
    let toml::Value::Table(servers) = servers_val else {
        return false;
    };
    let take = match servers.get(MCP_SERVER_KEY) {
        Some(entry) => !guard || toml_entry_matches_project(entry, project_path),
        None => false,
    };
    if take {
        servers.remove(MCP_SERVER_KEY);
        true
    } else {
        false
    }
}

/// Atomic text write (tmp + rename), mirroring `write_json_atomic`.
fn write_text_atomic(target: &Path, body: &str) -> std::io::Result<()> {
    let tmp_path = match target.extension().and_then(|e| e.to_str()) {
        Some(ext) => target.with_extension(format!("{ext}.tmp")),
        None => target.with_extension("tmp"),
    };
    {
        let mut tmp = fs::File::create(&tmp_path)?;
        tmp.write_all(body.as_bytes())?;
        tmp.sync_all().ok();
    }
    fs::rename(&tmp_path, target)
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

/// Delete the four known skill files (and their now-empty
/// `unity-open-mcp/` parent when empty).
fn clear_skills(project_path: &str, result: &mut ClearAiSetupResult) {
    let project = Path::new(project_path);
    for rel in SKILL_REL_PATHS {
        let skill = project.join(rel);
        if !skill.is_file() {
            continue;
        }
        if let Err(e) = fs::remove_file(&skill) {
            result
                .errors
                .push(format!("skill: cannot remove {}: {e}", skill.display()));
            continue;
        }
        result.skills_removed.push(rel.to_string());
        // Best-effort cleanup of the now-empty leaf folder + the
        // `skills/` and client dir parents. Errors here are ignored.
        if let Some(unity_dir) = skill.parent() {
            if unity_dir.read_dir().map(|mut d| d.next().is_none()).unwrap_or(false) {
                let _ = fs::remove_dir(unity_dir);
            }
        }
    }
}

/// Non-Tauri entry point; testable without spinning up the command surface.
pub fn clear_ai_setup_at(project_path: &str, home: &Path) -> ClearAiSetupResult {
    let mut result = ClearAiSetupResult::default();
    if project_path.trim().is_empty() {
        result.errors.push("projectPath is empty.".to_string());
        return result;
    }
    clear_manifest(project_path, &mut result);
    for target in all_client_targets(project_path, home) {
        clear_client_target(&target, project_path, &mut result);
    }
    clear_skills(project_path, &mut result);
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
            "args": ["-y", "unity-open-mcp@latest"],
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
        let removed = remove_entry(&mut root, &key, "/p/demo", true);
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
        let removed = remove_entry(&mut root, &key, "/p/demo", true);
        assert!(!removed);
        assert_eq!(
            root["mcpServers"]["unity-open-mcp"]["env"]["UNITY_PROJECT_PATH"],
            "/p/other"
        );
    }

    #[test]
    fn remove_entry_project_scope_unconditional() {
        let mut root = json!({
            "mcp": { "servers": { "unity-open-mcp": entry("/p/other") } }
        });
        let key = vec!["mcp", "servers", MCP_SERVER_KEY];
        let removed = remove_entry(&mut root, &key, "/p/demo", false);
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
        assert!(remove_entry(&mut root, &key, "/p/demo", true));
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
}
