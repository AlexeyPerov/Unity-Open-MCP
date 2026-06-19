//! Package creation — scaffolds a new UPM package on disk and
//! registers it as a tracked `Package` project.
//!
//! Unlike UPM-Template-Creator (which copies a vendored template dir
//! and runs token replacement), this generates the scaffold
//! programmatically. The template is small and deterministic (a
//! folder skeleton + package.json + README + CHANGELOG + an Editor
//! asmdef), so generating it avoids shipping a binary asset and keeps
//! the creation logic inspectable.
//!
//! The resulting folder is registered via `add_project`, which
//! classifies it as `Package` (it has a root `package.json`).

use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::persistence;
use crate::config::project_kind;
use crate::config::schemas::{ProjectEntry, ProjectKind, ProjectsFile};
use crate::config::upm::manifest::{write_manifest_at, ManifestError, PackageManifest};
use crate::config::upm::meta::meta_content_for_path;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreatePackageParams {
    /// Absolute parent folder the package is created under. The
    /// package folder itself is `<parent>/<name>`.
    pub parent: String,
    /// Package name; validated against `^[a-z0-9][a-z0-9.-]*$`.
    pub name: String,
    pub version: Option<String>,
    pub display_name: Option<String>,
    pub description: Option<String>,
    pub unity: Option<String>,
    pub keywords: Option<Vec<String>>,
    pub author_name: Option<String>,
    pub author_url: Option<String>,
    /// When true (default), create `Editor/` + `Samples~` + `README.md`
    /// + `CHANGELOG.md` + `LICENSE.md`. When false, only `package.json`
    /// + the `Editor/` asmdef are written.
    pub include_extras: Option<bool>,
    /// When true, overwrite an existing folder at the target path
    /// instead of failing. Off by default.
    pub overwrite: Option<bool>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreatePackageResult {
    pub project: ProjectEntry,
    pub projects: ProjectsFile,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum CreatePackageError {
    #[serde(rename_all = "camelCase")]
    ParentNotADirectory { path: String },
    #[serde(rename_all = "camelCase")]
    InvalidName { name: String, reason: String },
    #[serde(rename_all = "camelCase")]
    TargetExists { path: String },
    #[serde(rename_all = "camelCase")]
    ScaffoldFailed { message: String },
    #[serde(rename_all = "camelCase")]
    Duplicate { path: String },
    #[serde(rename_all = "camelCase")]
    PersistFailed { message: String },
}

/// Validates a package name against Unity's DNS-like convention.
fn validate_name(name: &str) -> Result<(), String> {
    if name.is_empty() {
        return Err("name is required".into());
    }
    // Unity requires reverse-DNS names like `com.author.pkg`, but the
    // folder-name segment can be any lowercase kebab string. We accept
    // the broader `^[a-z0-9][a-z0-9.-]*$` (matching the Go tool) so a
    // simple `my-package` works too.
    let valid = name
        .chars()
        .next()
        .map(|c| c.is_ascii_lowercase() || c.is_ascii_digit())
        .unwrap_or(false)
        && name
            .chars()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-' || c == '.');
    if !valid {
        return Err(format!(
            "name must match ^[a-z0-9][a-z0-9.-]*$ (got \"{}\")",
            name
        ));
    }
    Ok(())
}

/// Writes `content` to `path`, creating parent directories as needed.
fn write_file(path: &Path, content: &str) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    fs::write(path, content).map_err(|e| e.to_string())
}

/// Writes a `.meta` file for `asset` next to it (sibling `<name>.meta`).
fn write_meta(asset: &Path) -> Result<(), String> {
    let name = asset
        .file_name()
        .map(|n| n.to_string_lossy().to_string())
        .ok_or("asset has no file name")?;
    let meta_path = asset
        .parent()
        .ok_or("asset has no parent")?
        .join(format!("{}.meta", name));
    write_file(&meta_path, &meta_content_for_path(asset))
}

fn scaffold(
    target: &Path,
    params: &CreatePackageParams,
) -> Result<PackageManifest, CreatePackageError> {
    let include_extras = params.include_extras.unwrap_or(true);

    // Editor/ + asmdef.
    let editor_dir = target.join("Editor");
    fs::create_dir_all(&editor_dir).map_err(|e| CreatePackageError::ScaffoldFailed {
        message: e.to_string(),
    })?;
    let asmdef_name = format!("{}.Editor.asmdef", params.name);
    let asmdef_path = editor_dir.join(&asmdef_name);
    let asmdef_body = serde_json::json!({
        "name": format!("{}.Editor", params.name),
        "references": [],
        "includePlatforms": ["Editor"],
        "excludePlatforms": [],
        "allowUnsafeCode": false,
        "overrideReferences": false,
        "precompiledReferences": [],
        "autoReferenced": true,
        "defineConstraints": [],
        "versionDefines": [],
        "noEngineReferences": false
    });
    write_file(&asmdef_path, &format!("{}\n", serde_json::to_string_pretty(&asmdef_body).unwrap()))
        .map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;

    // .meta files for the scaffold.
    write_meta(&editor_dir).map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;
    write_meta(&asmdef_path).map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;
    write_meta(target).map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;

    // package.json.
    let manifest = PackageManifest {
        name: Some(params.name.clone()),
        version: Some(params.version.clone().unwrap_or_else(|| "1.0.0".into())),
        display_name: params.display_name.clone(),
        description: params.description.clone(),
        unity: Some(params.unity.clone().unwrap_or_else(|| "2022.3".into())),
        keywords: params.keywords.clone().unwrap_or_default(),
        author: Some(crate::config::upm::manifest::ManifestAuthor {
            name: params.author_name.clone(),
            url: params.author_url.clone(),
            email: None,
        }),
        ..Default::default()
    };
    let pkg_json = target.join("package.json");
    write_manifest_at(&pkg_json, &manifest).map_err(|e| match e {
        ManifestError::WriteFailed { message, .. } => {
            CreatePackageError::ScaffoldFailed { message }
        }
        _ => CreatePackageError::ScaffoldFailed {
            message: "manifest error".into(),
        },
    })?;
    write_meta(&pkg_json).map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;

    // Optional extras (README, CHANGELOG, LICENSE, Samples~).
    if include_extras {
        let readme = format!(
            "# {}\n\n{}\n",
            params.display_name.as_deref().unwrap_or(&params.name),
            params.description.as_deref().unwrap_or("A Unity package."),
        );
        write_file(&target.join("README.md"), &readme)
            .map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;
        write_meta(&target.join("README.md"))
            .map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;

        let changelog = format!(
            "# Changelog\n\n## [{}] - Initial Release\n\n### Added\n\n### Fixed\n\n### Changed\n\n### Removed\n",
            params.version.as_deref().unwrap_or("1.0.0"),
        );
        write_file(&target.join("CHANGELOG.md"), &changelog)
            .map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;
        write_meta(&target.join("CHANGELOG.md"))
            .map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;

        let license = format!(
            "MIT License\n\nCopyright (c) {} {}\n\n[MIT license text omitted — fill in]\n",
            chrono::Utc::now().format("%Y"),
            params.author_name.as_deref().unwrap_or("Author"),
        );
        write_file(&target.join("LICENSE.md"), &license)
            .map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;
        write_meta(&target.join("LICENSE.md"))
            .map_err(|e| CreatePackageError::ScaffoldFailed { message: e })?;

        // Samples~ (Unity ignores its contents; no .meta needed inside).
        let samples_dir = target.join("Samples~").join("Samples");
        fs::create_dir_all(&samples_dir).map_err(|e| CreatePackageError::ScaffoldFailed {
            message: e.to_string(),
        })?;
    }

    Ok(manifest)
}

/// Scaffolds a new UPM package and registers it as a tracked `Package`
/// project. The target folder is `<parent>/<name>`.
#[tauri::command]
pub fn create_package(
    state: State<AppState>,
    params: CreatePackageParams,
) -> Result<CreatePackageResult, CreatePackageError> {
    let parent = PathBuf::from(&params.parent);
    if !parent.is_dir() {
        return Err(CreatePackageError::ParentNotADirectory {
            path: params.parent.clone(),
        });
    }
    validate_name(&params.name).map_err(|reason| CreatePackageError::InvalidName {
        name: params.name.clone(),
        reason,
    })?;

    let target = parent.join(&params.name);
    if target.exists() {
        if !params.overwrite.unwrap_or(false) {
            return Err(CreatePackageError::TargetExists {
                path: target.display().to_string(),
            });
        }
        // Overwrite: remove the existing folder so the scaffold is clean.
        fs::remove_dir_all(&target).map_err(|e| CreatePackageError::ScaffoldFailed {
            message: format!("overwrite failed: {}", e),
        })?;
    }

    scaffold(&target, &params)?;

    // Register the new package as a tracked project (Package kind).
    let now = chrono::Utc::now().to_rfc3339();
    let entry = ProjectEntry {
        id: uuid::Uuid::new_v4().to_string(),
        name: params.name.clone(),
        path: target.to_string_lossy().to_string(),
        unity_version: None,
        last_opened_at: Some(now.clone()),
        last_modified_at: Some(now),
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
        render_pipeline: None,
        default_build_target: None,
        kind: ProjectKind::Package,
        package_manifest_path: project_kind::package_manifest_relative(ProjectKind::Package)
            .map(|s| s.to_string()),
        migrate_source_folder: None,
        line_count_stats: None,
    };

    // Duplicate check (canonicalized path).
    let canonical = fs::canonicalize(&target)
        .map(|c| c.to_string_lossy().to_string())
        .unwrap_or_else(|_| entry.path.clone());
    {
        let guard = state.projects.lock().unwrap();
        if guard
            .projects
            .iter()
            .any(|p| fs::canonicalize(&p.path).map(|c| c.to_string_lossy().to_string()).unwrap_or_else(|_| p.path.clone()) == canonical)
        {
            return Err(CreatePackageError::Duplicate {
                path: entry.path.clone(),
            });
        }
    }

    let mut projects = state.projects.lock().unwrap().clone();
    projects.projects.push(entry.clone());
    persistence::save_projects(&projects).map_err(|e| CreatePackageError::PersistFailed {
        message: e.to_string(),
    })?;
    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects.clone();
    }

    Ok(CreatePackageResult {
        project: entry,
        projects,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validate_name_accepts_kebab() {
        assert!(validate_name("my-package").is_ok());
        assert!(validate_name("com.author.pkg").is_ok());
        assert!(validate_name("a1").is_ok());
    }

    #[test]
    fn validate_name_rejects_uppercase_and_spaces() {
        assert!(validate_name("MyPackage").is_err());
        assert!(validate_name("my package").is_err());
        assert!(validate_name("-leading-dash").is_err());
        assert!(validate_name("").is_err());
    }

    #[test]
    fn scaffold_creates_package_json_and_editor() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("com.test.pkg");
        let params = CreatePackageParams {
            parent: dir.path().to_string_lossy().to_string(),
            name: "com.test.pkg".into(),
            version: Some("2.0.0".into()),
            display_name: Some("Test".into()),
            description: Some("desc".into()),
            unity: Some("2022.3".into()),
            keywords: Some(vec!["tool".into()]),
            author_name: Some("Author".into()),
            author_url: None,
            include_extras: Some(true),
            overwrite: None,
        };
        let manifest = scaffold(&target, &params).unwrap();
        assert_eq!(manifest.name.as_deref(), Some("com.test.pkg"));
        assert_eq!(manifest.version.as_deref(), Some("2.0.0"));
        assert!(target.join("package.json").exists());
        assert!(target.join("package.json.meta").exists());
        assert!(target.join("Editor/com.test.pkg.Editor.asmdef").exists());
        assert!(target.join("README.md").exists());
        assert!(target.join("CHANGELOG.md").exists());
        assert!(target.join("LICENSE.md").exists());
        assert!(target.join("Samples~/Samples").is_dir());
        assert!(target.join("Editor.meta").exists());
    }

    #[test]
    fn scaffold_without_extras_skips_readme() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("basic");
        let params = CreatePackageParams {
            parent: dir.path().to_string_lossy().to_string(),
            name: "basic".into(),
            version: None,
            display_name: None,
            description: None,
            unity: None,
            keywords: None,
            author_name: None,
            author_url: None,
            include_extras: Some(false),
            overwrite: None,
        };
        scaffold(&target, &params).unwrap();
        assert!(target.join("package.json").exists());
        assert!(!target.join("README.md").exists());
        // Defaults applied: version 1.0.0, unity 2022.3.
        let pkg = std::fs::read_to_string(target.join("package.json")).unwrap();
        assert!(pkg.contains("1.0.0"));
        assert!(pkg.contains("2022.3"));
    }

    #[test]
    fn random_guid_used_in_scaffold_meta() {
        // The meta files written by scaffold use meta_content_for_path,
        // which embeds a random_guid() — verify the resulting file has a
        // 32-hex guid line.
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("g");
        let params = CreatePackageParams {
            parent: dir.path().to_string_lossy().to_string(),
            name: "g".into(),
            version: None,
            display_name: None,
            description: None,
            unity: None,
            keywords: None,
            author_name: None,
            author_url: None,
            include_extras: Some(false),
            overwrite: None,
        };
        scaffold(&target, &params).unwrap();
        let meta = std::fs::read_to_string(target.join("package.json.meta")).unwrap();
        assert!(meta.contains("guid: "));
        // Find the guid line and check it is 32 hex chars.
        let guid_line = meta.lines().find(|l| l.starts_with("guid: ")).unwrap();
        let guid = guid_line.trim_start_matches("guid: ").trim();
        assert_eq!(guid.len(), 32);
        assert!(guid.chars().all(|c| c.is_ascii_hexdigit()));
    }
}
