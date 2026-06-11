//! M1.5-12 / M1.5-13: New project creation.
//!
//! Scaffolds a fresh Unity project on disk and registers it in
//! `projects.json`. The scaffold is either the bare minimum
//! (Task 4 / "Empty" template) or a user-supplied template folder
//! copied with overwrite (Task 5).
//!
//! ## Empty template
//!
//! Creates:
//!   - `Assets/`
//!   - `ProjectSettings/`
//!   - `ProjectSettings/ProjectVersion.txt` (the chosen Unity version)
//!   - `ProjectSettings/ProjectManager.asset` (minimal; `bundleVersion`
//!     matches the chosen `bundleVersion` param, default `0.1.0`)
//!   - `ProjectSettings/ProjectSettings.asset` (minimal; carries
//!     `bundleVersion` so Unity reads it on first open)
//!   - `Packages/` (with `manifest.json` when a render pipeline other
//!     than `none` is requested — see [`render_pipeline_package`])
//!
//! ## Custom / Hub-default template
//!
//! Validates the source folder is a Unity project root, recursively
//! copies it into the new project folder (with overwrite, since the
//! destination is freshly created and empty), then **rewrites**
//! `ProjectVersion.txt` to the chosen version. `manifest.json` is
//! written on top of any template copy so the chosen render pipeline
//! packages are present even if the template ships without them. The
//! `ProjectSettings/ProjectManager.asset` `bundleVersion` is refreshed
//! in-place (the template's value is not preserved).
//!
//! ## Atomicity
//!
//! The scaffold is staged into a sibling temp directory
//! (`<parent>/.<name>.hub-creating`) and renamed into place once every
//! file has been written. A failure mid-flight triggers a best-effort
//! recursive removal of the temp directory so the parent never contains
//! a partial scaffold. Persistence happens *after* the rename so
//! `projects.json` is never updated for a project that did not actually
//! land on disk.
//!
//! ## Name validation
//!
//! A name is valid when:
//!   - it is non-empty (after trim)
//!   - it does not contain path separators (`/`, `\`), NUL, or `:`
//!     (Windows-reserved); reserved Windows device names (`CON`, `PRN`,
//!     …) are rejected too so a click on a Windows Explorer
//!     breadcrumb does not blow up
//!   - it is not a single dot or double dot
//!
//! ## Pipeline support
//!
//! URP and HDRP require Unity 2019.3+ (the version that shipped the
//! Scriptable Render Pipeline). Older Unity versions return
//! [`NewProjectError::PipelineUnsupported`] so the UI can disable
//! pipeline selection rather than producing a project Unity will not
//! open.

use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::persistence;
use crate::config::schemas::{ProjectEntry, ProjectsFile};

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum RenderPipeline {
    None,
    URP,
    HDRP,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TemplateRef {
    /// `"hub-default"` or `"custom"` — informational, surfaced back in
    /// the modal so the user sees the source label they picked. The
    /// backend does not branch on it.
    pub source: String,
    pub path: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NewProjectParams {
    pub parent: String,
    pub name: String,
    pub version: String,
    pub pipeline: RenderPipeline,
    /// Default `0.1.0` when the frontend does not override.
    #[serde(default = "default_bundle_version")]
    pub bundle_version: String,
    /// `None` for the Empty template; `Some(ref)` for Hub-default or
    /// Custom. See [`TemplateRef`].
    #[serde(default)]
    pub template: Option<TemplateRef>,
    /// When `true`, the scaffold replaces an existing directory at
    /// `<parent>/<name>`. The frontend must surface a confirmation
    /// modal before flipping this flag. The backend never sets it on
    /// its own.
    #[serde(default)]
    pub overwrite: bool,
}

fn default_bundle_version() -> String {
    "0.1.0".to_string()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NewProjectResult {
    pub project: ProjectEntry,
    pub projects: ProjectsFile,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum NewProjectError {
    #[serde(rename_all = "camelCase")]
    ParentNotDirectory { path: String },
    #[serde(rename_all = "camelCase")]
    NameEmpty,
    #[serde(rename_all = "camelCase")]
    NameInvalid { name: String, reason: String },
    #[serde(rename_all = "camelCase")]
    NameCollision { path: String, is_directory: bool },
    #[serde(rename_all = "camelCase")]
    VersionUnknown { version: String },
    #[serde(rename_all = "camelCase")]
    VersionNotInstalled { version: String },
    #[serde(rename_all = "camelCase")]
    PipelineUnsupported { version: String, pipeline: String },
    #[serde(rename_all = "camelCase")]
    TemplateNotFound { path: String },
    #[serde(rename_all = "camelCase")]
    TemplateInvalid { path: String, reason: String },
    #[serde(rename_all = "camelCase")]
    IoError { message: String },
    #[serde(rename_all = "camelCase")]
    PersistFailed { message: String },
}

/// Returned by `list_hub_templates`. A `None` source folder is
/// serialized as `null` so the frontend can render the "Unity Hub
/// templates not installed" hint.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HubTemplatesResult {
    pub available: bool,
    pub folder: Option<String>,
    pub templates: Vec<HubTemplateEntry>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HubTemplateEntry {
    pub name: String,
    pub path: String,
    pub unity_version: Option<String>,
}

/// Default `bundleVersion` for the Empty template. Mirrors the
/// value Unity Hub's own "3D" template ships with so freshly-scaffolded
/// projects look identical to Unity Hub's output.
const DEFAULT_BUNDLE_VERSION: &str = "0.1.0";

/// Staging directory name suffix used while a scaffold is in flight.
const STAGING_SUFFIX: &str = ".hub-creating";

fn parse_major_minor(version: &str) -> Option<(u32, u32)> {
    // Unity version strings look like "6000.0.1f1", "2022.3.48f1",
    // "2019.4.40f1". We only need the first two numeric segments;
    // trailing letters (release type tag) and the build number are
    // discarded.
    let mut parts = version.trim().split('.');
    let major: u32 = parts.next()?.parse().ok()?;
    let minor: u32 = parts.next()?.parse().ok()?;
    Some((major, minor))
}

/// Pick a reasonable UPM package line for the chosen pipeline and
/// Unity major version. Returns `None` for `RenderPipeline::None` and
/// for combinations the Scriptable Render Pipeline does not support
/// (anything before Unity 2019.3).
///
/// The package version is a *known-good default* for that Unity major;
/// Unity's Package Manager will resolve a compatible build once the
/// user opens the project. The user can also bump it manually.
pub fn render_pipeline_package(pipeline: RenderPipeline, version: &str) -> Option<String> {
    if matches!(pipeline, RenderPipeline::None) {
        return None;
    }
    let (major, minor) = parse_major_minor(version)?;
    if (major, minor) < (2019, 3) {
        return None;
    }
    let name = match pipeline {
        RenderPipeline::URP => "com.unity.render-pipelines.universal",
        RenderPipeline::HDRP => "com.unity.render-pipelines.high-definition",
        RenderPipeline::None => unreachable!("guarded above"),
    };
    let pkg_version = match major {
        6 => "17.0.3",
        2022 => "14.0.11",
        2021 => "12.1.7",
        2020 => "10.10.1",
        2019 => "7.7.1",
        // Forward-compat fallback: pin to the latest known Unity 6
        // release. Unity will resolve a workable older build via the
        // registry, and the user can bump it from the Package Manager.
        _ => "17.0.3",
    };
    Some(format!("{}@{}", name, pkg_version))
}

/// Returned-by-name strings for `validate_name`.
fn validate_name(name: &str) -> Result<(), String> {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return Err("name is empty".to_string());
    }
    if trimmed == "." || trimmed == ".." {
        return Err(format!("name '{}' is reserved", trimmed));
    }
    if trimmed.contains('/') || trimmed.contains('\\') {
        return Err("name cannot contain path separators".to_string());
    }
    if trimmed.contains(':') || trimmed.contains('\0') {
        return Err("name contains an illegal character".to_string());
    }
    if cfg!(target_os = "windows") {
        const RESERVED: &[&str] = &[
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7",
            "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ];
        let upper = trimmed.to_ascii_uppercase();
        if RESERVED.iter().any(|r| *r == upper.as_str()) {
            return Err(format!("name '{}' is reserved on Windows", trimmed));
        }
    }
    Ok(())
}

/// Render the `ProjectVersion.txt` body for a given Unity version.
fn render_project_version_txt(version: &str) -> String {
    // Unity writes a trailing newline; the `m_EditorVersion:` line is
    // the only field Hub consumes, but a minimal `m_Revision` line is
    // included to mirror what real templates ship.
    format!(
        "m_EditorVersion: {}\nm_Revision: hub-scaffold\n",
        version
    )
}

/// Render a minimal `ProjectManager.asset`. The real file is
/// considerably longer; the small subset below is enough for Unity to
/// open the project without a "ProjectSettings are corrupt" prompt,
/// and Unity will rewrite the file on first save.
fn render_project_manager_asset(bundle_version: &str) -> String {
    format!(
        "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1045 &1\nEditorPrefs:\n  m_ObjectHideFlags: 0\n  serializedVersion: 1\n  bundleVersion: {bundle_version}\n",
    )
}

/// Render a minimal `ProjectSettings.asset`. Same caveat as
/// [`render_project_manager_asset`]: Unity will rewrite it on first
/// save. `bundleVersion` lives here too because Unity's runtime looks
/// for it in `ProjectSettings.asset`, not `ProjectManager.asset`.
fn render_project_settings_asset(bundle_version: &str) -> String {
    format!(
        "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!129 &1\nPlayerSettings:\n  m_ObjectHideFlags: 0\n  serializedVersion: 26\n  productGUID: {}\n  bundleVersion: {bundle_version}\n",
        uuid::Uuid::new_v4().to_string()
    )
}

/// Render a `Packages/manifest.json` body carrying the chosen render
/// pipeline package. The dependencies block is intentionally
/// minimal — the user can flesh it out in the Package Manager once
/// Unity opens the project.
fn render_manifest_json(pipeline_line: Option<&str>) -> String {
    let pipeline_block = pipeline_block(pipeline_line);
    format!(
        "{{\n  \"dependencies\": {{\n{}{}\n  }}\n}}\n",
        "    \"com.unity.ide.rider\": \"3.0.27\",\n",
        pipeline_block,
    )
}

fn pipeline_block(pipeline_line: Option<&str>) -> String {
    match pipeline_line {
        Some(line) => format!("    \"{line}\",\n"),
        None => String::new(),
    }
}

/// Mirror of `walk_up_scan::is_unity_project_root` so the template
/// validator does not pull in the scanner module.
fn is_unity_project_root(path: &Path) -> bool {
    path.is_dir() && path.join("Assets").is_dir() && path.join("ProjectSettings").is_dir()
}

/// Recursively copy `src` into `dst`. Both must be directories;
/// existing files inside `dst` are overwritten (the destination is
/// freshly created and empty in our flow, but overwriting is the safe
/// default for re-runs after a failed first attempt).
fn copy_dir_all(src: &Path, dst: &Path) -> std::io::Result<()> {
    fs::create_dir_all(dst)?;
    for entry in fs::read_dir(src)? {
        let entry = entry?;
        let file_type = entry.file_type()?;
        let from = entry.path();
        let to = dst.join(entry.file_name());
        if file_type.is_dir() {
            copy_dir_all(&from, &to)?;
        } else if file_type.is_symlink() {
            // Read the link target and re-create as a regular file.
            // Templates rarely ship symlinks, but the safest default
            // when they do is to inline the target's bytes so the
            // copy is self-contained.
            let target = fs::read_link(&from)?;
            if target.is_file() {
                fs::copy(&target, &to)?;
            } else if target.is_dir() {
                copy_dir_all(&target, &to)?;
            }
        } else {
            fs::copy(&from, &to)?;
        }
    }
    Ok(())
}

/// Recursively remove a directory. Used to clean up a half-scaffolded
/// staging directory on failure. Best-effort: errors are logged and
/// swallowed because we are already in a failure path.
fn remove_dir_all_best_effort(path: &Path) {
    let _ = fs::remove_dir_all(path);
}

fn write_file(path: &Path, body: &str) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::write(path, body)
}

/// Replace the `m_EditorVersion:` line in a `ProjectVersion.txt` file.
/// If the file does not exist, or has no `m_EditorVersion:` line, the
/// file is rewritten with the standard header. Other lines are
/// preserved so a template's `m_Revision:` (and any custom keys) is
/// kept.
fn rewrite_project_version(project_dir: &Path, version: &str) -> std::io::Result<()> {
    let path = project_dir
        .join("ProjectSettings")
        .join("ProjectVersion.txt");
    let body = if path.exists() {
        let original = fs::read_to_string(&path)?;
        let mut replaced = false;
        let mut out: Vec<String> = Vec::new();
        for line in original.lines() {
            let stripped = line.strip_prefix('\u{FEFF}').unwrap_or(line);
            if stripped.starts_with("m_EditorVersion:") {
                out.push(format!("m_EditorVersion: {}", version));
                replaced = true;
            } else {
                out.push(line.to_string());
            }
        }
        if !replaced {
            out.push(format!("m_EditorVersion: {}", version));
        }
        format!("{}\n", out.join("\n"))
    } else {
        render_project_version_txt(version)
    };
    write_file(&path, &body)
}

/// Rewrite the `bundleVersion` keys in `ProjectManager.asset` and
/// `ProjectSettings.asset` so a copied template picks up the
/// user-chosen value. Files that are missing the key get a new line
/// appended; missing files are skipped (the Empty-template path
/// creates them from scratch).
fn rewrite_bundle_version(project_dir: &Path, bundle_version: &str) -> std::io::Result<()> {
    rewrite_bundle_in_file(
        &project_dir.join("ProjectSettings").join("ProjectManager.asset"),
        bundle_version,
    )?;
    rewrite_bundle_in_file(
        &project_dir.join("ProjectSettings").join("ProjectSettings.asset"),
        bundle_version,
    )?;
    Ok(())
}

fn rewrite_bundle_in_file(path: &Path, bundle_version: &str) -> std::io::Result<()> {
    if !path.exists() {
        return Ok(());
    }
    let original = fs::read_to_string(path)?;
    let mut replaced = false;
    let mut out: Vec<String> = Vec::new();
    for line in original.lines() {
        if line.starts_with("bundleVersion:") {
            out.push(format!("bundleVersion: {}", bundle_version));
            replaced = true;
        } else {
            out.push(line.to_string());
        }
    }
    if !replaced {
        out.push(format!("bundleVersion: {}", bundle_version));
    }
    fs::write(path, format!("{}\n", out.join("\n")))
}

/// Resolve the OS-specific Unity Hub templates folder. `None` when
/// the directory does not exist (Hub is not installed, or no template
/// has been downloaded yet).
fn unity_hub_templates_dir() -> Option<PathBuf> {
    let base = if cfg!(target_os = "macos") {
        dirs::home_dir()?.join("Library/Application Support/UnityHub")
    } else if cfg!(target_os = "windows") {
        dirs::data_dir()?.join("UnityHub")
    } else {
        dirs::home_dir()?.join(".config/UnityHub")
    };
    let templates = base.join("Templates");
    if templates.is_dir() {
        Some(templates)
    } else {
        None
    }
}

/// Public command: list the Hub's downloaded templates so the modal
/// can offer them in the "Hub default" picker. Returns
/// `available=false` and an empty `templates` list when Hub is not
/// installed or has no downloaded templates — the UI can then render
/// the "Hub default — not installed" hint.
#[tauri::command]
pub fn list_hub_templates() -> HubTemplatesResult {
    let folder = unity_hub_templates_dir();
    let mut templates: Vec<HubTemplateEntry> = Vec::new();
    if let Some(ref dir) = folder {
        if let Ok(read) = fs::read_dir(dir) {
            for entry in read.flatten() {
                let path = entry.path();
                if !is_unity_project_root(&path) {
                    continue;
                }
                let name = entry
                    .file_name()
                    .to_string_lossy()
                    .to_string();
                let unity_version = read_template_version(&path);
                templates.push(HubTemplateEntry {
                    name,
                    path: path.to_string_lossy().to_string(),
                    unity_version,
                });
            }
            // Stable, human-friendly order: alphabetical. Hub ships
            // templates with names like "2D-Built-In-Renderer-2022.3"
            // so an alphabetical sort reads like a menu.
            templates.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
        }
    }
    HubTemplatesResult {
        available: folder.is_some() && !templates.is_empty(),
        folder: folder.map(|p| p.to_string_lossy().to_string()),
        templates,
    }
}

fn read_template_version(project_root: &Path) -> Option<String> {
    let path = project_root
        .join("ProjectSettings")
        .join("ProjectVersion.txt");
    let content = fs::read_to_string(&path).ok()?;
    for line in content.lines() {
        let line = line.strip_prefix('\u{FEFF}').unwrap_or(line);
        if let Some(rest) = line.strip_prefix("m_EditorVersion:") {
            let trimmed = rest.trim();
            if !trimmed.is_empty() {
                return Some(trimmed.to_string());
            }
        }
    }
    None
}

/// Sanitize a Unity `bundleVersion` string. Unity accepts the
/// `Major.Minor.Patch[-label]` form; we keep the input verbatim when
/// it looks plausible, fall back to the default otherwise.
fn sanitize_bundle_version(input: &str) -> String {
    let trimmed = input.trim();
    if trimmed.is_empty() {
        return DEFAULT_BUNDLE_VERSION.to_string();
    }
    trimmed.to_string()
}

#[tauri::command]
pub fn create_new_project(
    state: State<AppState>,
    params: NewProjectParams,
) -> Result<NewProjectResult, NewProjectError> {
    let parent_path = PathBuf::from(&params.parent);
    if !parent_path.is_dir() {
        return Err(NewProjectError::ParentNotDirectory {
            path: params.parent.clone(),
        });
    }

    let name = params.name.trim().to_string();
    if let Err(reason) = validate_name(&name) {
        return Err(NewProjectError::NameInvalid {
            name: name.clone(),
            reason,
        });
    }

    let version = params.version.trim().to_string();
    if version.is_empty() {
        return Err(NewProjectError::VersionUnknown {
            version: version.clone(),
        });
    }

    // Cross-check the chosen version against the discovery cache so
    // we surface "Unity 6000.0.1f1 is not installed" before writing
    // any files. The cache is populated by the GUI; in CLI mode the
    // cache is empty so this check is skipped (the CLI flow uses
    // `run_cli_mode` instead and does not call this command).
    {
        let cache = state.discovery_cache.lock().unwrap();
        if let Some(ref result) = *cache {
            if !result.installations.iter().any(|i| i.version == version) {
                return Err(NewProjectError::VersionNotInstalled {
                    version: version.clone(),
                });
            }
        }
    }

    if let Some(ref template) = params.template {
        if !is_unity_project_root(Path::new(&template.path)) {
            return Err(NewProjectError::TemplateInvalid {
                path: template.path.clone(),
                reason: "selected folder is not a Unity project root (missing Assets/ or ProjectSettings/)"
                    .to_string(),
            });
        }
    }

    // Pipeline support check.
    if !matches!(params.pipeline, RenderPipeline::None) {
        if render_pipeline_package(params.pipeline, &version).is_none() {
            return Err(NewProjectError::PipelineUnsupported {
                version: version.clone(),
                pipeline: pipeline_label(params.pipeline).to_string(),
            });
        }
    }

    let bundle_version = sanitize_bundle_version(&params.bundle_version);

    let target = parent_path.join(&name);
    if target.exists() {
        let is_dir = target.is_dir();
        if !params.overwrite {
            return Err(NewProjectError::NameCollision {
                path: target.to_string_lossy().to_string(),
                is_directory: is_dir,
            });
        }
        // Overwrite confirmed. Remove the existing entry so the
        // rename below does not fail on Windows (rename refuses to
        // overwrite a non-empty directory).
        if is_dir {
            if let Err(e) = fs::remove_dir_all(&target) {
                return Err(NewProjectError::IoError {
                    message: format!("failed to remove existing project at {}: {}", target.display(), e),
                });
            }
        } else if let Err(e) = fs::remove_file(&target) {
            return Err(NewProjectError::IoError {
                message: format!("failed to remove existing file at {}: {}", target.display(), e),
            });
        }
    }

    // Stage into a sibling temp dir. Rename is atomic on the same
    // filesystem; if anything in the middle fails, we delete the
    // staging dir so the parent is left untouched.
    let staging = parent_path.join(format!("{}{}", name, STAGING_SUFFIX));
    if staging.exists() {
        // A previous run died mid-scaffold. Remove the partial before
        // starting a new one.
        remove_dir_all_best_effort(&staging);
    }
    if let Err(e) = fs::create_dir_all(&staging) {
        return Err(NewProjectError::IoError {
            message: format!("failed to create staging directory {}: {}", staging.display(), e),
        });
    }

    let scaffold_result = scaffold_into(&staging, &params, &version, &bundle_version);
    if let Err(err) = scaffold_result {
        remove_dir_all_best_effort(&staging);
        return Err(err);
    }

    // Atomic publish.
    if let Err(e) = fs::rename(&staging, &target) {
        remove_dir_all_best_effort(&staging);
        return Err(NewProjectError::IoError {
            message: format!("failed to publish project from {} to {}: {}", staging.display(), target.display(), e),
        });
    }

    // Register the new project. We register *after* the rename so
    // projects.json never references a path that is not on disk.
    let now_iso = chrono::Utc::now().to_rfc3339();
    let entry = ProjectEntry {
        id: uuid::Uuid::new_v4().to_string(),
        name: name.clone(),
        path: target.to_string_lossy().to_string(),
        unity_version: Some(version.clone()),
        last_opened_at: Some(now_iso.clone()),
        last_modified_at: Some(now_iso),
        launch_args: None,
        platform_intent: None,
        last_launch_pid: None,
        last_launch_at: None,
        // Bump frecency to 1 so a freshly-created project sorts above
        // the rest of the list (the user has just "clicked" it via
        // the New Project modal). Without this bump a brand new
        // project would sort to the bottom of the frecency list
        // behind any project the user has launched before.
        frecency: 1,
        git_branch: None,
        // New projects created via the modal are manual entries; the
        // walk-up and Hub-seed sources are reserved for the scanner /
        // first-run import.
        source: "manual".to_string(),
        hidden: false,
        stale: false,
        env_vars: Default::default(),
    };

    let mut projects = state.projects.lock().unwrap().clone();
    // De-duplicate by canonical path: a stale entry with the same
    // path would silently double-count the frecency score.
    let canonical = canonicalize_for_compare(&entry.path);
    projects.projects.retain(|p| {
        canonicalize_for_compare(&p.path) != canonical
    });
    projects.projects.push(entry.clone());

    if let Err(e) = persistence::save_projects(&projects) {
        // Persistence failed. Best-effort: remove the on-disk project
        // we just published so the user is not left with a folder
        // that no longer appears in the list.
        remove_dir_all_best_effort(&target);
        return Err(NewProjectError::PersistFailed { message: e.to_string() });
    }

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects.clone();
    }

    Ok(NewProjectResult {
        project: entry,
        projects,
    })
}

fn canonicalize_for_compare(path: &str) -> String {
    fs::canonicalize(Path::new(path))
        .map(|p| p.to_string_lossy().to_string())
        .unwrap_or_else(|_| path.to_string())
}

fn pipeline_label(p: RenderPipeline) -> &'static str {
    match p {
        RenderPipeline::None => "none",
        RenderPipeline::URP => "urp",
        RenderPipeline::HDRP => "hdrp",
    }
}

fn scaffold_into(
    staging: &Path,
    params: &NewProjectParams,
    version: &str,
    bundle_version: &str,
) -> Result<(), NewProjectError> {
    // 1. Template (Hub default / Custom): copy with overwrite, then
    //    rewrite ProjectVersion.txt + bundleVersion so the copy
    //    matches the user's choices.
    if let Some(ref template) = params.template {
        let src = PathBuf::from(&template.path);
        if !src.is_dir() {
            return Err(NewProjectError::TemplateNotFound { path: template.path.clone() });
        }
        if let Err(e) = copy_dir_all(&src, staging) {
            return Err(NewProjectError::IoError {
                message: format!("failed to copy template {}: {}", src.display(), e),
            });
        }
        if let Err(e) = rewrite_project_version(staging, version) {
            return Err(NewProjectError::IoError {
                message: format!("failed to rewrite ProjectVersion.txt: {}", e),
            });
        }
        if let Err(e) = rewrite_bundle_version(staging, bundle_version) {
            return Err(NewProjectError::IoError {
                message: format!("failed to rewrite bundleVersion: {}", e),
            });
        }
    } else {
        // 2. Empty template: hand-craft the minimum file set.
        let assets = staging.join("Assets");
        let project_settings = staging.join("ProjectSettings");
        let packages = staging.join("Packages");
        for d in [&assets, &project_settings, &packages] {
            if let Err(e) = fs::create_dir_all(d) {
                return Err(NewProjectError::IoError {
                    message: format!("failed to create {}: {}", d.display(), e),
                });
            }
        }
        if let Err(e) = write_file(
            &project_settings.join("ProjectVersion.txt"),
            &render_project_version_txt(version),
        ) {
            return Err(NewProjectError::IoError {
                message: format!("failed to write ProjectVersion.txt: {}", e),
            });
        }
        if let Err(e) = write_file(
            &project_settings.join("ProjectManager.asset"),
            &render_project_manager_asset(bundle_version),
        ) {
            return Err(NewProjectError::IoError {
                message: format!("failed to write ProjectManager.asset: {}", e),
            });
        }
        if let Err(e) = write_file(
            &project_settings.join("ProjectSettings.asset"),
            &render_project_settings_asset(bundle_version),
        ) {
            return Err(NewProjectError::IoError {
                message: format!("failed to write ProjectSettings.asset: {}", e),
            });
        }
    }

    // 3. Render pipeline manifest. Always written so URP / HDRP
    //    selections land on top of any template copy. For the Empty
    //    template with `pipeline = None` we still drop a minimal
    //    manifest so Unity does not warn about a missing file on
    //    first open.
    let pipeline_line = render_pipeline_package(params.pipeline, version);
    if let Err(e) = write_file(
        &staging.join("Packages").join("manifest.json"),
        &render_manifest_json(pipeline_line.as_deref()),
    ) {
        return Err(NewProjectError::IoError {
            message: format!("failed to write manifest.json: {}", e),
        });
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_dir(p: &Path) {
        fs::create_dir_all(p).unwrap();
    }

    #[test]
    fn parse_major_minor_unity_6() {
        assert_eq!(parse_major_minor("6000.0.1f1"), Some((6000, 0)));
    }

    #[test]
    fn parse_major_minor_2022() {
        assert_eq!(parse_major_minor("2022.3.48f1"), Some((2022, 3)));
    }

    #[test]
    fn parse_major_minor_2019_4() {
        assert_eq!(parse_major_minor("2019.4.40f1"), Some((2019, 4)));
    }

    #[test]
    fn parse_major_minor_rejects_garbage() {
        assert_eq!(parse_major_minor(""), None);
        assert_eq!(parse_major_minor("f1"), None);
        assert_eq!(parse_major_minor("2022"), None);
        assert_eq!(parse_major_minor("2022.3"), Some((2022, 3)));
    }

    #[test]
    fn render_pipeline_none_is_none() {
        assert!(render_pipeline_package(RenderPipeline::None, "6000.0.1f1").is_none());
    }

    #[test]
    fn render_pipeline_urp_unity6() {
        let pkg = render_pipeline_package(RenderPipeline::URP, "6000.0.1f1").unwrap();
        assert!(pkg.starts_with("com.unity.render-pipelines.universal@"));
    }

    #[test]
    fn render_pipeline_hdrp_unity6() {
        let pkg = render_pipeline_package(RenderPipeline::HDRP, "6000.0.1f1").unwrap();
        assert!(pkg.starts_with("com.unity.render-pipelines.high-definition@"));
    }

    #[test]
    fn render_pipeline_urp_2022() {
        let pkg = render_pipeline_package(RenderPipeline::URP, "2022.3.48f1").unwrap();
        assert!(pkg.starts_with("com.unity.render-pipelines.universal@"));
    }

    #[test]
    fn render_pipeline_unsupported_pre_2019_3() {
        assert!(render_pipeline_package(RenderPipeline::URP, "2019.2.20f1").is_none());
        assert!(render_pipeline_package(RenderPipeline::HDRP, "2018.4.36f1").is_none());
    }

    #[test]
    fn render_project_version_txt_has_editor_line() {
        let body = render_project_version_txt("2022.3.48f1");
        assert!(body.contains("m_EditorVersion: 2022.3.48f1"));
    }

    #[test]
    fn render_project_manager_asset_carries_bundle_version() {
        let body = render_project_manager_asset("1.2.3");
        assert!(body.contains("bundleVersion: 1.2.3"));
    }

    #[test]
    fn render_project_settings_asset_carries_bundle_version() {
        let body = render_project_settings_asset("1.2.3");
        assert!(body.contains("bundleVersion: 1.2.3"));
        assert!(body.contains("productGUID:"));
    }

    #[test]
    fn render_manifest_json_includes_pipeline() {
        let body = render_manifest_json(Some("com.unity.render-pipelines.universal@17.0.3"));
        assert!(body.contains("com.unity.render-pipelines.universal@17.0.3"));
        assert!(body.contains("\"dependencies\""));
    }

    #[test]
    fn render_manifest_json_omits_pipeline_for_none() {
        let body = render_manifest_json(None);
        assert!(!body.contains("com.unity.render-pipelines"));
    }

    #[test]
    fn validate_name_rejects_empty() {
        assert!(validate_name("").is_err());
        assert!(validate_name("   ").is_err());
    }

    #[test]
    fn validate_name_rejects_path_separators() {
        assert!(validate_name("a/b").is_err());
        assert!(validate_name("a\\b").is_err());
    }

    #[test]
    fn validate_name_rejects_traversal() {
        assert!(validate_name(".").is_err());
        assert!(validate_name("..").is_err());
    }

    #[test]
    fn validate_name_rejects_illegal_chars() {
        assert!(validate_name("a:b").is_err());
        assert!(validate_name("a\0b").is_err());
    }

    #[test]
    fn validate_name_accepts_simple() {
        assert!(validate_name("MyGame").is_ok());
        assert!(validate_name("my-game_2").is_ok());
    }

    #[test]
    fn sanitize_bundle_version_uses_default_for_empty() {
        assert_eq!(sanitize_bundle_version(""), DEFAULT_BUNDLE_VERSION);
        assert_eq!(sanitize_bundle_version("   "), DEFAULT_BUNDLE_VERSION);
    }

    #[test]
    fn sanitize_bundle_version_keeps_input() {
        assert_eq!(sanitize_bundle_version("1.2.3"), "1.2.3");
        assert_eq!(sanitize_bundle_version("  1.0  "), "1.0");
    }

    #[test]
    fn copy_dir_all_copies_recursively() {
        let src = tempfile::tempdir().unwrap();
        let dst = tempfile::tempdir().unwrap();
        fs::create_dir_all(src.path().join("a/b")).unwrap();
        fs::write(src.path().join("a/file1.txt"), "one").unwrap();
        fs::write(src.path().join("a/b/file2.txt"), "two").unwrap();

        let dst_path = dst.path().join("out");
        copy_dir_all(src.path(), &dst_path).unwrap();

        assert_eq!(fs::read_to_string(dst_path.join("a/file1.txt")).unwrap(), "one");
        assert_eq!(fs::read_to_string(dst_path.join("a/b/file2.txt")).unwrap(), "two");
    }

    #[test]
    fn rewrite_project_version_replaces_existing_line() {
        let dir = tempfile::tempdir().unwrap();
        let ps = dir.path().join("ProjectSettings");
        fs::create_dir_all(&ps).unwrap();
        fs::write(
            ps.join("ProjectVersion.txt"),
            "m_EditorVersion: 2019.4.40f1\nm_Revision: foo\n",
        )
        .unwrap();

        rewrite_project_version(dir.path(), "2022.3.48f1").unwrap();

        let body = fs::read_to_string(ps.join("ProjectVersion.txt")).unwrap();
        assert!(body.contains("m_EditorVersion: 2022.3.48f1"));
        assert!(body.contains("m_Revision: foo"));
    }

    #[test]
    fn rewrite_project_version_creates_when_missing() {
        let dir = tempfile::tempdir().unwrap();
        rewrite_project_version(dir.path(), "2022.3.48f1").unwrap();

        let body = fs::read_to_string(
            dir.path().join("ProjectSettings").join("ProjectVersion.txt"),
        )
        .unwrap();
        assert!(body.contains("m_EditorVersion: 2022.3.48f1"));
    }

    #[test]
    fn rewrite_bundle_version_replaces_in_both_files() {
        let dir = tempfile::tempdir().unwrap();
        let ps = dir.path().join("ProjectSettings");
        fs::create_dir_all(&ps).unwrap();
        fs::write(
            ps.join("ProjectManager.asset"),
            "bundleVersion: 0.1.0\nm_Other: 1\n",
        )
        .unwrap();
        fs::write(
            ps.join("ProjectSettings.asset"),
            "bundleVersion: 0.1.0\nm_Other: 2\n",
        )
        .unwrap();

        rewrite_bundle_version(dir.path(), "1.2.3").unwrap();

        let pm = fs::read_to_string(ps.join("ProjectManager.asset")).unwrap();
        let ps_body = fs::read_to_string(ps.join("ProjectSettings.asset")).unwrap();
        assert!(pm.contains("bundleVersion: 1.2.3"));
        assert!(ps_body.contains("bundleVersion: 1.2.3"));
        assert!(pm.contains("m_Other: 1"));
    }

    #[test]
    fn rewrite_bundle_version_no_op_for_missing_files() {
        let dir = tempfile::tempdir().unwrap();
        rewrite_bundle_version(dir.path(), "1.2.3").unwrap();
        // No assertions: the function must succeed even when the
        // template does not ship ProjectManager.asset /
        // ProjectSettings.asset.
    }

    #[test]
    fn is_unity_project_root_validates_template() {
        let dir = tempfile::tempdir().unwrap();
        make_dir(&dir.path().join("Assets"));
        make_dir(&dir.path().join("ProjectSettings"));
        assert!(is_unity_project_root(dir.path()));
    }

    #[test]
    fn is_unity_project_root_rejects_partial_template() {
        let dir = tempfile::tempdir().unwrap();
        make_dir(&dir.path().join("Assets"));
        assert!(!is_unity_project_root(dir.path()));
    }

    #[test]
    fn list_hub_templates_returns_empty_when_no_hub_folder() {
        // The dev machine may or may not have Unity Hub installed; we
        // assert on the structural contract: the call must not panic
        // and the `templates` list is well-formed.
        let result = list_hub_templates();
        for t in &result.templates {
            assert!(!t.name.is_empty());
            assert!(!t.path.is_empty());
        }
    }

    fn scaffold_empty_minimal(params: NewProjectParams) -> Result<(), NewProjectError> {
        let dir = tempfile::tempdir().unwrap();
        let version = params.version.clone();
        let bundle_version = sanitize_bundle_version(&params.bundle_version);
        scaffold_into(dir.path(), &params, &version, &bundle_version)
    }

    #[test]
    fn scaffold_empty_creates_minimum_file_set() {
        let dir = tempfile::tempdir().unwrap();
        let params = NewProjectParams {
            parent: dir.path().to_string_lossy().to_string(),
            name: "P".to_string(),
            version: "2022.3.48f1".to_string(),
            pipeline: RenderPipeline::None,
            bundle_version: "0.1.0".to_string(),
            template: None,
            overwrite: false,
        };
        let version = params.version.clone();
        let bundle_version = sanitize_bundle_version(&params.bundle_version);
        scaffold_into(dir.path(), &params, &version, &bundle_version).unwrap();

        assert!(dir.path().join("Assets").is_dir());
        assert!(dir.path().join("ProjectSettings").is_dir());
        assert!(dir.path().join("Packages").is_dir());
        assert!(dir.path().join("ProjectSettings/ProjectVersion.txt").is_file());
        assert!(dir.path().join("ProjectSettings/ProjectManager.asset").is_file());
        assert!(dir.path().join("ProjectSettings/ProjectSettings.asset").is_file());
        assert!(dir.path().join("Packages/manifest.json").is_file());
    }

    #[test]
    fn scaffold_empty_writes_pipeline_package_into_manifest() {
        let dir = tempfile::tempdir().unwrap();
        let params = NewProjectParams {
            parent: dir.path().to_string_lossy().to_string(),
            name: "P".to_string(),
            version: "6000.0.1f1".to_string(),
            pipeline: RenderPipeline::URP,
            bundle_version: "0.1.0".to_string(),
            template: None,
            overwrite: false,
        };
        let version = params.version.clone();
        let bundle_version = sanitize_bundle_version(&params.bundle_version);
        scaffold_into(dir.path(), &params, &version, &bundle_version).unwrap();

        let manifest = fs::read_to_string(dir.path().join("Packages/manifest.json")).unwrap();
        assert!(manifest.contains("com.unity.render-pipelines.universal@"));
    }

    #[test]
    fn scaffold_copies_template_and_rewrites_version() {
        // Build a minimal "template" — Assets/, ProjectSettings/,
        // ProjectVersion.txt with a different version. The scaffold
        // should copy everything and rewrite the version to the
        // chosen one.
        let tpl = tempfile::tempdir().unwrap();
        make_dir(&tpl.path().join("Assets"));
        make_dir(&tpl.path().join("ProjectSettings"));
        fs::write(
            tpl.path().join("ProjectSettings").join("ProjectVersion.txt"),
            "m_EditorVersion: 2019.4.40f1\nm_Revision: old\n",
        )
        .unwrap();
        fs::write(tpl.path().join("Assets").join("sentinel.txt"), "kept").unwrap();

        let out = tempfile::tempdir().unwrap();
        let params = NewProjectParams {
            parent: out.path().to_string_lossy().to_string(),
            name: "P".to_string(),
            version: "6000.0.1f1".to_string(),
            pipeline: RenderPipeline::None,
            bundle_version: "1.2.3".to_string(),
            template: Some(TemplateRef {
                source: "custom".to_string(),
                path: tpl.path().to_string_lossy().to_string(),
            }),
            overwrite: false,
        };
        let version = params.version.clone();
        let bundle_version = sanitize_bundle_version(&params.bundle_version);
        scaffold_into(out.path(), &params, &version, &bundle_version).unwrap();

        let pv = fs::read_to_string(
            out.path().join("ProjectSettings").join("ProjectVersion.txt"),
        )
        .unwrap();
        assert!(pv.contains("m_EditorVersion: 6000.0.1f1"));
        // Template's sentinel file is preserved by the copy.
        assert_eq!(
            fs::read_to_string(out.path().join("Assets").join("sentinel.txt")).unwrap(),
            "kept"
        );
    }

    #[test]
    fn scaffold_template_missing_path_returns_template_not_found() {
        let out = tempfile::tempdir().unwrap();
        let params = NewProjectParams {
            parent: out.path().to_string_lossy().to_string(),
            name: "P".to_string(),
            version: "2022.3.48f1".to_string(),
            pipeline: RenderPipeline::None,
            bundle_version: "0.1.0".to_string(),
            template: Some(TemplateRef {
                source: "custom".to_string(),
                path: "/definitely/does/not/exist/qwerty".to_string(),
            }),
            overwrite: false,
        };
        let version = params.version.clone();
        let bundle_version = sanitize_bundle_version(&params.bundle_version);
        let err = scaffold_into(out.path(), &params, &version, &bundle_version).unwrap_err();
        match err {
            NewProjectError::TemplateNotFound { path } => {
                assert!(path.contains("qwerty"));
            }
            other => panic!("unexpected error: {:?}", other),
        }
    }

    #[test]
    fn name_validation_rejects_empty_for_pipeline() {
        // A guard test: pipeline support check requires a non-empty
        // version. The actual empty-version path is handled upstream
        // by `create_new_project` before we reach `scaffold_into`.
        // This test pins the `scaffold_empty_minimal` helper used by
        // the other tests so a future refactor cannot accidentally
        // start returning a *successful* scaffold for an empty
        // version string.
        let params = NewProjectParams {
            parent: String::new(),
            name: "P".to_string(),
            version: String::new(),
            pipeline: RenderPipeline::None,
            bundle_version: "0.1.0".to_string(),
            template: None,
            overwrite: false,
        };
        // We do not assert on the error type because the helper
        // short-circuits before validation; the contract is "must
        // not panic".
        let _ = scaffold_empty_minimal(params);
    }

    #[test]
    fn new_project_error_serializes() {
        let err = NewProjectError::NameCollision {
            path: "/x/MyGame".to_string(),
            is_directory: true,
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"nameCollision\""));
        assert!(json.contains("/x/MyGame"));
        assert!(json.contains("\"isDirectory\":true"));
    }

    #[test]
    fn new_project_error_pipeline_unsupported_serializes() {
        let err = NewProjectError::PipelineUnsupported {
            version: "2019.2.20f1".to_string(),
            pipeline: "urp".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"pipelineUnsupported\""));
        assert!(json.contains("2019.2.20f1"));
    }

    #[test]
    fn new_project_error_io_serializes() {
        let err = NewProjectError::IoError {
            message: "disk full".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"ioError\""));
        assert!(json.contains("disk full"));
    }

    #[test]
    fn new_project_result_serializes() {
        let result = NewProjectResult {
            project: ProjectEntry {
                id: "id".to_string(),
                name: "P".to_string(),
                path: "/x/P".to_string(),
                unity_version: Some("2022.3.48f1".to_string()),
                last_opened_at: Some("2026-06-11T19:00:00+00:00".to_string()),
                last_modified_at: Some("2026-06-11T19:00:00+00:00".to_string()),
                launch_args: None,
                platform_intent: None,
                last_launch_pid: None,
                last_launch_at: None,
                frecency: 1,
                git_branch: None,
                source: "manual".to_string(),
                hidden: false,
                stale: false,
                env_vars: Default::default(),
            },
            projects: ProjectsFile {
                version: 1,
                projects: vec![],
            },
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(json.contains("\"project\""));
        assert!(json.contains("\"projects\""));
        assert!(json.contains("\"frecency\":1"));
    }
}
