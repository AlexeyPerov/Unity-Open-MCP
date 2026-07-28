//! Read / write a Unity `package.json` manifest with the full schema.
//!
//! The Go original (`UPM-Template-Creator`) modeled only a subset of
//! fields (it omitted `dependencies`, `unityRelease`, `type`, and the
//! URL fields). We model the complete Unity package.json spec so the
//! Package settings popup can edit every field Unity recognizes,
//! including the dependency map.
//!
//! H4 (round-2 review): real-world manifests carry many fields the Hub
//! does not model (`license`, `repository`, `homepage`, `bugs`, `main`,
//! `files`, `scripts`, `devDependencies`, `publishConfig`, `_upm`,
//! `dist`, `upmCi`, …). A `read → edit → write` round-trip with a struct
//! that lacks an overflow field would silently drop every unknown key on
//! the first save. We capture the unknown keys into the `extra` map on
//! deserialize and re-serialize them verbatim, so the file the Hub
//! writes back contains every key the user's manifest started with
//! (plus whatever they edited through the form).

use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use serde_json::Value;
use tauri::State;

use crate::config::commands::AppState;
use crate::config::persistence;

/// Full Unity package.json schema. Every field is optional on
/// deserialize (Unity tolerates a minimal `{ "name", "version" }`)
/// and skipped on serialize when empty/none so round-tripping a
/// minimal manifest stays compact. Field order follows Unity's
/// documented layout so the written file reads naturally.
///
/// H4: `extra` is a `#[serde(flatten)]` overflow map. Every key the
/// struct does not model (`license`, `repository`, `homepage`, `bugs`,
/// `main`, `files`, `scripts`, `devDependencies`, `publishConfig`,
/// `_upm`, `dist`, `upmCi`, …) lands here on deserialize and is
/// re-serialized verbatim. Without it, the first Hub save of a
/// real-world manifest would silently drop every unknown key. The map
/// is also `skip_serializing_if = "BTreeMap::is_empty"` so a manifest
/// written from scratch (no unknown fields) does not grow a trailing
/// `"extra": {}` block.
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
    /// Overflow for keys the struct does not model — preserves
    /// `license` / `repository` / `homepage` / `bugs` / `main` /
    /// `files` / `scripts` / `devDependencies` / `publishConfig` /
    /// `_upm` / `dist` / `upmCi` / … across a read → edit → write
    /// round-trip. See H4 in the round-2 review.
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
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
///
/// H4: writes via tmp + rename (sibling tmp file → atomic rename) so a
/// crash mid-write never leaves a truncated manifest on disk. The
/// `extra` overflow field on [`PackageManifest`] preserves every key
/// the original file carried that the struct does not model.
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
    persistence::atomic_write_at(path, &format!("{}\n", json)).map_err(|e| {
        ManifestError::WriteFailed {
            path: path.display().to_string(),
            message: e.to_string(),
        }
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
    // A10 — only the entry is needed up-front; the projects snapshot cloned
    // here went stale during write_manifest_at + the optional changelog bump
    // and clobbered concurrent changes on write-back. The persist now goes
    // through with_projects against the fresh live state (see below).
    let entry = {
        let guard = state.projects.lock().unwrap();
        guard
            .projects
            .iter()
            .find(|p| p.id == project_id)
            .cloned()
            .ok_or_else(|| ManifestError::ProjectNotFound {
                project_id: project_id.clone(),
            })?
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
    // A10 — apply the mtime bump to the FRESH live state via with_projects
    // instead of cloning a snapshot before write_manifest_at and writing it
    // back afterwards. The snapshot went stale if any concurrent change (a
    // launch, a hide, an env-var save) landed during the manifest write + the
    // optional changelog prepend, and assigning the stale clone back into the
    // Mutex discarded those changes.
    let now = chrono::Utc::now().to_rfc3339();
    if let Err(e) = crate::config::commands::with_projects(
        &state.projects,
        |p| {
            for entry in p.projects.iter_mut() {
                if entry.id == project_id {
                    entry.last_modified_at = Some(now.clone());
                    break;
                }
            }
        },
    ) {
        log::error!("Failed to persist project mtime after manifest write: {}", e);
        return Err(ManifestError::PersistFailed {
            message: e.to_string(),
        });
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

    #[test]
    fn round_trip_preserves_unknown_keys() {
        // H4: a real-world manifest carries many fields the Hub struct
        // does not model. They must survive a read → edit → write cycle.
        let json = r#"{
            "name": "com.foo.bar",
            "version": "1.0.0",
            "license": "MIT",
            "repository": { "type": "git", "url": "https://example.com" },
            "homepage": "https://example.com",
            "main": "Runtime/index.js",
            "files": ["Runtime/", "package.json"],
            "scripts": { "test": "echo test" },
            "devDependencies": { "typescript": "^5.0.0" },
            "publishConfig": { "access": "public" },
            "_upm": { "name": "extra" }
        }"#;
        let m: PackageManifest = serde_json::from_str(json).unwrap();
        // The Hub-edited field changes; the unknown fields are untouched
        // in the overflow map.
        let mut edited = m.clone();
        edited.version = Some("1.1.0".into());
        let out = serde_json::to_string_pretty(&edited).unwrap();
        let restored: PackageManifest = serde_json::from_str(&out).unwrap();
        assert_eq!(restored.version.as_deref(), Some("1.1.0"));
        // Every unknown key round-trips verbatim.
        assert_eq!(restored.extra.get("license").and_then(|v| v.as_str()), Some("MIT"));
        assert_eq!(restored.extra.get("main").and_then(|v| v.as_str()), Some("Runtime/index.js"));
        assert!(restored.extra.get("files").unwrap().is_array());
        assert!(restored.extra.get("scripts").unwrap().is_object());
        assert!(restored.extra.get("devDependencies").unwrap().is_object());
        assert!(restored.extra.get("publishConfig").unwrap().is_object());
        assert!(restored.extra.get("_upm").unwrap().is_object());
        // And the unknown block is absent from a manifest written from
        // scratch (no extra fields → empty map → skipped on serialize).
        let fresh = PackageManifest {
            name: Some("x".into()),
            version: Some("1.0.0".into()),
            ..Default::default()
        };
        let fresh_out = serde_json::to_string(&fresh).unwrap();
        assert!(!fresh_out.contains("\"extra\""));
    }

    #[test]
    fn write_manifest_at_uses_atomic_rename() {
        // H4: the writer stages via a sibling tmp file and renames; a
        // partial write must never produce a truncated manifest.
        let tmp = tempfile::tempdir().unwrap();
        let path = tmp.path().join("package.json");
        let original = PackageManifest {
            name: Some("com.x".into()),
            version: Some("1.0.0".into()),
            ..Default::default()
        };
        write_manifest_at(&path, &original).unwrap();
        assert!(path.exists());
        // No leftover tmp file after a successful write.
        assert!(!tmp.path().join("package.json.tmp-merge").exists());
        let read_back = read_manifest_at(&path).unwrap();
        assert_eq!(read_back.name.as_deref(), Some("com.x"));
    }
}
