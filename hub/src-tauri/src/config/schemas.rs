use std::collections::BTreeMap;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub version: u32,
    pub launch: LaunchSettings,
    pub project_list: ProjectListSettings,
    pub safety: SafetySettings,
    pub unity_discovery: UnityDiscoverySettings,
    #[serde(default = "default_diagnostics_settings")]
    pub diagnostics: DiagnosticsSettings,
    /// M4: AI toolkit root + advanced MCP override (Q2=B). The wizard
    /// Step 2 collects the cloned unity-open-mcp monorepo path and
    /// persists it here; the MCP path, package URLs, and skill copy
    /// sources are all derived from `aiToolkit.rootPath`. The
    /// advanced override is the per-M4 Step 4 escape hatch for a
    /// custom-built `mcp-server/dist/index.js` only — packages and
    /// skills remain rooted at `rootPath`. `#[serde(default)]` keeps
    /// legacy `settings.json` files loadable with empty defaults.
    #[serde(default)]
    pub ai_toolkit: AiToolkitSettings,
    /// M1.5-18: three-way theme switch — `dark` | `light` | `system`
    /// (default). `#[serde(default = "default_theme")]` keeps legacy
    /// `settings.json` files loadable; the documented default is
    /// `system` per the task spec. The frontend drives a
    /// `[data-theme="…"]` attribute on `<html>` from this field.
    #[serde(default = "default_theme")]
    pub theme: String,
    /// Multi-type: when the on-disk size of a project is below this
    /// threshold (in MiB), the git popup auto-calculates and caches a
    /// line count so a passive stat is shown. Above it, the popup shows
    /// a hint instead and the user can run the manual counter. Default
    /// 30 MiB per the brief. `#[serde(default)]` keeps legacy files
    /// loadable.
    #[serde(default = "default_line_count_threshold_mb")]
    pub line_count_auto_calc_threshold_mb: u32,
}

fn default_line_count_threshold_mb() -> u32 {
    30
}

fn default_theme() -> String {
    "system".to_string()
}

/// M4: AI toolkit root + advanced MCP override. `#[serde(default)]`
/// keeps legacy `settings.json` files loadable with empty defaults
/// (the wizard prompts for the root before any derived path is used).
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AiToolkitSettings {
    /// Absolute path to the cloned unity-open-mcp monorepo root.
    /// Empty when the wizard has not yet collected a valid root;
    /// downstream steps (3/4) hard-block until this is set on the
    /// local-checkout path.
    #[serde(default)]
    pub root_path: String,
    /// Optional Step 4 advanced override for `mcp-server/dist/index.js`.
    /// Empty means "use the path derived from `rootPath`". Packages
    /// and skill copy always use `rootPath` regardless of this value.
    #[serde(default)]
    pub mcp_index_override: String,
    /// Step 2 toggle: when `true`, the wizard uses the local toolkit
    /// checkout to derive the MCP launch command (`node <root>/mcp-server/
    /// dist/index.js`); when `false` (default), onboarding resolves to the
    /// bundled npm package via `npx -y unity-open-mcp@latest`. The wizard
    /// auto-enables this when `root_path` is already set so existing M4
    /// (clone-based) onboarding keeps working without a forced migration.
    #[serde(default)]
    pub use_local_checkout: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiagnosticsSettings {
    pub auto_open_drawer_on_launch_failure: bool,
}

fn default_diagnostics_settings() -> DiagnosticsSettings {
    DiagnosticsSettings {
        auto_open_drawer_on_launch_failure: true,
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchSettings {
    pub mode: String,
    pub remember_last_selection: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectListSettings {
    pub show_path_column: bool,
    pub show_modified_column: bool,
    /// Added in M1.5-6. `#[serde(default)]` keeps legacy `settings.json`
    /// files (pre-M1.5-4/6) loadable; the default is `true` so the column
    /// is visible by default per the task acceptance checklist.
    #[serde(default = "default_true")]
    pub show_git_branch_column: bool,
    pub search_includes_path: bool,
    /// `"frecency"` (default) or `"lastModified"`. Unknown values fall back to
    /// `"frecency"` so a future field addition never bricks the list sort.
    #[serde(default = "default_project_list_sort_by")]
    pub sort_by: String,
    /// M1.5-15: when true, the Projects tab starts in the "Missing or
    /// stale" filter preset on launch so a freshly-installed Hub does
    /// not immediately flash every missing path at the user. The
    /// toolbar's filter chips and the "Show hidden" toggle always
    /// remain reachable — this only changes the default selection.
    /// `#[serde(default)]` keeps legacy `settings.json` files loadable
    /// (default value is `false`).
    #[serde(default)]
    pub hide_missing_by_default: bool,
}

fn default_project_list_sort_by() -> String {
    "frecency".to_string()
}

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SafetySettings {
    pub confirm_kill_unity: bool,
    pub confirm_remove_project: bool,
    /// M1.5-17: when `true` (default), a confirmation modal lists
    /// colliding env-var keys before a launch so the user is warned
    /// that the spawned Unity will override a parent-process variable.
    /// `#[serde(default = "default_true")]` keeps legacy `settings.json`
    /// files loadable; the documented default is `true` per the task
    /// spec.
    #[serde(default = "default_true")]
    pub confirm_env_var_override: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UnityDiscoverySettings {
    pub parent_folders: Vec<String>,
    /// Polling interval (in seconds) for the running-Unity process scan that
    /// powers the `running` chip on the Projects tab. Added in M1.5-10.
    /// `#[serde(default = "default_scan_interval_seconds")]` keeps legacy
    /// `settings.json` files loadable; the default is `30` (raised from `5`
    /// — the scan shells out to `ps`/PowerShell, so a longer cadence avoids
    /// idle CPU churn and re-render storms while still catching starts/stops
    /// promptly enough for a status chip).
    #[serde(default = "default_scan_interval_seconds")]
    pub scan_interval_seconds: u32,
    /// M1.5-11: walk-up directory scan roots. Each entry is a folder the
    /// user wants Hub to recursively scan for Unity project roots
    /// (a folder containing both `Assets/` and `ProjectSettings/`).
    /// Added in M1.5-11. `#[serde(default)]` keeps legacy `settings.json`
    /// files loadable; the documented default is an empty list.
    #[serde(default)]
    pub walk_up_roots: Vec<String>,
    /// M1.5-11: maximum directory depth the walk-up scan descends from
    /// each root. Default 4, hard cap 8. `#[serde(default)]` keeps legacy
    /// `settings.json` files loadable.
    #[serde(default = "default_walk_up_max_depth")]
    pub walk_up_max_depth: u32,
    /// M1.5-11: when true, the walk-up scan follows symbolic links. Off
    /// by default to avoid loops and unintended traversal of the user's
    /// home directory. `#[serde(default)]` keeps legacy files loadable.
    #[serde(default)]
    pub walk_up_follow_symlinks: bool,
    /// M1.5-11: when true (default), a cancelled walk-up scan keeps the
    /// projects it had already discovered and appended to
    /// `projects.json`; when false, the partial results are discarded.
    /// `#[serde(default = "default_true")]` keeps legacy files loadable.
    #[serde(default = "default_true")]
    pub walk_up_keep_partial: bool,
    /// M1.5-13: absolute paths to user-curated Unity project roots
    /// that can be used as a template when creating a new project
    /// ("Custom folder…" in the New Project modal). Each entry is
    /// validated as a Unity project root (must contain `Assets/` and
    /// `ProjectSettings/`) when the project is created; the
    /// Settings UI rejects the entry on save when the path does not
    /// resolve to a directory.
    /// `#[serde(default)]` keeps legacy `settings.json` files loadable.
    #[serde(default)]
    pub custom_template_folders: Vec<String>,
}

fn default_scan_interval_seconds() -> u32 {
    30
}

fn default_walk_up_max_depth() -> u32 {
    4
}

/// Seed the "additional parent folders" list with the OS-default Unity
/// Hub editor folder for the current host. We only seed the *current*
/// OS's default — the Settings UI is per-machine and showing
/// cross-platform paths (e.g. a Mac user seeing
/// `C:\Program Files\Unity\Hub\Editor`) is confusing and a no-op for
/// discovery, which always scans the current OS's defaults regardless
/// of what is stored here.
fn default_parent_folders() -> Vec<String> {
    let path: PathBuf = if cfg!(target_os = "macos") {
        PathBuf::from("/Applications/Unity/Hub/Editor")
    } else if cfg!(target_os = "windows") {
        PathBuf::from("C:\\Program Files\\Unity\\Hub\\Editor")
    } else {
        dirs::home_dir()
            .map(|h| h.join("Unity/Hub/Editor"))
            .unwrap_or_else(|| PathBuf::from("Unity/Hub/Editor"))
    };
    vec![path.to_string_lossy().to_string()]
}

/// Returns the OS-default Unity Hub editor folders for the current
/// host. The frontend uses this to mark the matching rows in
/// "Additional parent folders" as non-removable (the Settings tab
/// shows them as informational entries that ship with the OS).
pub fn os_default_hub_paths() -> Vec<String> {
    default_parent_folders()
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            version: 1,
            launch: LaunchSettings {
                mode: "openProject".to_string(),
                remember_last_selection: true,
            },
            project_list: ProjectListSettings {
                show_path_column: true,
                show_modified_column: true,
                show_git_branch_column: true,
                search_includes_path: true,
                sort_by: default_project_list_sort_by(),
                hide_missing_by_default: false,
            },
            safety: SafetySettings {
                confirm_kill_unity: true,
                confirm_remove_project: true,
                confirm_env_var_override: true,
            },
            unity_discovery: UnityDiscoverySettings {
                parent_folders: default_parent_folders(),
                scan_interval_seconds: default_scan_interval_seconds(),
                walk_up_roots: Vec::new(),
                walk_up_max_depth: default_walk_up_max_depth(),
                walk_up_follow_symlinks: false,
                walk_up_keep_partial: true,
                custom_template_folders: Vec::new(),
            },
            diagnostics: DiagnosticsSettings {
                auto_open_drawer_on_launch_failure: true,
            },
            ai_toolkit: AiToolkitSettings::default(),
            theme: default_theme(),
            line_count_auto_calc_threshold_mb: default_line_count_threshold_mb(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectsFile {
    pub version: u32,
    pub projects: Vec<ProjectEntry>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectEntry {
    pub id: String,
    pub name: String,
    pub path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unity_version: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_opened_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_modified_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub launch_args: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub platform_intent: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_launch_pid: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_launch_at: Option<String>,
    /// Frecency counter. Incremented on every successful launch; the sort
    /// score combines this counter with `lastLaunchAt` (14-day half-life).
    /// Defaulted to 0 for legacy entries via `#[serde(default)]`.
    #[serde(default)]
    pub frecency: u32,
    /// Cached git branch name (`refs/heads/<name>`) or full ref for detached
    /// HEAD. `None` for non-git projects or when the read is pending.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub git_branch: Option<String>,
    /// M1.5-11: where this entry came from. One of:
    /// - `"hub-seed"` (from the M1 first-run Unity Hub import)
    /// - `"manual"` (user added via the Add Project button / drag-drop)
    /// - `"walk-up"` (added by the walk-up directory scan)
    /// - `"cli"` (added by the CLI mode auto-launch flow)
    /// Legacy entries deserialize as `"manual"` so the on-disk file
    /// stays compact.
    #[serde(default = "default_project_source")]
    pub source: String,
    /// M1.5-15: when true, the row is soft-deleted from the Projects tab
    /// list view but the entry is kept on disk so a "Show hidden" toggle
    /// in the toolbar can reveal it again. The Project Status, M-time,
    /// frecency, and other fields are preserved untouched. Default
    /// `false`. `#[serde(default)]` keeps legacy `projects.json` files
    /// loadable.
    #[serde(default)]
    pub hidden: bool,
    /// M1.5-15: when true, the row is kept visible with a `stale` chip
    /// (distinct from `missing path`) and is excluded from launch /
    /// running-Unity actions. A "Mark stale" toggle on the missing-path
    /// chip is the only way to set this field; relinking to a real
    /// project root clears it. Default `false`. `#[serde(default)]`
    /// keeps legacy entries loadable.
    #[serde(default)]
    pub stale: bool,
    /// M1.5-17: per-project environment variables merged into the
    /// spawned Unity process's environment (the child overrides the
    /// parent when keys collide). Stored as a record of strings; empty
    /// maps are skipped on serialize so legacy entries stay compact.
    /// `#[serde(default, skip_serializing_if = "BTreeMap::is_empty")]`
    /// keeps pre-M1.5-17 `projects.json` files loadable with an empty
    /// map (the documented default).
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub env_vars: BTreeMap<String, String>,
    /// M15 T6.4: cached render-pipeline label for the project, read
    /// from `ProjectSettings/GraphicsSettings.asset` (URP / HDRP /
    /// Built-in). Stored as a short kebab string so the frontend can
    /// render a chip without re-running detection on every paint.
    /// `#[serde(default, skip_serializing_if = "Option::is_none")]`
    /// keeps legacy `projects.json` files loadable and the on-disk
    /// shape compact when the value has not been computed yet.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub render_pipeline: Option<String>,
    /// M15 T6.4: cached default build target read from
    /// `ProjectSettings/ProjectSettings.asset` (`m_BuildTarget` /
    /// `m_BuildTargetGroup`). `None` for projects that have never been
    /// opened by a Unity Editor (Unity writes the keys on first save).
    /// `#[serde(default, skip_serializing_if = "Option::is_none")]`
    /// keeps legacy `projects.json` files loadable.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub default_build_target: Option<String>,
    /// Multi-type support: discriminator for how the hub treats this
    /// folder. `Unity` is the default for legacy entries (the only kind
    /// the hub tracked before multi-type), so `#[serde(default = …)]`
    /// keeps existing `projects.json` files loadable with no migration.
    #[serde(default = "default_project_kind")]
    pub kind: ProjectKind,
    /// For Package / OpenMcp kinds: relative path to the package
    /// manifest from the project root (`package.json`). `None` for
    /// Unity / Custom kinds. Stored so the UI does not have to re-detect
    /// it on every paint.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub package_manifest_path: Option<String>,
    /// Per-package saved source folder for the "Migrate" feature in the
    /// Package settings popup. Persisted across sessions per the brief.
    /// `None` for kinds that do not support migrate.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub migrate_source_folder: Option<String>,
    /// Cached output of the line counter (§7). Populated by the manual
    /// "Run line count" button and, when the project is small enough,
    /// by the git-popup auto-calc path. `None` until first run.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub line_count_stats: Option<LineCountStats>,
}

/// The four kinds of folder the hub tracks. Ordering follows the
/// detection precedence in `project_kind::detect_kind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ProjectKind {
    Unity,
    Package,
    OpenMcp,
    Custom,
}

impl Default for ProjectKind {
    fn default() -> Self {
        ProjectKind::Unity
    }
}

impl ProjectKind {
    pub fn as_str(self) -> &'static str {
        match self {
            ProjectKind::Unity => "unity",
            ProjectKind::Package => "package",
            ProjectKind::OpenMcp => "openMcp",
            ProjectKind::Custom => "custom",
        }
    }
}

fn default_project_kind() -> ProjectKind {
    ProjectKind::Unity
}

/// Cached line-counter result. The `details` string is the same
/// 4-section report the CLI emits (extensions counted/ignored, skipped
/// dirs, `.gitignore` respected), suitable for appending to the app
/// logs without re-formatting.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LineCountStats {
    pub total_lines: u64,
    pub code_files: u32,
    pub ignored_files: u32,
    pub skipped_dirs: u32,
    /// ISO-8601 timestamp of the scan; shown in the settings popup so
    /// the user knows how stale the cached number is.
    pub scanned_at: String,
    /// Human-readable report (the four LineWalker sections). Surfaced
    /// to the app logs by the manual "Run line count" button.
    pub details: String,
}

fn default_project_source() -> String {
    "manual".to_string()
}

impl Default for ProjectsFile {
    fn default() -> Self {
        ProjectsFile {
            version: 1,
            projects: vec![],
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn settings_default_version_is_1() {
        assert_eq!(Settings::default().version, 1);
    }

    #[test]
    fn settings_default_launch_mode() {
        let s = Settings::default();
        assert_eq!(s.launch.mode, "openProject");
        assert!(s.launch.remember_last_selection);
    }

    #[test]
    fn settings_default_safety_all_confirmed() {
        let s = Settings::default();
        assert!(s.safety.confirm_kill_unity);
        assert!(s.safety.confirm_remove_project);
        assert!(s.safety.confirm_env_var_override);
    }

    #[test]
    fn settings_default_project_list_all_visible() {
        let s = Settings::default();
        assert!(s.project_list.show_path_column);
        assert!(s.project_list.show_modified_column);
        assert!(s.project_list.show_git_branch_column);
        assert!(s.project_list.search_includes_path);
        assert_eq!(s.project_list.sort_by, "frecency");
    }

    #[test]
    fn settings_default_discovery_has_one_os_folder() {
        let s = Settings::default();
        // We only seed the *current* OS's default Hub path; the
        // discovery layer always scans it regardless of this list, so
        // stuffing cross-platform defaults here is just noise on
        // machines that will never use them.
        assert_eq!(s.unity_discovery.parent_folders.len(), 1);
    }

    #[test]
    fn settings_default_diagnostics_auto_open_on() {
        let s = Settings::default();
        assert!(s.diagnostics.auto_open_drawer_on_launch_failure);
    }

    #[test]
    fn settings_diagnostics_roundtrip() {
        let mut s = Settings::default();
        s.diagnostics.auto_open_drawer_on_launch_failure = false;
        let json = serde_json::to_string_pretty(&s).unwrap();
        let restored: Settings = serde_json::from_str(&json).unwrap();
        assert!(!restored.diagnostics.auto_open_drawer_on_launch_failure);
    }

    #[test]
    fn settings_loads_without_diagnostics_field() {
        // Backwards compat: legacy settings.json files written before
        // M1.5-3 do not carry the `diagnostics` block. The default must
        // kick in so existing user configs are not rejected.
        let legacy = r#"{
            "version": 1,
            "launch": { "mode": "openProject", "rememberLastSelection": true },
            "projectList": {
                "showPathColumn": true,
                "showModifiedColumn": true,
                "searchIncludesPath": true
            },
            "safety": { "confirmKillUnity": true, "confirmRemoveProject": true },
            "unityDiscovery": { "parentFolders": [] }
        }"#;
        let restored: Settings = serde_json::from_str(legacy).unwrap();
        assert!(restored.diagnostics.auto_open_drawer_on_launch_failure);
    }

    #[test]
    fn settings_loads_legacy_project_list_without_new_fields() {
        // Pre-M1.5-4/6 settings.json files do not carry `showGitBranchColumn`
        // or `sortBy`. Both must default in for backwards compatibility so
        // existing user configs are not rejected.
        let legacy = r#"{
            "version": 1,
            "launch": { "mode": "openProject", "rememberLastSelection": true },
            "projectList": {
                "showPathColumn": true,
                "showModifiedColumn": true,
                "searchIncludesPath": true
            },
            "safety": { "confirmKillUnity": true, "confirmRemoveProject": true },
            "unityDiscovery": { "parentFolders": [] }
        }"#;
        let restored: Settings = serde_json::from_str(legacy).unwrap();
        assert!(restored.project_list.show_git_branch_column);
        assert_eq!(restored.project_list.sort_by, "frecency");
    }

    #[test]
    fn settings_loads_legacy_project_list_without_hide_missing_by_default() {
        // Pre-M1.5-15 settings.json files do not carry
        // `hideMissingByDefault`. The deserializer must default to
        // `false` (the documented default) so existing user configs are
        // not rejected.
        let legacy = r#"{
            "version": 1,
            "launch": { "mode": "openProject", "rememberLastSelection": true },
            "projectList": {
                "showPathColumn": true,
                "showModifiedColumn": true,
                "searchIncludesPath": true
            },
            "safety": { "confirmKillUnity": true, "confirmRemoveProject": true },
            "unityDiscovery": { "parentFolders": [] }
        }"#;
        let restored: Settings = serde_json::from_str(legacy).unwrap();
        assert!(!restored.project_list.hide_missing_by_default);
    }

    #[test]
    fn settings_loads_legacy_discovery_without_scan_interval() {
        // Pre-M1.5-10 settings.json files do not carry `scanIntervalSeconds`.
        // The deserializer must fall back to the documented default (30) so
        // existing configs are not rejected (the same pattern as
        // `showGitBranchColumn`).
        let legacy = r#"{
            "version": 1,
            "launch": { "mode": "openProject", "rememberLastSelection": true },
            "projectList": {
                "showPathColumn": true,
                "showModifiedColumn": true,
                "searchIncludesPath": true
            },
            "safety": { "confirmKillUnity": true, "confirmRemoveProject": true },
            "unityDiscovery": { "parentFolders": [] }
        }"#;
        let restored: Settings = serde_json::from_str(legacy).unwrap();
        assert_eq!(restored.unity_discovery.scan_interval_seconds, 30);
    }

    #[test]
    fn settings_loads_legacy_discovery_without_walk_up_fields() {
        // Pre-M1.5-11 settings.json files do not carry any of the
        // `walkUp*` fields. All four must default in: empty roots, max
        // depth 4, follow-symlinks off, keep-partial on. This is the
        // contract that keeps existing user configs loadable.
        let legacy = r#"{
            "version": 1,
            "launch": { "mode": "openProject", "rememberLastSelection": true },
            "projectList": {
                "showPathColumn": true,
                "showModifiedColumn": true,
                "searchIncludesPath": true
            },
            "safety": { "confirmKillUnity": true, "confirmRemoveProject": true },
            "unityDiscovery": { "parentFolders": [] }
        }"#;
        let restored: Settings = serde_json::from_str(legacy).unwrap();
        assert!(restored.unity_discovery.walk_up_roots.is_empty());
        assert_eq!(restored.unity_discovery.walk_up_max_depth, 4);
        assert!(!restored.unity_discovery.walk_up_follow_symlinks);
        assert!(restored.unity_discovery.walk_up_keep_partial);
    }

    #[test]
    fn settings_loads_legacy_discovery_without_custom_template_folders() {
        // Pre-M1.5-13 settings.json files do not carry
        // `customTemplateFolders`. The deserializer must default to an
        // empty list so existing user configs are not rejected
        // (same pattern as the M1.5-11 walk-up fields).
        let legacy = r#"{
            "version": 1,
            "launch": { "mode": "openProject", "rememberLastSelection": true },
            "projectList": {
                "showPathColumn": true,
                "showModifiedColumn": true,
                "searchIncludesPath": true
            },
            "safety": { "confirmKillUnity": true, "confirmRemoveProject": true },
            "unityDiscovery": { "parentFolders": [] }
        }"#;
        let restored: Settings = serde_json::from_str(legacy).unwrap();
        assert!(restored.unity_discovery.custom_template_folders.is_empty());
    }

    #[test]
    fn custom_template_folders_roundtrip() {
        let mut s = Settings::default();
        s.unity_discovery.custom_template_folders = vec![
            "/Users/dev/UnityTemplates/Empty".to_string(),
            "C:\\UnityTemplates\\URP".to_string(),
        ];
        let json = serde_json::to_string_pretty(&s).unwrap();
        let restored: Settings = serde_json::from_str(&json).unwrap();
        assert_eq!(restored.unity_discovery.custom_template_folders.len(), 2);
        assert_eq!(
            restored.unity_discovery.custom_template_folders[0],
            "/Users/dev/UnityTemplates/Empty"
        );
    }

    #[test]
    fn settings_loads_legacy_settings_without_theme() {
        // M1.5-18: pre-M1.5-18 settings.json files do not carry the
        // `theme` field. The deserializer must default to `"system"`
        // (the documented default) so existing user configs are not
        // rejected and a freshly-updated Hub follows the OS color
        // scheme on first launch.
        let legacy = r#"{
            "version": 1,
            "launch": { "mode": "openProject", "rememberLastSelection": true },
            "projectList": {
                "showPathColumn": true,
                "showModifiedColumn": true,
                "searchIncludesPath": true
            },
            "safety": { "confirmKillUnity": true, "confirmRemoveProject": true },
            "unityDiscovery": { "parentFolders": [] }
        }"#;
        let restored: Settings = serde_json::from_str(legacy).unwrap();
        assert_eq!(restored.theme, "system");
    }

    #[test]
    fn settings_default_theme_is_system() {
        let s = Settings::default();
        assert_eq!(s.theme, "system");
    }

    #[test]
    fn settings_theme_roundtrip() {
        let mut s = Settings::default();
        s.theme = "light".to_string();
        let json = serde_json::to_string_pretty(&s).unwrap();
        let restored: Settings = serde_json::from_str(&json).unwrap();
        assert_eq!(restored.theme, "light");
    }

    #[test]
    fn projects_default_empty() {
        let p = ProjectsFile::default();
        assert_eq!(p.version, 1);
        assert!(p.projects.is_empty());
    }

    #[test]
    fn settings_roundtrip() {
        let original = Settings::default();
        let json = serde_json::to_string_pretty(&original).unwrap();
        let restored: Settings = serde_json::from_str(&json).unwrap();
        assert_eq!(original.version, restored.version);
        assert_eq!(original.launch.mode, restored.launch.mode);
        assert_eq!(
            original.launch.remember_last_selection,
            restored.launch.remember_last_selection
        );
        assert_eq!(
            original.safety.confirm_kill_unity,
            restored.safety.confirm_kill_unity
        );
        assert_eq!(
            original.project_list.show_path_column,
            restored.project_list.show_path_column
        );
        assert_eq!(
            original.unity_discovery.parent_folders,
            restored.unity_discovery.parent_folders
        );
    }

    #[test]
    fn projects_roundtrip_with_entry() {
        let original = ProjectsFile {
            version: 1,
            projects: vec![ProjectEntry {
                id: "test-id".to_string(),
                name: "TestProject".to_string(),
                path: "/tmp/test".to_string(),
                unity_version: Some("6000.0.1f1".to_string()),
                last_opened_at: None,
                last_modified_at: Some("2026-06-09T12:00:00Z".to_string()),
                launch_args: None,
                platform_intent: Some("StandaloneWindows64".to_string()),
                last_launch_pid: Some(12345),
                last_launch_at: Some("2026-06-09T11:55:00Z".to_string()),
                frecency: 3,
                git_branch: Some("feature/frecency".to_string()),
                source: "manual".to_string(),
                hidden: false,
                stale: false,
                env_vars: BTreeMap::from([
                    ("MY_KEY".to_string(), "hello".to_string()),
                    ("DEBUG_LEVEL".to_string(), "verbose".to_string()),
                ]),
                render_pipeline: Some("URP".to_string()),
                default_build_target: Some("StandaloneWindows64".to_string()),
                kind: ProjectKind::Unity,
                package_manifest_path: None,
                migrate_source_folder: None,
                line_count_stats: None,
            }],
        };
        let json = serde_json::to_string_pretty(&original).unwrap();
        let restored: ProjectsFile = serde_json::from_str(&json).unwrap();
        assert_eq!(restored.version, 1);
        assert_eq!(restored.projects.len(), 1);
        let p = &restored.projects[0];
        assert_eq!(p.id, "test-id");
        assert_eq!(p.name, "TestProject");
        assert_eq!(p.path, "/tmp/test");
        assert_eq!(p.unity_version.as_deref(), Some("6000.0.1f1"));
        assert!(p.last_opened_at.is_none());
        assert_eq!(
            p.last_modified_at.as_deref(),
            Some("2026-06-09T12:00:00Z")
        );
        assert_eq!(p.last_launch_pid, Some(12345));
        assert_eq!(p.frecency, 3);
        assert_eq!(p.git_branch.as_deref(), Some("feature/frecency"));
        // M1.5-17: env_vars round-trip preserves both keys (BTreeMap
        // ordering is alphabetical, so the JSON shape is stable).
        assert_eq!(p.env_vars.len(), 2);
        assert_eq!(p.env_vars.get("MY_KEY").map(String::as_str), Some("hello"));
        assert_eq!(
            p.env_vars.get("DEBUG_LEVEL").map(String::as_str),
            Some("verbose")
        );
        // M15 T6.4: renderPipeline + defaultBuildTarget round-trip.
        assert_eq!(p.render_pipeline.as_deref(), Some("URP"));
        assert_eq!(
            p.default_build_target.as_deref(),
            Some("StandaloneWindows64")
        );
    }

    #[test]
    fn settings_serializes_camel_case() {
        let s = Settings::default();
        let json = serde_json::to_string(&s).unwrap();
        assert!(json.contains("\"rememberLastSelection\""));
        assert!(json.contains("\"showPathColumn\""));
        assert!(json.contains("\"confirmKillUnity\""));
        assert!(json.contains("\"parentFolders\""));
    }

    #[test]
    fn project_entry_skips_none_optional_fields() {
        let entry = ProjectEntry {
            id: "id".to_string(),
            name: "Name".to_string(),
            path: "/path".to_string(),
            unity_version: None,
            last_opened_at: None,
            last_modified_at: None,
            launch_args: None,
            platform_intent: None,
            last_launch_pid: None,
            last_launch_at: None,
            frecency: 0,
            git_branch: None,
            source: "manual".to_string(),
            hidden: false,
            stale: false,
            env_vars: BTreeMap::new(),
            render_pipeline: None,
            default_build_target: None,
            kind: ProjectKind::Unity,
            package_manifest_path: None,
            migrate_source_folder: None,
            line_count_stats: None,
        };
        let json = serde_json::to_string(&entry).unwrap();
        assert!(!json.contains("unityVersion"));
        assert!(!json.contains("lastOpenedAt"));
        assert!(!json.contains("lastLaunchPid"));
        assert!(!json.contains("gitBranch"));
        // frecency has no `skip_serializing_if`, so it is always emitted;
        // we keep the disk file compact for legacy fields but the new
        // counter is part of the public sort contract and must be visible.
        assert!(json.contains("\"frecency\":0"));
        // hidden and stale default to false; they are emitted because
        // the new fields are part of the persistent on-disk contract.
        assert!(json.contains("\"hidden\":false"));
        assert!(json.contains("\"stale\":false"));
        // M1.5-17: envVars is skipped on serialize when the map is
        // empty so the on-disk file stays compact for the common case.
        assert!(!json.contains("envVars"));
        // M15 T6.4: renderPipeline / defaultBuildTarget are skipped on
        // serialize when `None` so legacy on-disk files stay compact.
        assert!(!json.contains("renderPipeline"));
        assert!(!json.contains("defaultBuildTarget"));
    }

    #[test]
    fn project_entry_env_vars_default_to_empty_for_legacy_json() {
        // Pre-M1.5-17 entries have no `envVars` field. The deserializer
        // must default to an empty map so legacy projects keep working
        // and the env-var panel renders an empty add-row on first
        // open. Persisting after a default load round-trips cleanly.
        let legacy = r#"{
            "id": "abc",
            "name": "Proj",
            "path": "/p"
        }"#;
        let entry: ProjectEntry = serde_json::from_str(legacy).unwrap();
        assert!(entry.env_vars.is_empty());
    }

    #[test]
    fn project_entry_frecency_defaults_to_zero_for_legacy_json() {
        // Legacy projects.json files (pre-M1.5-4) have no `frecency` key.
        // The deserializer must fill in 0 so the sort still works.
        let legacy = r#"{
            "id": "abc",
            "name": "Proj",
            "path": "/p"
        }"#;
        let entry: ProjectEntry = serde_json::from_str(legacy).unwrap();
        assert_eq!(entry.frecency, 0);
        assert!(entry.git_branch.is_none());
    }

    #[test]
    fn project_entry_source_defaults_to_manual_for_legacy_json() {
        // Pre-M1.5-11 entries have no `source` field. They must default
        // to "manual" so the walk-up chip / filter does not falsely
        // match legacy rows. New writes (via add_project, walk_up_scan,
        // the CLI flow) always set the field explicitly.
        let legacy = r#"{
            "id": "abc",
            "name": "Proj",
            "path": "/p"
        }"#;
        let entry: ProjectEntry = serde_json::from_str(legacy).unwrap();
        assert_eq!(entry.source, "manual");
    }

    #[test]
    fn project_entry_render_pipeline_defaults_to_none_for_legacy_json() {
        // M15 T6.4: pre-M15 entries have no `renderPipeline` field. The
        // deserializer must default to `None` so existing user configs
        // are not rejected and the Projects tab renders the chip only
        // once the value has been computed by `refresh_all_projects`.
        let legacy = r#"{
            "id": "abc",
            "name": "Proj",
            "path": "/p"
        }"#;
        let entry: ProjectEntry = serde_json::from_str(legacy).unwrap();
        assert!(entry.render_pipeline.is_none());
        assert!(entry.default_build_target.is_none());
    }

    #[test]
    fn project_entry_kind_defaults_to_unity_for_legacy_json() {
        // Multi-type: pre-multi-type entries have no `kind` field. They
        // must default to `Unity` so the existing Unity-only behavior is
        // preserved without a migration. The other multi-type fields
        // (packageManifestPath / migrateSourceFolder / lineCountStats)
        // all default to `None` and stay off the on-disk shape until set.
        let legacy = r#"{
            "id": "abc",
            "name": "Proj",
            "path": "/p"
        }"#;
        let entry: ProjectEntry = serde_json::from_str(legacy).unwrap();
        assert_eq!(entry.kind, ProjectKind::Unity);
        assert!(entry.package_manifest_path.is_none());
        assert!(entry.migrate_source_folder.is_none());
        assert!(entry.line_count_stats.is_none());
    }

    #[test]
    fn project_entry_kind_roundtrips_all_variants() {
        // `kind` is a non-optional field that must serialize in the
        // camelCase form the frontend expects and round-trip cleanly.
        for kind in [
            ProjectKind::Unity,
            ProjectKind::Package,
            ProjectKind::OpenMcp,
            ProjectKind::Custom,
        ] {
            let entry = ProjectEntry {
                id: "id".to_string(),
                name: "N".to_string(),
                path: "/p".to_string(),
                unity_version: None,
                last_opened_at: None,
                last_modified_at: None,
                launch_args: None,
                platform_intent: None,
                last_launch_pid: None,
                last_launch_at: None,
                frecency: 0,
                git_branch: None,
                source: "manual".to_string(),
                hidden: false,
                stale: false,
                env_vars: BTreeMap::new(),
                render_pipeline: None,
                default_build_target: None,
                kind,
                package_manifest_path: None,
                migrate_source_folder: None,
                line_count_stats: None,
            };
            let json = serde_json::to_string(&entry).unwrap();
            let restored: ProjectEntry = serde_json::from_str(&json).unwrap();
            assert_eq!(restored.kind, kind, "kind {:?} did not round-trip", kind);
        }
    }

    #[test]
    fn settings_line_count_threshold_defaults_to_30() {
        // Multi-type: the git-popup auto-calc threshold has a documented
        // default of 30 MiB and must default in for legacy settings.
        let legacy = r#"{
            "version": 1,
            "launch": { "mode": "openProject", "rememberLastSelection": true },
            "projectList": {
                "showPathColumn": true,
                "showModifiedColumn": true,
                "searchIncludesPath": true
            },
            "safety": { "confirmKillUnity": true, "confirmRemoveProject": true },
            "unityDiscovery": { "parentFolders": [] }
        }"#;
        let restored: Settings = serde_json::from_str(legacy).unwrap();
        assert_eq!(restored.line_count_auto_calc_threshold_mb, 30);
    }

    #[test]
    fn project_entry_hidden_defaults_to_false_for_legacy_json() {
        // Pre-M1.5-15 entries have no `hidden` / `stale` fields. Both
        // must default to `false` so legacy projects show up in the
        // default list view (no rows are silently hidden) and the
        // "missing or stale" filter does not falsely match them.
        let legacy = r#"{
            "id": "abc",
            "name": "Proj",
            "path": "/p"
        }"#;
        let entry: ProjectEntry = serde_json::from_str(legacy).unwrap();
        assert!(!entry.hidden);
        assert!(!entry.stale);
    }

    #[test]
    fn corrupt_json_fails_deserialization() {
        let garbage = "{ not valid json }}}";
        assert!(serde_json::from_str::<Settings>(garbage).is_err());
        assert!(serde_json::from_str::<ProjectsFile>(garbage).is_err());
    }

    #[test]
    fn partial_json_fails_deserialization() {
        assert!(serde_json::from_str::<Settings>(r#"{"version":1}"#).is_err());
        assert!(serde_json::from_str::<ProjectsFile>(r#"{"version":1}"#).is_err());
    }
}
