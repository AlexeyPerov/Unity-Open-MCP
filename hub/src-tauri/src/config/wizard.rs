//! M4 Plan 3 — wizard Step 1 detect + Step 3 manifest merge.
//!
//! This module owns the **read-side** of the wizard Steps 1 and 3:
//!
//! - [`detect_project_state`] — reads `ProjectVersion.txt`,
//!   `Packages/manifest.json`, and the known MCP client config
//!   files. Returns the same fields Step 1 and the Done screen
//!   render so the live detection state can be reused without a
//!   second disk pass (per `hub-wizard.md` §Step 1 and §Done).
//! - [`read_manifest`] / [`plan_manifest_merge`] /
//!   [`write_manifest_merge`] — non-destructive
//!   `Packages/manifest.json` merge. The merge logic treats an
//!   exact URL+tag match as a no-op (questions-4 Q5 = A) and
//!   requires an explicit `confirm_upgrades` flag before touching
//!   an existing entry whose URL or tag differs (questions-4 Q5 = B).
//!   Unrelated `dependencies` and other top-level keys are never
//!   rewritten.
//!
//! Bridge status (`/ping`) is intentionally **not** checked here;
//!   the spec defers that to Step 5 (Plan 5) and the Step 1 column
//!   surfaces `"notChecked"`.

use std::collections::BTreeMap;
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

/// M4: Unity minimum major version. The bridge package requires
/// Unity 6 (per `packages/bridge.md` §UPM identity) and the wizard
/// hard-blocks below this threshold (questions-4 Q10 = A).
pub const MIN_UNITY_MAJOR: u32 = 6000;

/// UPM package id for the bridge package. Used by detect (is the
/// package already installed?) and by the merge (target entry).
pub const BRIDGE_PACKAGE_ID: &str = "com.alexeyperov.unity-agent-bridge";

/// UPM package id for the verify package.
pub const VERIFY_PACKAGE_ID: &str = "com.alexeyperov.unity-agent-verify";

/// Default git remote used when the toolkit root has no `.git`
/// directory or no `[remote "origin"]` block (e.g. a downloaded
/// zip). Mirrors the canonical repository URL referenced in
/// `packages/bridge.md` and `packages/verify.md` §Install.
pub const DEFAULT_GIT_REMOTE: &str = "https://github.com/AlexeyPerov/Unity-AI-Hub.git";

/// Default git tag pins for each package. Used when the user does
/// not override via the Step 3 "Package version pin" field.
pub const DEFAULT_BRIDGE_TAG: &str = "bridge-v1.0.0";
pub const DEFAULT_VERIFY_TAG: &str = "verify-v1.0.0";

/// Per-package default install (relative path inside the
/// monorepo + default tag pin).
pub const BRIDGE_PACKAGE_PATH: &str = "packages/bridge";
pub const VERIFY_PACKAGE_PATH: &str = "packages/verify";

/// Sub-path fragment appended to the monorepo remote to install
/// a single package. UPM `path=` URL form per the bridge / verify
/// install examples.
pub const PATH_QUERY_PREFIX: &str = "?path=";
pub const TAG_FRAGMENT_PREFIX: &str = "#";

/// Bridge status reported by Step 1 / detect and consumed by the
/// Done screen. The detect snapshot always reports `NotChecked`
/// (the `detect_project_state` command does not perform a `/ping`)
/// — the actual `/ping` result lives in the wizard's own
/// `Step 5` state and is rendered next to the live detect copy
/// on Done.
///
/// The variants are kept in lock-step with the Step 5 ping
/// surface defined in [`crate::config::launch_verify`]. The
/// `Ok` variant carries the structured fields the bridge
/// `/ping` JSON advertises so the Done screen can render
/// `connected`, `compiling`, and project-path chips without
/// having to keep a second copy of the data in the frontend.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum BridgeStatusKind {
    /// No `/ping` has been run in this wizard session. Step 1
    /// and the detect-driven Done row render this as "Not checked".
    NotChecked,
    /// Bridge `/ping` responded 200 with a parseable body.
    #[serde(rename_all = "camelCase")]
    Ok {
        /// `true` when the bridge reports the connector is live.
        connected: bool,
        /// Bridge-reported project path (when present).
        #[serde(default)]
        project_path: Option<String>,
        /// `true` while Unity is mid-compile. The Done screen
        /// surfaces this as a "compiling" hint so the user can
        /// tell compile errors from a hung bridge.
        compiling: bool,
        /// `true` when the Editor is in play mode.
        is_playing: bool,
    },
    /// Bridge `/ping` failed — connection refused, timeout,
    /// non-200 status, malformed body, or compile error. The
    /// Done chip renders this as "failed" with the message
    /// surfaced inline.
    #[serde(rename_all = "camelCase")]
    Failed { message: String },
}

/// Per-client MCP configuration heuristic. Each flag is `true`
/// when a known client config file exists on disk and contains a
/// `unity-agent` MCP server entry. Used by Step 1 to surface
/// "MCP configured?" and by the Done screen checklist.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpConfigHeuristic {
    pub cursor: bool,
    pub claude_desktop: bool,
    pub opencode_global: bool,
    pub opencode_project: bool,
}

impl McpConfigHeuristic {
    /// `true` when any known client has a `unity-agent` entry.
    pub fn any(&self) -> bool {
        self.cursor || self.claude_desktop || self.opencode_global || self.opencode_project
    }
}

/// Full Step 1 detection snapshot. Mirrors the `hub-wizard.md`
/// §Step 1 UI table: project name, path, Unity version, bridge /
/// verify install flags, MCP heuristic, and bridge status (always
/// `NotChecked` from this command). The Done screen re-reads the
/// same command on entry so its checklist matches the latest
/// on-disk state without persisting anything in `projects.json`.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectState {
    /// Absolute path that was inspected.
    pub path: String,
    /// Folder name (last path segment).
    pub name: String,
    /// `true` when the path contains both `Assets/` and
    /// `ProjectSettings/`. The wizard blocks invalid project
    /// paths in Step 1 (`hub-wizard.md` §Step 1 Notes).
    pub is_valid_unity_project: bool,
    /// Raw string from `ProjectSettings/ProjectVersion.txt`
    /// (e.g. `"6000.0.1f1"`). `None` when the file is missing.
    pub unity_version: Option<String>,
    /// `true` when `unity_version` is `Some` and parses to a
    /// major version at or above `MIN_UNITY_MAJOR` (6000).
    pub meets_min_unity_version: bool,
    /// `true` when `Packages/manifest.json` exists (even if it
    /// fails to parse — that case is surfaced separately by the
    /// Step 3 read command).
    pub manifest_present: bool,
    /// `true` when the manifest's `dependencies` contains the
    /// bridge package id.
    pub bridge_installed: bool,
    /// `true` when the manifest's `dependencies` contains the
    /// verify package id.
    pub verify_installed: bool,
    /// Per-client heuristic for whether a `unity-agent` MCP
    /// server entry is already configured.
    pub mcp_configured: McpConfigHeuristic,
    /// `true` when the wizard believes `Packages/manifest.json`
    /// can be written to. `false` when the file is read-only or
    /// the parent directory is not user-writable — Step 2 hard
    /// blocks on this. (See `hub-wizard.md` §Step 2 Checks.)
    pub manifest_writable: bool,
    /// `true` when any path segment contains a space. Step 2
    /// surfaces this as a **warning** only (`hub-wizard.md`
    /// §Step 2 Checks, "recommended" row).
    pub has_spaces_in_path: bool,
    /// Always `NotChecked` in M4; the Step 5 `/ping` verification
    /// rewrites this on Done entry.
    pub bridge_status: BridgeStatusKind,
}

/// Result of reading `Packages/manifest.json` for the wizard.
/// `parse_error` is populated when the file exists but cannot be
/// parsed — the wizard blocks Step 3 in that case so the user
/// can fix the manifest by hand before any merge attempt.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestRead {
    pub project_path: String,
    pub present: bool,
    pub readable: bool,
    pub parse_error: Option<String>,
    /// The raw top-level keys preserved verbatim from the file
    /// (in file order when serde_json's `preserve_order` feature
    /// is on, alphabetical otherwise). Used by the merge writer
    /// to re-emit the file without reformatting unrelated keys.
    pub raw: Option<Value>,
    /// Flat `dependencies` view keyed by package id, value being
    /// the UPM URL spec (e.g. `"file:../../packages/bridge"` or
    /// `"https://github.com/...#tag"`).
    pub dependencies: BTreeMap<String, String>,
}

/// A single package's derived install URL. The wizard Step 3
/// preview surfaces these so the user can see what will be
/// written.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PackageInstallEntry {
    pub id: String,
    pub url: String,
    /// Tag pin, when present. Empty when the URL has no `#tag`
    /// fragment. The wizard surfaces the tag separately so the
    /// "exact match" comparison can ignore URL-encoded differences.
    pub tag: String,
    /// Relative path inside the monorepo (e.g.
    /// `"packages/bridge"`). Empty when the URL is a custom
    /// override that does not use the `?path=` form.
    pub package_path: String,
}

/// The two package URLs the wizard always derives from a
/// validated toolkit root (questions-4 Q2 = B; spec §Step 3
/// "Package URL derivation").
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DerivedPackageUrls {
    pub toolkit_root: String,
    pub git_remote: String,
    pub bridge: PackageInstallEntry,
    pub verify: PackageInstallEntry,
}

/// A single change the wizard intends to apply. Used by the
/// preview UI and by the writer.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PackageChange {
    pub id: String,
    pub before: Option<String>,
    pub after: String,
    /// `"add"` — dependency missing entirely.
    /// `"upgrade"` — present with a different URL or tag.
    /// `"unchanged"` — present with the exact URL+tag.
    pub kind: ChangeKind,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ChangeKind {
    Add,
    Upgrade,
    Unchanged,
}

/// Inputs to the merge planner / writer. Mirrors the Step 3 form
/// state: per-package install flags, optional version pin, and
/// optional custom git URL.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestMergeParams {
    pub project_path: String,
    pub toolkit_root: String,
    pub install_bridge: bool,
    pub install_verify: bool,
    /// Optional tag pin applied to **both** packages (e.g.
    /// `"bridge-v1.1.0"`). The wizard exposes a single
    /// version-pin field; per-package pinning is not in M4.
    #[serde(default)]
    pub version_pin: String,
    /// Optional custom git URL. When non-empty, this URL is used
    /// as the base remote for both packages instead of the
    /// toolkit root's remote — useful for dev builds against a
    /// fork. Per-package URLs are not in M4.
    #[serde(default)]
    pub custom_url: String,
    /// Explicit user confirmation for any upgrade. The writer
    /// refuses to apply an upgrade when this is `false`.
    #[serde(default)]
    pub confirm_upgrades: bool,
}

/// The plan returned by [`plan_manifest_merge`]. Includes the
/// per-package change classification, the derived URLs, and the
/// would-be `dependencies` block — the UI uses all three to
/// render the diff preview and the upgrade confirmation prompt.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestMergePlan {
    pub project_path: String,
    pub derived_urls: DerivedPackageUrls,
    pub changes: Vec<PackageChange>,
    pub proposed_dependencies: BTreeMap<String, String>,
    pub manifest_read: ManifestRead,
    pub has_upgrades: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestWriteResult {
    pub project_path: String,
    pub manifest_path: String,
    /// Absolute path to the pre-write backup
    /// (`Packages/manifest.json.bak`). Empty when no backup was
    /// created (no existing file).
    pub backup_path: String,
    pub changes: Vec<PackageChange>,
    pub dependencies: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestError {
    pub kind: String,
    pub message: String,
}

impl ManifestError {
    fn new(kind: &str, message: impl Into<String>) -> Self {
        Self {
            kind: kind.to_string(),
            message: message.into(),
        }
    }
}

/// Tauri command: read the full Step 1 detection snapshot for a
/// project. Pure read; does not mutate `projects.json` or the
/// project's manifest. The wizard Step 1 calls this on mount and
/// the Done screen calls it again on entry so its checklist
/// always reflects the latest on-disk state.
#[tauri::command]
pub fn detect_project_state(project_path: String) -> ProjectState {
    detect_project_state_at(&PathBuf::from(&project_path))
}

/// Tauri command: read and parse `Packages/manifest.json`.
/// Returns a [`ManifestRead`] with the raw value, the flat
/// `dependencies` map, and (if the file exists but cannot be
/// parsed) a `parse_error` string the UI surfaces inline.
///
/// No file is written.
#[tauri::command]
pub fn read_manifest(project_path: String) -> ManifestRead {
    let project = PathBuf::from(&project_path);
    let manifest_path = project.join("Packages").join("manifest.json");
    if !manifest_path.exists() {
        return ManifestRead {
            project_path: project_path.clone(),
            present: false,
            readable: false,
            parse_error: None,
            raw: None,
            dependencies: BTreeMap::new(),
        };
    }
    let content = match fs::read_to_string(&manifest_path) {
        Ok(s) => s,
        Err(e) => {
            return ManifestRead {
                project_path: project_path.clone(),
                present: true,
                readable: false,
                parse_error: Some(format!("cannot read manifest: {}", e)),
                raw: None,
                dependencies: BTreeMap::new(),
            };
        }
    };
    let value: Value = match serde_json::from_str(&content) {
        Ok(v) => v,
        Err(e) => {
            return ManifestRead {
                project_path: project_path.clone(),
                present: true,
                readable: true,
                parse_error: Some(format!("invalid JSON: {}", e)),
                raw: None,
                dependencies: BTreeMap::new(),
            };
        }
    };
    let dependencies = extract_dependencies(&value);
    ManifestRead {
        project_path: project_path.clone(),
        present: true,
        readable: true,
        parse_error: None,
        raw: Some(value),
        dependencies,
    }
}

/// Tauri command: compute a merge plan without writing anything.
/// The wizard Step 3 calls this whenever the form state changes
/// so the diff preview is always live.
#[tauri::command]
pub fn plan_manifest_merge(params: ManifestMergeParams) -> ManifestMergePlan {
    let read = read_manifest_inner(&PathBuf::from(&params.project_path));
    let derived = derive_package_urls(
        &params.toolkit_root,
        &params.version_pin,
        &params.custom_url,
    );
    let mut changes = Vec::new();
    let mut proposed = BTreeMap::new();
    if params.install_bridge {
        let entry = &derived.bridge;
        let kind = classify(&read.dependencies, &entry.id, &entry.url, &entry.tag);
        changes.push(PackageChange {
            id: entry.id.clone(),
            before: read.dependencies.get(&entry.id).cloned(),
            after: entry.url.clone(),
            kind,
        });
        proposed.insert(entry.id.clone(), entry.url.clone());
    }
    if params.install_verify {
        let entry = &derived.verify;
        let kind = classify(&read.dependencies, &entry.id, &entry.url, &entry.tag);
        changes.push(PackageChange {
            id: entry.id.clone(),
            before: read.dependencies.get(&entry.id).cloned(),
            after: entry.url.clone(),
            kind,
        });
        proposed.insert(entry.id.clone(), entry.url.clone());
    }
    // Carry over existing entries the wizard is not managing so
    // the preview reflects the full `dependencies` block.
    for (k, v) in &read.dependencies {
        proposed.entry(k.clone()).or_insert_with(|| v.clone());
    }
    let has_upgrades = changes.iter().any(|c| c.kind == ChangeKind::Upgrade);
    ManifestMergePlan {
        project_path: params.project_path,
        derived_urls: derived,
        changes,
        proposed_dependencies: proposed,
        manifest_read: read,
        has_upgrades,
    }
}

/// Tauri command: apply a merge plan. Refuses when:
/// - the manifest cannot be parsed (`parse_error` is set);
/// - the project path is not a valid Unity project;
/// - any change is an upgrade and `confirm_upgrades` is `false`.
///
/// On success: writes the manifest atomically, leaves a
/// `manifest.json.bak` next to the original (when one existed),
/// and returns a [`ManifestWriteResult`] with the new
/// `dependencies` map.
#[tauri::command]
pub fn write_manifest_merge(
    params: ManifestMergeParams,
) -> Result<ManifestWriteResult, ManifestError> {
    let project = PathBuf::from(&params.project_path);
    let assets = project.join("Assets");
    if !project.is_dir() || !assets.is_dir() {
        return Err(ManifestError::new(
            "notAUnityProject",
            "Project path is not a valid Unity project (missing Assets/).",
        ));
    }
    let manifest_path = project.join("Packages").join("manifest.json");
    let read = read_manifest_inner(&project);
    if read.parse_error.is_some() {
        return Err(ManifestError::new(
            "invalidJson",
            read.parse_error.unwrap_or_else(|| "invalid JSON".to_string()),
        ));
    }
    let derived = derive_package_urls(
        &params.toolkit_root,
        &params.version_pin,
        &params.custom_url,
    );
    let mut changes = Vec::new();
    let mut proposed = BTreeMap::new();
    if params.install_bridge {
        let entry = &derived.bridge;
        let kind = classify(&read.dependencies, &entry.id, &entry.url, &entry.tag);
        if kind == ChangeKind::Upgrade && !params.confirm_upgrades {
            return Err(ManifestError::new(
                "upgradeNotConfirmed",
                format!(
                    "Existing {} entry differs from the proposed value; set confirm_upgrades to apply.",
                    entry.id
                ),
            ));
        }
        changes.push(PackageChange {
            id: entry.id.clone(),
            before: read.dependencies.get(&entry.id).cloned(),
            after: entry.url.clone(),
            kind,
        });
        proposed.insert(entry.id.clone(), entry.url.clone());
    }
    if params.install_verify {
        let entry = &derived.verify;
        let kind = classify(&read.dependencies, &entry.id, &entry.url, &entry.tag);
        if kind == ChangeKind::Upgrade && !params.confirm_upgrades {
            return Err(ManifestError::new(
                "upgradeNotConfirmed",
                format!(
                    "Existing {} entry differs from the proposed value; set confirm_upgrades to apply.",
                    entry.id
                ),
            ));
        }
        changes.push(PackageChange {
            id: entry.id.clone(),
            before: read.dependencies.get(&entry.id).cloned(),
            after: entry.url.clone(),
            kind,
        });
        proposed.insert(entry.id.clone(), entry.url.clone());
    }
    for (k, v) in &read.dependencies {
        proposed.entry(k.clone()).or_insert_with(|| v.clone());
    }
    // Read or initialize the manifest value. When the file does
    // not exist we start from a minimal valid manifest. When it
    // exists, we preserve every key the wizard is not managing
    // (scopedRegistries, testables, custom fields, …).
    let mut value: Value = read
        .raw
        .clone()
        .unwrap_or_else(|| json!({ "dependencies": {} }));
    if !value.is_object() {
        return Err(ManifestError::new(
            "invalidJson",
            "manifest root is not a JSON object",
        ));
    }
    let deps_value = value
        .as_object_mut()
        .unwrap()
        .entry("dependencies".to_string())
        .or_insert_with(|| json!({}));
    if !deps_value.is_object() {
        return Err(ManifestError::new(
            "invalidJson",
            "manifest `dependencies` is not a JSON object",
        ));
    }
    let deps_map = deps_value.as_object_mut().unwrap();
    for change in &changes {
        if matches!(change.kind, ChangeKind::Unchanged) {
            continue;
        }
        deps_map.insert(change.id.clone(), Value::String(change.after.clone()));
    }

    // Backup the existing file (if any) before writing, but
    // only when at least one change actually mutates the file.
    // A pure "already installed" run should not create an
    // identical `.bak` next to the manifest.
    let has_real_change = changes.iter().any(|c| c.kind != ChangeKind::Unchanged);
    let mut backup_path = String::new();
    if has_real_change && manifest_path.exists() {
        let candidate = project
            .join("Packages")
            .join("manifest.json.bak");
        // Overwrite any prior backup — the wizard owns this file
        // and the previous backup has already served its purpose.
        let _ = fs::copy(&manifest_path, &candidate);
        backup_path = candidate.to_string_lossy().into_owned();
    }

    if !has_real_change {
        // Nothing to write — return a no-op result so the UI
        // surfaces a clean "already installed" message.
        return Ok(ManifestWriteResult {
            project_path: params.project_path,
            manifest_path: manifest_path.to_string_lossy().into_owned(),
            backup_path,
            changes,
            dependencies: extract_dependencies(&value),
        });
    }

    // Ensure the Packages/ directory exists. A missing parent
    // surfaces a clear "no such file" error otherwise.
    if let Some(parent) = manifest_path.parent() {
        if !parent.exists() {
            fs::create_dir_all(parent).map_err(|e| {
                ManifestError::new(
                    "writeFailed",
                    format!("cannot create Packages/ folder: {}", e),
                )
            })?;
        }
    }

    // Atomic write: tmp + rename.
    let tmp_path = project
        .join("Packages")
        .join("manifest.json.tmp");
    let json_text = match serde_json::to_string_pretty(&value) {
        Ok(s) => s + "\n",
        Err(e) => {
            return Err(ManifestError::new(
                "serializeFailed",
                format!("failed to serialize manifest: {}", e),
            ));
        }
    };
    {
        let mut tmp = fs::File::create(&tmp_path).map_err(|e| {
            ManifestError::new("writeFailed", format!("cannot create tmp file: {}", e))
        })?;
        tmp.write_all(json_text.as_bytes()).map_err(|e| {
            ManifestError::new("writeFailed", format!("cannot write tmp file: {}", e))
        })?;
        tmp.sync_all().ok();
    }
    fs::rename(&tmp_path, &manifest_path).map_err(|e| {
        ManifestError::new("writeFailed", format!("cannot rename tmp to manifest: {}", e))
    })?;

    let dependencies = extract_dependencies(&value);
    Ok(ManifestWriteResult {
        project_path: params.project_path,
        manifest_path: manifest_path.to_string_lossy().into_owned(),
        backup_path,
        changes,
        dependencies,
    })
}

/// Build a [`ProjectState`] for the given project path. Exposed
/// as a non-Tauri helper so the tests can drive it without
/// spinning up the command surface.
pub fn detect_project_state_at(project: &Path) -> ProjectState {
    let path_string = project.to_string_lossy().into_owned();
    let name = project
        .file_name()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_default();
    let assets = project.join("Assets");
    let project_settings = project.join("ProjectSettings");
    let is_valid_unity_project =
        project.is_dir() && assets.is_dir() && project_settings.is_dir();

    let unity_version = read_unity_version(project);
    let meets_min = unity_version
        .as_deref()
        .map(meets_min_unity_version)
        .unwrap_or(false);

    let manifest_path = project.join("Packages").join("manifest.json");
    let manifest_present = manifest_path.exists();
    let (bridge_installed, verify_installed) = read_manifest_inner(project).dependencies.iter().fold(
        (false, false),
        |(b, v), (k, _)| {
            (
                b || k == BRIDGE_PACKAGE_ID,
                v || k == VERIFY_PACKAGE_ID,
            )
        },
    );

    let mcp_configured = read_mcp_heuristic(project);
    let manifest_writable = check_manifest_writable_at(&manifest_path);
    let has_spaces_in_path = project.to_string_lossy().contains(' ');

    ProjectState {
        path: path_string,
        name,
        is_valid_unity_project,
        unity_version,
        meets_min_unity_version: meets_min,
        manifest_present,
        bridge_installed,
        verify_installed,
        mcp_configured,
        manifest_writable,
        has_spaces_in_path,
        bridge_status: BridgeStatusKind::NotChecked,
    }
}

/// `true` when the Unity version string parses to a major at or
/// above `MIN_UNITY_MAJOR` (6000). Returns `false` for unparsable
/// strings — the spec treats those as "below minimum" so the
/// wizard blocks with a clear "could not parse" message.
pub fn meets_min_unity_version(raw: &str) -> bool {
    parse_unity_major_minor(raw)
        .map(|(major, _)| major >= MIN_UNITY_MAJOR)
        .unwrap_or(false)
}

fn parse_unity_major_minor(raw: &str) -> Option<(u32, u32)> {
    let trimmed = raw.trim().trim_start_matches('\u{FEFF}');
    let mut parts = trimmed.split('.');
    let major: u32 = parts.next()?.parse().ok()?;
    let minor_str: String = parts
        .next()?
        .chars()
        .take_while(|c| c.is_ascii_digit())
        .collect();
    let minor: u32 = if minor_str.is_empty() {
        0
    } else {
        minor_str.parse().ok()?
    };
    Some((major, minor))
}

fn read_unity_version(project: &Path) -> Option<String> {
    let version_file = project
        .join("ProjectSettings")
        .join("ProjectVersion.txt");
    let content = fs::read_to_string(&version_file).ok()?;
    for line in content.lines() {
        let line = line.strip_prefix('\u{FEFF}').unwrap_or(line);
        if let Some(version) = line.strip_prefix("m_EditorVersion:") {
            let trimmed = version.trim();
            if !trimmed.is_empty() {
                return Some(trimmed.to_string());
            }
        }
    }
    None
}

fn read_manifest_inner(project: &Path) -> ManifestRead {
    let project_path = project.to_string_lossy().into_owned();
    let manifest_path = project.join("Packages").join("manifest.json");
    if !manifest_path.exists() {
        return ManifestRead {
            project_path,
            present: false,
            readable: false,
            parse_error: None,
            raw: None,
            dependencies: BTreeMap::new(),
        };
    }
    let content = match fs::read_to_string(&manifest_path) {
        Ok(s) => s,
        Err(e) => {
            return ManifestRead {
                project_path,
                present: true,
                readable: false,
                parse_error: Some(format!("cannot read manifest: {}", e)),
                raw: None,
                dependencies: BTreeMap::new(),
            };
        }
    };
    let value: Value = match serde_json::from_str(&content) {
        Ok(v) => v,
        Err(e) => {
            return ManifestRead {
                project_path,
                present: true,
                readable: true,
                parse_error: Some(format!("invalid JSON: {}", e)),
                raw: None,
                dependencies: BTreeMap::new(),
            };
        }
    };
    let dependencies = extract_dependencies(&value);
    ManifestRead {
        project_path,
        present: true,
        readable: true,
        parse_error: None,
        raw: Some(value),
        dependencies,
    }
}

fn extract_dependencies(value: &Value) -> BTreeMap<String, String> {
    let mut out = BTreeMap::new();
    if let Some(deps) = value.get("dependencies").and_then(|v| v.as_object()) {
        for (k, v) in deps {
            if let Value::String(s) = v {
                out.insert(k.clone(), s.clone());
            }
        }
    }
    out
}

/// Derive the per-package install URLs from the validated
/// toolkit root, honoring the optional version pin and custom
/// git URL. The order of precedence matches `hub-wizard.md`
/// §Step 3 "Package URL derivation":
///
/// 1. Custom git URL (when non-empty) — replaces the remote.
/// 2. Toolkit root's `[remote "origin"]` URL (when present).
/// 3. [`DEFAULT_GIT_REMOTE`] — fallback for non-git checkouts.
pub fn derive_package_urls(
    toolkit_root: &str,
    version_pin: &str,
    custom_url: &str,
) -> DerivedPackageUrls {
    let trimmed_root = toolkit_root.trim();
    let trimmed_pin = version_pin.trim();
    let trimmed_custom = custom_url.trim();

    let remote = if !trimmed_custom.is_empty() {
        trimmed_custom.to_string()
    } else if let Some(origin) = read_git_origin(trimmed_root) {
        origin
    } else {
        DEFAULT_GIT_REMOTE.to_string()
    };

    let bridge = build_package_entry(
        BRIDGE_PACKAGE_ID,
        BRIDGE_PACKAGE_PATH,
        DEFAULT_BRIDGE_TAG,
        trimmed_pin,
        &remote,
    );
    let verify = build_package_entry(
        VERIFY_PACKAGE_ID,
        VERIFY_PACKAGE_PATH,
        DEFAULT_VERIFY_TAG,
        trimmed_pin,
        &remote,
    );

    DerivedPackageUrls {
        toolkit_root: trimmed_root.to_string(),
        git_remote: remote,
        bridge,
        verify,
    }
}

fn build_package_entry(
    id: &str,
    package_path: &str,
    default_tag: &str,
    version_pin: &str,
    remote: &str,
) -> PackageInstallEntry {
    let tag = if version_pin.is_empty() {
        default_tag.to_string()
    } else {
        version_pin.to_string()
    };
    let mut url = String::new();
    url.push_str(remote.trim_end_matches('/'));
    url.push_str(PATH_QUERY_PREFIX);
    url.push_str(package_path);
    url.push_str(TAG_FRAGMENT_PREFIX);
    url.push_str(&tag);
    PackageInstallEntry {
        id: id.to_string(),
        url,
        tag,
        package_path: package_path.to_string(),
    }
}

/// Read `[remote "origin"]` `url = ...` from the toolkit root's
/// `.git/config`. Returns `None` for missing or unparsable
/// files. The parser is intentionally minimal — it only needs
/// the first `[remote "origin"]` block, and it tolerates the
/// subset of INI syntax Git emits (section headers, `key =
/// value` lines, comments, blank lines, whitespace).
fn read_git_origin(toolkit_root: &str) -> Option<String> {
    if toolkit_root.is_empty() {
        return None;
    }
    let config_path = Path::new(toolkit_root).join(".git").join("config");
    let content = fs::read_to_string(&config_path).ok()?;
    let mut in_origin = false;
    for line in content.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() || trimmed.starts_with('#') || trimmed.starts_with(';') {
            continue;
        }
        if let Some(rest) = trimmed.strip_prefix('[') {
            if let Some(header) = rest.strip_suffix(']') {
                let header = header.trim().to_ascii_lowercase();
                in_origin = header == "remote \"origin\"";
            }
            continue;
        }
        if in_origin {
            if let Some((key, value)) = split_ini_kv(trimmed) {
                if key.eq_ignore_ascii_case("url") {
                    let v = value.trim().to_string();
                    if !v.is_empty() {
                        return Some(v);
                    }
                }
            }
        }
    }
    None
}

fn split_ini_kv(line: &str) -> Option<(&str, &str)> {
    let (key, value) = line.split_once('=')?;
    Some((key.trim(), value.trim()))
}

fn classify(
    existing: &BTreeMap<String, String>,
    id: &str,
    proposed_url: &str,
    proposed_tag: &str,
) -> ChangeKind {
    match existing.get(id) {
        None => ChangeKind::Add,
        Some(current) if urls_match(current, proposed_url, proposed_tag) => ChangeKind::Unchanged,
        Some(_) => ChangeKind::Upgrade,
    }
}

/// `true` when the existing dependency string and the proposed
/// URL produce the same effective install: same normalized
/// remote, same `path=` query value, and same tag pin.
/// Whitespace differences are tolerated; the host portion of the
/// remote is normalized (trailing `.git` and `/` stripped) so a
/// `https://github.com/...git` vs `https://github.com/...` does
/// not look like a remote switch.
fn urls_match(existing: &str, proposed_url: &str, _proposed_tag: &str) -> bool {
    let existing_normalized = existing.trim();
    let proposed_normalized = proposed_url.trim();
    let existing_tag = extract_tag(existing_normalized);
    let proposed_tag = extract_tag(proposed_normalized);
    if existing_tag != proposed_tag {
        return false;
    }
    let existing_path = extract_path_query(existing_normalized);
    let proposed_path = extract_path_query(proposed_normalized);
    if existing_path != proposed_path {
        return false;
    }
    let existing_remote = extract_remote(existing_normalized);
    let proposed_remote = extract_remote(proposed_normalized);
    existing_remote == proposed_remote
}

fn extract_remote(url: &str) -> String {
    let before_query = url.split('?').next().unwrap_or(url);
    let trimmed = before_query.trim_end_matches('/');
    let trimmed = trimmed.strip_suffix(".git").unwrap_or(trimmed);
    trimmed.to_ascii_lowercase()
}

fn extract_tag(url: &str) -> Option<String> {
    let (_, after_hash) = url.split_once('#')?;
    let tag = after_hash.split('&').next().unwrap_or("").trim();
    if tag.is_empty() {
        None
    } else {
        Some(tag.to_string())
    }
}

fn extract_path_query(url: &str) -> Option<String> {
    let (_, after_query) = url.split_once('?')?;
    let path = after_query
        .split('&')
        .find_map(|kv| kv.strip_prefix("path=").map(|s| s.to_string()))
        .or_else(|| {
            // Tolerate the `?path=foo#tag` form by stripping the
            // tag fragment from the query first.
            let before_hash = after_query.split('#').next().unwrap_or("");
            before_hash
                .split('&')
                .find_map(|kv| kv.strip_prefix("path=").map(|s| s.to_string()))
        });
    path
}

fn read_mcp_heuristic(project: &Path) -> McpConfigHeuristic {
    let home = match dirs::home_dir() {
        Some(h) => h,
        None => return McpConfigHeuristic::default(),
    };
    let cursor = contains_mcp_key(&home.join(".cursor").join("mcp.json"));
    let claude_desktop = contains_mcp_key(&claude_desktop_config_path(&home));
    let opencode_global = contains_mcp_key(&home.join(".config").join("opencode").join("opencode.json"));
    let opencode_project = contains_mcp_key(&project.join("opencode.json"));
    McpConfigHeuristic {
        cursor,
        claude_desktop,
        opencode_global,
        opencode_project,
    }
}

pub fn claude_desktop_config_path(home: &Path) -> PathBuf {
    if cfg!(target_os = "macos") {
        home.join("Library")
            .join("Application Support")
            .join("Claude")
            .join("claude_desktop_config.json")
    } else if cfg!(target_os = "windows") {
        // `%APPDATA%\Claude\claude_desktop_config.json` — dirs
        // resolves `config_dir` to that on Windows, but the
        // Claude folder is nested under it.
        dirs::config_dir()
            .unwrap_or_else(|| home.to_path_buf())
            .join("Claude")
            .join("claude_desktop_config.json")
    } else {
        // Linux: the documented Claude Desktop path is
        // `~/.config/Claude/claude_desktop_config.json`.
        home.join(".config")
            .join("Claude")
            .join("claude_desktop_config.json")
    }
}

/// `true` when the JSON file at `path` exists and contains a
/// `unity-agent` MCP server entry under either `mcpServers` or
/// `mcp`. Unparsable files are treated as "not configured" so a
/// malformed config does not falsely report as set up.
pub fn contains_mcp_key(path: &Path) -> bool {
    if !path.exists() {
        return false;
    }
    let Ok(content) = fs::read_to_string(path) else {
        return false;
    };
    let Ok(value) = serde_json::from_str::<Value>(&content) else {
        return false;
    };
    let Some(obj) = value.as_object() else {
        return false;
    };
    if let Some(servers) = obj.get("mcpServers").and_then(|v| v.as_object()) {
        if servers.contains_key("unity-agent") {
            return true;
        }
    }
    if let Some(mcp) = obj.get("mcp").and_then(|v| v.as_object()) {
        if mcp.contains_key("unity-agent") {
            return true;
        }
    }
    false
}

fn check_manifest_writable_at(manifest_path: &Path) -> bool {
    if let Some(parent) = manifest_path.parent() {
        if !parent.exists() {
            // The Packages/ folder is created on demand by the
            // writer; treat a missing parent as writable when
            // the project root itself is writable.
            if let Some(grand) = parent.parent() {
                return probe_writable(grand);
            }
            return false;
        }
    }
    if manifest_path.exists() {
        return probe_writable(manifest_path);
    }
    manifest_path
        .parent()
        .map(probe_writable)
        .unwrap_or(false)
}

fn probe_writable(path: &Path) -> bool {
    // Cheap cross-platform writable check: try to open the
    // directory (or file's parent) for write. The test in the
    // README documents this as "best effort" — the wizard
    // surfaces write failures again at write time so a
    // permission flip between Step 2 and Step 3 still surfaces a
    // clear error.
    if path.is_dir() {
        let probe = path.join(".hub-write-probe");
        let created = match fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&probe)
        {
            Ok(f) => f.sync_all().is_ok(),
            Err(_) => false,
        };
        if created {
            let _ = fs::remove_file(&probe);
        }
        return created;
    }
    if path.is_file() {
        return match fs::OpenOptions::new().write(true).open(path) {
            Ok(_) => true,
            Err(_) => false,
        };
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::Map;
    use std::fs;
    use tempfile::tempdir;

    fn make_valid_project(root: &Path) {
        fs::create_dir_all(root.join("Assets")).unwrap();
        fs::create_dir_all(root.join("ProjectSettings")).unwrap();
        fs::write(
            root.join("ProjectSettings").join("ProjectVersion.txt"),
            "m_EditorVersion: 6000.0.1f1\n",
        )
        .unwrap();
    }

    fn write_manifest(root: &Path, deps: &[(&str, &str)]) {
        let path = root.join("Packages").join("manifest.json");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let mut map = Map::new();
        for (k, v) in deps {
            map.insert((*k).to_string(), Value::String((*v).to_string()));
        }
        let value = json!({
            "dependencies": map,
            "scopedRegistries": [],
        });
        let text = serde_json::to_string_pretty(&value).unwrap();
        fs::write(&path, text + "\n").unwrap();
    }

    #[test]
    fn parse_unity_major_minor_accepts_canonical_unity_format() {
        assert_eq!(parse_unity_major_minor("6000.0.1f1"), Some((6000, 0)));
        assert_eq!(parse_unity_major_minor("2022.3.62f3"), Some((2022, 3)));
        assert_eq!(parse_unity_major_minor("6000.0"), Some((6000, 0)));
    }

    #[test]
    fn parse_unity_major_minor_rejects_garbage() {
        assert_eq!(parse_unity_major_minor(""), None);
        assert_eq!(parse_unity_major_minor("not a version"), None);
        assert_eq!(parse_unity_major_minor("6000"), None);
    }

    #[test]
    fn meets_min_unity_version_blocks_below_minimum() {
        assert!(meets_min_unity_version("6000.0.1f1"));
        assert!(meets_min_unity_version("6000.0"));
        assert!(!meets_min_unity_version("2022.3.62f3"));
        assert!(!meets_min_unity_version("5999.9.1f1"));
        assert!(!meets_min_unity_version("not-a-version"));
    }

    #[test]
    fn detect_marks_invalid_project_when_assets_missing() {
        let dir = tempdir().unwrap();
        let bogus = dir.path().join("nope");
        let state = detect_project_state_at(&bogus);
        assert!(!state.is_valid_unity_project);
        assert!(state.unity_version.is_none());
        assert!(!state.meets_min_unity_version);
    }

    #[test]
    fn detect_reads_unity_version_and_bridge_installed() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        write_manifest(
            dir.path(),
            &[
                (BRIDGE_PACKAGE_ID, "file:../../packages/bridge"),
                ("com.unity.render-pipelines.universal", "17.0.3"),
            ],
        );
        let state = detect_project_state_at(dir.path());
        assert!(state.is_valid_unity_project);
        assert_eq!(state.unity_version.as_deref(), Some("6000.0.1f1"));
        assert!(state.meets_min_unity_version);
        assert!(state.bridge_installed);
        assert!(!state.verify_installed);
        assert!(state.manifest_present);
    }

    #[test]
    fn detect_marks_spaces_in_path() {
        let dir = tempdir().unwrap();
        let spaced = dir.path().join("My Game");
        make_valid_project(&spaced);
        let state = detect_project_state_at(&spaced);
        assert!(state.has_spaces_in_path);
    }

    #[test]
    fn detect_bridge_status_is_not_checked() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        let state = detect_project_state_at(dir.path());
        // M4 explicitly defers /ping to Step 5; Step 1 must not
        // pretend it has probed the bridge.
        assert!(matches!(state.bridge_status, BridgeStatusKind::NotChecked));
    }

    #[test]
    fn read_manifest_parses_dependencies_map() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        write_manifest(
            dir.path(),
            &[
                (BRIDGE_PACKAGE_ID, "file:../../packages/bridge"),
                (VERIFY_PACKAGE_ID, "file:../../packages/verify"),
            ],
        );
        let r = read_manifest_inner(dir.path());
        assert!(r.present);
        assert!(r.readable);
        assert!(r.parse_error.is_none());
        assert_eq!(r.dependencies.len(), 2);
        assert_eq!(
            r.dependencies.get(BRIDGE_PACKAGE_ID).map(String::as_str),
            Some("file:../../packages/bridge")
        );
    }

    #[test]
    fn read_manifest_surfaces_invalid_json_error() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        let path = dir.path().join("Packages").join("manifest.json");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, "{ this is not json ").unwrap();
        let r = read_manifest_inner(dir.path());
        assert!(r.present);
        assert!(r.parse_error.is_some());
        assert!(r.dependencies.is_empty());
    }

    #[test]
    fn read_manifest_returns_absent_for_missing_file() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        let r = read_manifest_inner(dir.path());
        assert!(!r.present);
        assert!(r.dependencies.is_empty());
    }

    #[test]
    fn derive_package_urls_uses_default_remote_when_no_git_config() {
        let derived = derive_package_urls("/repos/uai", "", "");
        assert_eq!(derived.git_remote, DEFAULT_GIT_REMOTE);
        assert!(derived.bridge.url.contains("packages/bridge"));
        assert!(derived.bridge.url.contains(DEFAULT_BRIDGE_TAG));
        assert!(derived.verify.url.contains("packages/verify"));
        assert!(derived.verify.url.contains(DEFAULT_VERIFY_TAG));
    }

    #[test]
    fn derive_package_urls_reads_origin_from_git_config() {
        let dir = tempdir().unwrap();
        fs::create_dir_all(dir.path().join(".git")).unwrap();
        fs::write(
            dir.path().join(".git").join("config"),
            "[core]\n  repositoryformatversion = 0\n[remote \"origin\"]\n  url = git@github.com:AlexeyPerov/Unity-AI-Hub.git\n  fetch = +refs/heads/*:refs/remotes/origin/*\n",
        )
        .unwrap();
        let derived = derive_package_urls(
            dir.path().to_str().unwrap(),
            "",
            "",
        );
        assert_eq!(derived.git_remote, "git@github.com:AlexeyPerov/Unity-AI-Hub.git");
        assert!(derived.bridge.url.starts_with("git@github.com:AlexeyPerov/Unity-AI-Hub.git"));
    }

    #[test]
    fn derive_package_urls_prefers_custom_url() {
        let derived = derive_package_urls(
            "/repos/uai",
            "bridge-v1.2.3",
            "https://github.com/fork/Unity-AI-Hub.git",
        );
        assert_eq!(derived.git_remote, "https://github.com/fork/Unity-AI-Hub.git");
        assert_eq!(derived.bridge.tag, "bridge-v1.2.3");
        assert!(derived
            .bridge
            .url
            .contains("https://github.com/fork/Unity-AI-Hub.git"));
    }

    #[test]
    fn classify_reports_add_when_dependency_missing() {
        let mut existing = BTreeMap::new();
        existing.insert("com.unity.ide.rider".to_string(), "3.0.36".to_string());
        let kind = classify(&existing, BRIDGE_PACKAGE_ID, "file:../../packages/bridge", "");
        assert_eq!(kind, ChangeKind::Add);
    }

    #[test]
    fn classify_reports_unchanged_on_exact_match() {
        let url = "https://github.com/AlexeyPerov/Unity-AI-Hub.git?path=packages/bridge#bridge-v1.0.0";
        let mut existing = BTreeMap::new();
        existing.insert(BRIDGE_PACKAGE_ID.to_string(), url.to_string());
        let kind = classify(&existing, BRIDGE_PACKAGE_ID, url, "bridge-v1.0.0");
        assert_eq!(kind, ChangeKind::Unchanged);
    }

    #[test]
    fn classify_reports_upgrade_on_different_tag() {
        let mut existing = BTreeMap::new();
        existing.insert(
            BRIDGE_PACKAGE_ID.to_string(),
            "https://github.com/AlexeyPerov/Unity-AI-Hub.git?path=packages/bridge#bridge-v0.9.0"
                .to_string(),
        );
        let kind = classify(
            &existing,
            BRIDGE_PACKAGE_ID,
            "https://github.com/AlexeyPerov/Unity-AI-Hub.git?path=packages/bridge#bridge-v1.0.0",
            "bridge-v1.0.0",
        );
        assert_eq!(kind, ChangeKind::Upgrade);
    }

    #[test]
    fn classify_reports_upgrade_on_different_remote() {
        let mut existing = BTreeMap::new();
        existing.insert(
            BRIDGE_PACKAGE_ID.to_string(),
            "https://github.com/fork/Unity-AI-Hub.git?path=packages/bridge#bridge-v1.0.0"
                .to_string(),
        );
        let kind = classify(
            &existing,
            BRIDGE_PACKAGE_ID,
            "https://github.com/AlexeyPerov/Unity-AI-Hub.git?path=packages/bridge#bridge-v1.0.0",
            "bridge-v1.0.0",
        );
        assert_eq!(kind, ChangeKind::Upgrade);
    }

    #[test]
    fn plan_manifest_merge_marks_noop_when_already_installed() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        let derived = derive_package_urls("/repos/uai", "", "");
        write_manifest(
            dir.path(),
            &[
                (BRIDGE_PACKAGE_ID, &derived.bridge.url),
                (VERIFY_PACKAGE_ID, &derived.verify.url),
            ],
        );
        let plan = plan_manifest_merge(ManifestMergeParams {
            project_path: dir.path().to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            install_bridge: true,
            install_verify: true,
            version_pin: String::new(),
            custom_url: String::new(),
            confirm_upgrades: false,
        });
        assert!(!plan.has_upgrades);
        assert_eq!(plan.changes.len(), 2);
        assert!(plan.changes.iter().all(|c| c.kind == ChangeKind::Unchanged));
    }

    #[test]
    fn plan_manifest_merge_flags_upgrade_when_tag_differs() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        write_manifest(
            dir.path(),
            &[(
                BRIDGE_PACKAGE_ID,
                "https://github.com/AlexeyPerov/Unity-AI-Hub.git?path=packages/bridge#bridge-v0.5.0",
            )],
        );
        let plan = plan_manifest_merge(ManifestMergeParams {
            project_path: dir.path().to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            install_bridge: true,
            install_verify: false,
            version_pin: String::new(),
            custom_url: String::new(),
            confirm_upgrades: false,
        });
        assert!(plan.has_upgrades);
        let bridge = plan
            .changes
            .iter()
            .find(|c| c.id == BRIDGE_PACKAGE_ID)
            .unwrap();
        assert_eq!(bridge.kind, ChangeKind::Upgrade);
        assert!(bridge.before.as_ref().unwrap().contains("v0.5.0"));
        assert!(bridge.after.contains("v1.0.0"));
    }

    #[test]
    fn write_manifest_merge_creates_backup_and_preserves_unrelated_keys() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        write_manifest(
            dir.path(),
            &[
                ("com.unity.ide.rider", "3.0.36"),
                ("com.unity.ide.visualstudio", "2.0.22"),
            ],
        );
        let result = write_manifest_merge(ManifestMergeParams {
            project_path: dir.path().to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            install_bridge: true,
            install_verify: true,
            version_pin: String::new(),
            custom_url: String::new(),
            confirm_upgrades: false,
        })
        .unwrap();
        assert!(!result.backup_path.is_empty());
        assert!(std::path::Path::new(&result.backup_path).exists());

        // Re-read the manifest to ensure unrelated keys survived.
        let read_after = read_manifest_inner(dir.path());
        assert!(read_after
            .dependencies
            .contains_key("com.unity.ide.rider"));
        assert!(read_after
            .dependencies
            .contains_key("com.unity.ide.visualstudio"));
        assert!(read_after.dependencies.contains_key(BRIDGE_PACKAGE_ID));
        assert!(read_after.dependencies.contains_key(VERIFY_PACKAGE_ID));
    }

    #[test]
    fn write_manifest_merge_adds_missing_entries_without_backup() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        // No existing manifest at all.
        let result = write_manifest_merge(ManifestMergeParams {
            project_path: dir.path().to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            install_bridge: true,
            install_verify: false,
            version_pin: String::new(),
            custom_url: String::new(),
            confirm_upgrades: false,
        })
        .unwrap();
        assert!(result.backup_path.is_empty());
        assert_eq!(result.dependencies.len(), 1);
        let read_after = read_manifest_inner(dir.path());
        assert!(read_after.dependencies.contains_key(BRIDGE_PACKAGE_ID));
    }

    #[test]
    fn write_manifest_merge_refuses_upgrade_without_confirmation() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        write_manifest(
            dir.path(),
            &[(
                BRIDGE_PACKAGE_ID,
                "https://github.com/AlexeyPerov/Unity-AI-Hub.git?path=packages/bridge#bridge-v0.5.0",
            )],
        );
        let err = write_manifest_merge(ManifestMergeParams {
            project_path: dir.path().to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            install_bridge: true,
            install_verify: false,
            version_pin: String::new(),
            custom_url: String::new(),
            confirm_upgrades: false,
        })
        .unwrap_err();
        assert_eq!(err.kind, "upgradeNotConfirmed");

        // The manifest must not have changed.
        let read_after = read_manifest_inner(dir.path());
        assert!(read_after
            .dependencies
            .get(BRIDGE_PACKAGE_ID)
            .unwrap()
            .contains("v0.5.0"));
    }

    #[test]
    fn write_manifest_merge_blocks_on_invalid_json() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        let path = dir.path().join("Packages").join("manifest.json");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, "{ not valid json ").unwrap();
        let err = write_manifest_merge(ManifestMergeParams {
            project_path: dir.path().to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            install_bridge: true,
            install_verify: true,
            version_pin: String::new(),
            custom_url: String::new(),
            confirm_upgrades: true,
        })
        .unwrap_err();
        assert_eq!(err.kind, "invalidJson");
    }

    #[test]
    fn write_manifest_merge_refuses_non_unity_project() {
        let dir = tempdir().unwrap();
        let result = write_manifest_merge(ManifestMergeParams {
            project_path: dir.path().to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            install_bridge: true,
            install_verify: false,
            version_pin: String::new(),
            custom_url: String::new(),
            confirm_upgrades: false,
        });
        assert!(matches!(result, Err(ref e) if e.kind == "notAUnityProject"));
    }

    #[test]
    fn write_manifest_merge_uses_version_pin_for_both_packages() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        let result = write_manifest_merge(ManifestMergeParams {
            project_path: dir.path().to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            install_bridge: true,
            install_verify: true,
            version_pin: "bridge-v1.2.3".to_string(),
            custom_url: String::new(),
            confirm_upgrades: false,
        })
        .unwrap();
        assert!(result
            .dependencies
            .get(BRIDGE_PACKAGE_ID)
            .unwrap()
            .contains("bridge-v1.2.3"));
        assert!(result
            .dependencies
            .get(VERIFY_PACKAGE_ID)
            .unwrap()
            .contains("bridge-v1.2.3"));
    }

    #[test]
    fn write_manifest_merge_noop_when_already_installed() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        let derived = derive_package_urls("/repos/uai", "", "");
        write_manifest(
            dir.path(),
            &[
                (BRIDGE_PACKAGE_ID, &derived.bridge.url),
                (VERIFY_PACKAGE_ID, &derived.verify.url),
            ],
        );
        let result = write_manifest_merge(ManifestMergeParams {
            project_path: dir.path().to_string_lossy().into_owned(),
            toolkit_root: "/repos/uai".to_string(),
            install_bridge: true,
            install_verify: true,
            version_pin: String::new(),
            custom_url: String::new(),
            confirm_upgrades: false,
        })
        .unwrap();
        // All changes are Unchanged, so the writer did not touch
        // the manifest, no backup was made, but the call succeeds.
        assert!(result.changes.iter().all(|c| c.kind == ChangeKind::Unchanged));
        assert!(result.backup_path.is_empty());
    }

    #[test]
    fn manifest_writable_when_path_is_valid() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        let manifest_path = dir.path().join("Packages").join("manifest.json");
        // No file yet — the wizard will create it; that is fine
        // as long as the project root is writable.
        assert!(check_manifest_writable_at(&manifest_path));
    }

    #[test]
    fn manifest_writable_when_file_exists() {
        let dir = tempdir().unwrap();
        make_valid_project(dir.path());
        write_manifest(dir.path(), &[]);
        let manifest_path = dir.path().join("Packages").join("manifest.json");
        assert!(check_manifest_writable_at(&manifest_path));
    }
}
