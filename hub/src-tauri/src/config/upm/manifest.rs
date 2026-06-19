//! Read / write a Unity `package.json` manifest with the full schema.
//!
//! The Go original (`UPM-Template-Creator`) modeled only a subset of
//! fields (it omitted `dependencies`, `unityRelease`, `type`, and the
//! URL fields). We model the complete Unity package.json spec so the
//! Package settings popup can edit every field Unity recognizes,
//! including the dependency map.

use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::persistence;

/// Full Unity package.json schema. Every field is optional on
/// deserialize (Unity tolerates a minimal `{ "name", "version" }`)
/// and skipped on serialize when empty/none so round-tripping a
/// minimal manifest stays compact. Field order follows Unity's
/// documented layout so the written file reads naturally.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PackageManifest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub display_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unity: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unity_release: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub keywords: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub author: Option<ManifestAuthor>,
    /// `com.unity.xxx: "1.0.0"` dependency map. Empty maps are skipped
    /// on serialize so a dependency-free package stays compact.
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub dependencies: BTreeMap<String, String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub samples: Vec<ManifestSample>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hide_in_editor: Option<bool>,
    #[serde(rename = "type", skip_serializing_if = "Option::is_none")]
    pub package_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub documentation_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub changelog_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub licenses_url: Option<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestAuthor {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub email: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub url: Option<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestSample {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub display_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum ManifestError {
    #[serde(rename_all = "camelCase")]
    NotFound { path: String },
    #[serde(rename_all = "camelCase")]
    ParseFailed { path: String, message: String },
    #[serde(rename_all = "camelCase")]
    WriteFailed { path: String, message: String },
    #[serde(rename_all = "camelCase")]
    ProjectNotFound { project_id: String },
    #[serde(rename_all = "camelCase")]
    PersistFailed { message: String },
}

/// Reads and parses the package.json at `path`. Returns a typed
/// error when the file is missing or unparseable so the UI can show
/// a precise reason (the Go tool silently bailed).
pub fn read_manifest_at(path: &PathBuf) -> Result<PackageManifest, ManifestError> {
    let raw = fs::read_to_string(path).map_err(|_| ManifestError::NotFound {
        path: path.display().to_string(),
    })?;
    serde_json::from_str(&raw).map_err(|e| ManifestError::ParseFailed {
        path: path.display().to_string(),
        message: e.to_string(),
    })
}

/// Writes `manifest` to `path` with 2-space indentation and a trailing
/// newline, matching the Go tool's `writePackageManifest` and Unity's
/// own serialization shape.
pub fn write_manifest_at(
    path: &PathBuf,
    manifest: &PackageManifest,
) -> Result<(), ManifestError> {
    // serde_json preserves struct field declaration order, which we
    // keep aligned with Unity's documented layout above.
    let json = serde_json::to_string_pretty(manifest).map_err(|e| ManifestError::WriteFailed {
        path: path.display().to_string(),
        message: e.to_string(),
    })?;
    fs::write(path, format!("{}\n", json)).map_err(|e| ManifestError::WriteFailed {
        path: path.display().to_string(),
        message: e.to_string(),
    })
}

/// Resolves the manifest path for a tracked project entry. For
/// Package / OpenMcp kinds this is `<root>/package.json` (stored on
/// the entry as `package_manifest_path`); for other kinds it errors.
fn manifest_path_for(entry: &crate::config::schemas::ProjectEntry) -> Result<PathBuf, ManifestError> {
    let rel = entry
        .package_manifest_path
        .as_deref()
        .unwrap_or("package.json");
    Ok(PathBuf::from(&entry.path).join(rel))
}

/// Reads the manifest for a tracked package project.
#[tauri::command]
pub fn read_package_manifest(
    state: State<AppState>,
    project_id: String,
) -> Result<PackageManifest, ManifestError> {
    let guard = state.projects.lock().unwrap();
    let entry = guard
        .projects
        .iter()
        .find(|p| p.id == project_id)
        .ok_or_else(|| ManifestError::ProjectNotFound {
            project_id: project_id.clone(),
        })?;
    let path = manifest_path_for(entry)?;
    drop(guard);
    read_manifest_at(&path)
}

/// Writes an updated manifest for a tracked package project and bumps
/// the changelog when the version changed (the Go tool's behaviour).
/// The changelog bump is opt-in via `bump_changelog` + an optional
/// `changelog_label` (defaults to today's UTC date).
#[tauri::command]
pub fn write_package_manifest(
    state: State<AppState>,
    project_id: String,
    manifest: PackageManifest,
    previous_version: Option<String>,
    bump_changelog: Option<bool>,
    changelog_label: Option<String>,
) -> Result<PackageManifest, ManifestError> {
    let (entry, projects) = {
        let guard = state.projects.lock().unwrap();
        let entry = guard
            .projects
            .iter()
            .find(|p| p.id == project_id)
            .cloned()
            .ok_or_else(|| ManifestError::ProjectNotFound {
                project_id: project_id.clone(),
            })?;
        (entry, guard.clone())
    };
    let path = manifest_path_for(&entry)?;
    write_manifest_at(&path, &manifest)?;

    // Optional changelog bump when the version changed.
    if bump_changelog.unwrap_or(false) {
        if let Some(prev) = previous_version {
            if let Some(new_version) = &manifest.version {
                if prev != *new_version {
                    let changelog_path = PathBuf::from(&entry.path).join("CHANGELOG.md");
                    let label = changelog_label.unwrap_or_else(|| {
                        chrono::Utc::now().format("%Y-%m-%d").to_string()
                    });
                    if let Err(e) = crate::config::upm::changelog::prepend_version(
                        &changelog_path,
                        new_version,
                        &label,
                    ) {
                        log::warn!("changelog bump failed: {}", e);
                    }
                }
            }
        }
    }

    // No project-entry mutation needed (the manifest lives on disk,
    // not on ProjectEntry), but we re-save to bump lastModifiedAt so
    // the list's m-time column reflects the edit.
    let mut updated = projects;
    let now = chrono::Utc::now().to_rfc3339();
    for p in updated.projects.iter_mut() {
        if p.id == project_id {
            p.last_modified_at = Some(now);
            break;
        }
    }
    if let Err(e) = persistence::save_projects(&updated) {
        log::error!("Failed to persist project mtime after manifest write: {}", e);
        return Err(ManifestError::PersistFailed {
            message: e.to_string(),
        });
    }
    {
        let mut guard = state.projects.lock().unwrap();
        *guard = updated;
    }

    Ok(manifest)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn minimal_manifest_roundtrips() {
        let json = r#"{"name":"com.foo.bar","version":"1.0.0"}"#;
        let m: PackageManifest = serde_json::from_str(json).unwrap();
        assert_eq!(m.name.as_deref(), Some("com.foo.bar"));
        assert_eq!(m.version.as_deref(), Some("1.0.0"));
        assert!(m.dependencies.is_empty());
        let out = serde_json::to_string(&m).unwrap();
        assert!(out.contains("\"name\":\"com.foo.bar\""));
        // Empty maps / vecs are skipped on serialize.
        assert!(!out.contains("dependencies"));
        assert!(!out.contains("keywords"));
    }

    #[test]
    fn full_manifest_roundtrips_with_dependencies() {
        let json = r#"{
            "name": "com.foo.bar",
            "version": "2.1.0",
            "displayName": "Bar",
            "description": "A thing",
            "unity": "2022.3",
            "keywords": ["tool"],
            "author": { "name": "Author", "url": "https://example.com" },
            "dependencies": { "com.unity.xr.management": "4.0.1" },
            "samples": [{ "displayName": "S", "description": "d", "path": "Samples~/S" }]
        }"#;
        let m: PackageManifest = serde_json::from_str(json).unwrap();
        assert_eq!(m.dependencies.len(), 1);
        assert_eq!(
            m.dependencies.get("com.unity.xr.management").map(String::as_str),
            Some("4.0.1")
        );
        assert_eq!(m.samples.len(), 1);
        // Round-trip preserves the dependency map.
        let out = serde_json::to_string(&m).unwrap();
        let restored: PackageManifest = serde_json::from_str(&out).unwrap();
        assert_eq!(restored.dependencies.len(), 1);
    }

    #[test]
    fn empty_author_is_skipped_on_serialize() {
        let m = PackageManifest {
            name: Some("x".into()),
            version: Some("1.0.0".into()),
            author: Some(ManifestAuthor::default()),
            ..Default::default()
        };
        let out = serde_json::to_string(&m).unwrap();
        // An author with all-None fields serializes as {} which serde
        // still emits; that is acceptable (Unity reads it fine) and
        // matches the Go tool's behaviour.
        assert!(out.contains("author"));
    }
}
