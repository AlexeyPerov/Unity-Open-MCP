//! `.meta` file generation, GUID regeneration, and missing-meta fixup.
//!
//! Ports the relevant pieces of UPM-Template-Creator's `meta.go`:
//!   - [`meta_content_for_path`] picks the right Unity importer type
//!     for a file extension and emits a valid `.meta` body.
//!   - [`random_guid`] generates a 32-hex-char GUID (Unity's format).
//!   - [`regenerate_all_guids`] rewrites every `.meta` GUID in a tree.
//!   - [`add_missing_meta_files`] creates `.meta` siblings for assets
//!     that lack one (skipping `~`-suffixed Unity-optional folders).

use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::persistence;

/// Returns the `.meta` file body for `asset_path`, picking the importer
/// type Unity expects for that extension. The GUID is random; the rest
/// is a minimal-but-valid importer block. Mirrors the Go
/// `metaContentForPath` dispatch table.
pub fn meta_content_for_path(asset_path: &Path) -> String {
    let guid = random_guid();
    let ext = asset_path
        .extension()
        .map(|e| e.to_string_lossy().to_ascii_lowercase())
        .unwrap_or_default();
    let is_folder = asset_path.is_dir() || ext.is_empty();

    let (importer, extra) = if is_folder {
        (
            "DefaultImporter",
            "  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
        )
    } else {
        match ext.as_str() {
            "cs" => (
                "MonoImporter",
                "  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
            ),
            "asmdef" => (
                "AssemblyDefinitionImporter",
                "  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
            ),
            "prefab" | "unity" | "asset" | "mat" | "controller" | "anim" | "preset" | "shadersubgraph" | "flare" => (
                "NativeFormatImporter",
                "  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
            ),
            "dll" | "so" | "bundle" => (
                "PluginImporter",
                "  serializedVersion: 2\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
            ),
            "png" | "jpg" | "jpeg" | "tga" | "psd" | "tif" | "tiff" | "bmp" | "iff" | "gif" | "hdr" | "exr" | "pict" => (
                "TextureImporter",
                "  serializedVersion: 12\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
            ),
            _ => (
                "TextScriptImporter",
                "  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
            ),
        }
    };

    let folder_line = if is_folder {
        "  isImporter: 0\n  folderAsset: yes\n"
    } else {
        ""
    };

    format!(
        "fileFormatVersion: 2\n\
         guid: {guid}\n\
         {folder_line}\
         {importer}:\n\
         {extra}\
         ",
        guid = guid,
        folder_line = folder_line,
        importer = importer,
        extra = extra,
    )
}

/// Generates a 32-character lowercase hex GUID from 16 random bytes,
/// matching Unity's GUID format exactly.
pub fn random_guid() -> String {
    let mut bytes = [0u8; 16];
    // std does not expose a stable rand in stdlib; use a simple
    // entropy source from the OS via the filesystem on Unix and the
    // process id + time as a fallback. For a desktop app this runs
    // rarely and the GUID only needs to be unique-per-write, not
    // cryptographically random. We use `getrandom`-style seeding via
    // the system clock + thread id for cross-platform portability
    // without adding a dependency.
    use std::time::{SystemTime, UNIX_EPOCH};
    let seed = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64 ^ (std::process::id() as u64))
        .unwrap_or(0xCAFEBABE)
        .wrapping_mul(0x9E3779B97F4A7C15);
    // xorshift64* to fill 16 bytes.
    let mut state = seed;
    for b in &mut bytes {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        *b = (state & 0xFF) as u8;
    }
    // Hex-encode.
    let mut out = String::with_capacity(32);
    for b in &bytes {
        out.push_str(&format!("{:02x}", b));
    }
    out
}

/// Replaces the `guid:` line in a `.meta` file body with `new_guid`.
fn replace_guid_in_meta(content: &str, new_guid: &str) -> String {
    let mut out = String::new();
    let mut replaced = false;
    for line in content.lines() {
        if !replaced && line.starts_with("guid:") {
            out.push_str(&format!("guid: {}\n", new_guid));
            replaced = true;
        } else {
            out.push_str(line);
            out.push('\n');
        }
    }
    if !replaced {
        // No guid line (malformed meta) — prepend one.
        out = format!("fileFormatVersion: 2\nguid: {}\n{}", new_guid, out);
    }
    out
}

/// Returns true when `rel_path` points inside a `~`-suffixed folder
/// (Unity ignores these — `Samples~`, `Documentation~` — so `.meta`
/// files are not generated for their contents).
fn is_inside_tilde_folder(rel_path: &str) -> bool {
    rel_path
        .split('/')
        .any(|seg| seg.ends_with('~'))
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MetaOperationResult {
    pub regenerated: u32,
    pub added: u32,
    /// Per-file notes (errors + successes) for the UI log.
    pub notes: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum MetaOperationError {
    #[serde(rename_all = "camelCase")]
    ProjectNotFound { project_id: String },
    #[serde(rename_all = "camelCase")]
    PersistFailed { message: String },
}

/// Recursively walks the package root and regenerates the GUID in
/// every existing `.meta` file. Assets without a `.meta` are left
/// alone (use `add_missing_package_meta` for that).
#[tauri::command]
pub fn regenerate_package_meta_guids(
    state: State<AppState>,
    project_id: String,
) -> Result<MetaOperationResult, MetaOperationError> {
    let entry = {
        let guard = state.projects.lock().unwrap();
        guard
            .projects
            .iter()
            .find(|p| p.id == project_id)
            .cloned()
            .ok_or_else(|| MetaOperationError::ProjectNotFound {
                project_id: project_id.clone(),
            })?
    };
    let root = PathBuf::from(&entry.path);
    let mut regenerated: u32 = 0;
    let mut notes: Vec<String> = Vec::new();
    walk_meta_files(&root, &root, &mut |meta_path, content| {
        let new_guid = random_guid();
        let new_content = replace_guid_in_meta(content, &new_guid);
        match fs::write(meta_path, &new_content) {
            Ok(()) => {
                regenerated += 1;
            }
            Err(e) => notes.push(format!("failed to rewrite {}: {}", meta_path.display(), e)),
        }
    });
    bump_project_mtime(&state, &project_id)?;
    Ok(MetaOperationResult {
        regenerated,
        added: 0,
        notes,
    })
}

/// Recursively walks the package root and creates a `.meta` sibling
/// for every asset (file or folder) that does not already have one.
/// Assets inside `~`-suffixed folders are skipped (Unity ignores
/// them, so `.meta` files would be noise).
#[tauri::command]
pub fn add_missing_package_meta(
    state: State<AppState>,
    project_id: String,
) -> Result<MetaOperationResult, MetaOperationError> {
    let entry = {
        let guard = state.projects.lock().unwrap();
        guard
            .projects
            .iter()
            .find(|p| p.id == project_id)
            .cloned()
            .ok_or_else(|| MetaOperationError::ProjectNotFound {
                project_id: project_id.clone(),
            })?
    };
    let root = PathBuf::from(&entry.path);
    let mut added: u32 = 0;
    let mut notes: Vec<String> = Vec::new();

    fn walk(
        dir: &Path,
        root: &Path,
        added: &mut u32,
        notes: &mut Vec<String>,
    ) {
        let entries = match fs::read_dir(dir) {
            Ok(e) => e,
            Err(_) => return,
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let name = entry.file_name();
            let name = name.to_string_lossy().to_string();
            // Skip dot-dirs and the standard dependency folders.
            if name.starts_with('.') || matches!(name.as_str(), "node_modules" | "vendor" | "dist" | "build" | "target") {
                continue;
            }
            let rel = path
                .strip_prefix(root)
                .map(|p| p.to_string_lossy().replace('\\', "/").to_string())
                .unwrap_or_else(|_| name.clone());
            // Skip assets inside ~ folders.
            if is_inside_tilde_folder(&rel) {
                continue;
            }
            // The .meta file itself is not an asset to generate for.
            if name.ends_with(".meta") {
                continue;
            }
            let ft = match entry.file_type() {
                Ok(ft) => ft,
                Err(_) => continue,
            };
            if ft.is_dir() {
                // For folders the .meta is `<name>.meta` (sibling), not
                // an extension swap — with_extension would corrupt
                // `Editor` → `Editor.meta` which is correct here, but
                // for `Foo.bar` it would produce `Foo.meta`. Use the
                // sibling form explicitly.
                let meta_path = path.parent().unwrap().join(format!("{}.meta", name));
                if !meta_path.exists() {
                    match fs::write(&meta_path, meta_content_for_path(&path)) {
                        Ok(()) => *added += 1,
                        Err(e) => notes.push(format!("failed to create {}: {}", meta_path.display(), e)),
                    }
                }
                walk(&path, root, added, notes);
            } else if ft.is_file() {
                let meta_path = path.parent().unwrap().join(format!("{}.meta", name));
                if !meta_path.exists() {
                    match fs::write(&meta_path, meta_content_for_path(&path)) {
                        Ok(()) => *added += 1,
                        Err(e) => notes.push(format!("failed to create {}: {}", meta_path.display(), e)),
                    }
                }
            }
        }
    }

    walk(&root, &root, &mut added, &mut notes);
    bump_project_mtime(&state, &project_id)?;
    Ok(MetaOperationResult {
        regenerated: 0,
        added,
        notes,
    })
}

/// Walks `dir` and invokes `f` for every `.meta` file, passing the
/// file path and its current contents.
fn walk_meta_files<F>(dir: &Path, root: &Path, f: &mut F)
where
    F: FnMut(&Path, &str),
{
    let entries = match fs::read_dir(dir) {
        Ok(e) => e,
        Err(_) => return,
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let name = entry.file_name();
        let name = name.to_string_lossy().to_string();
        if name.starts_with('.') || matches!(name.as_str(), "node_modules" | "vendor" | "dist" | "build" | "target") {
            continue;
        }
        let ft = match entry.file_type() {
            Ok(ft) => ft,
            Err(_) => continue,
        };
        if ft.is_dir() {
            walk_meta_files(&path, root, f);
        } else if ft.is_file() && name.ends_with(".meta") {
            let rel = path
                .strip_prefix(root)
                .map(|p| p.to_string_lossy().replace('\\', "/").to_string())
                .unwrap_or_else(|_| name.clone());
            if is_inside_tilde_folder(&rel) {
                continue;
            }
            if let Ok(content) = fs::read_to_string(&path) {
                f(&path, &content);
            }
        }
    }
}

fn bump_project_mtime(
    state: &State<AppState>,
    project_id: &str,
) -> Result<(), MetaOperationError> {
    let mut projects = state.projects.lock().unwrap().clone();
    let now = chrono::Utc::now().to_rfc3339();
    for p in projects.projects.iter_mut() {
        if p.id == project_id {
            p.last_modified_at = Some(now);
            break;
        }
    }
    if let Err(e) = persistence::save_projects(&projects) {
        log::error!("Failed to persist mtime after meta op: {}", e);
        return Err(MetaOperationError::PersistFailed {
            message: e.to_string(),
        });
    }
    *state.projects.lock().unwrap() = projects;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn random_guid_is_32_lowercase_hex() {
        let g = random_guid();
        assert_eq!(g.len(), 32);
        assert!(g.chars().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
    }

    #[test]
    fn random_guid_is_unique_across_calls() {
        let a = random_guid();
        let b = random_guid();
        // Not guaranteed unique with a weak entropy source, but with
        // nanosecond seeding the collision chance across two immediate
        // calls is effectively zero.
        assert_ne!(a, b);
    }

    #[test]
    fn meta_for_folder_has_folder_asset_flag() {
        let dir = tempfile::tempdir().unwrap();
        let content = meta_content_for_path(dir.path());
        assert!(content.contains("folderAsset: yes"));
        assert!(content.contains("DefaultImporter"));
    }

    #[test]
    fn meta_for_cs_file_uses_mono_importer() {
        let f = Path::new("Foo.cs");
        let content = meta_content_for_path(f);
        assert!(content.contains("MonoImporter"));
        assert!(!content.contains("folderAsset"));
    }

    #[test]
    fn meta_for_asmdef_uses_assembly_definition_importer() {
        let f = Path::new("Pkg.Editor.asmdef");
        assert!(meta_content_for_path(f).contains("AssemblyDefinitionImporter"));
    }

    #[test]
    fn replace_guid_swaps_the_guid_line() {
        let input = "fileFormatVersion: 2\nguid: oldguid1234\nMonoImporter:\n  x: 1\n";
        let out = replace_guid_in_meta(input, "newguid5678");
        assert!(out.contains("guid: newguid5678"));
        assert!(!out.contains("oldguid1234"));
        // Non-guid lines survive.
        assert!(out.contains("MonoImporter"));
    }

    #[test]
    fn is_inside_tilde_folder_detects_samples() {
        // Any path with a `~`-suffixed segment is treated as inside a
        // Unity-optional folder — `Samples~`, `Documentation~`, etc.
        assert!(is_inside_tilde_folder("Samples~/Foo/Bar.cs"));
        assert!(is_inside_tilde_folder("Documentation~/x.md"));
        assert!(is_inside_tilde_folder("Samples~/Bar"));
        assert!(!is_inside_tilde_folder("Editor/Foo.cs"));
        assert!(!is_inside_tilde_folder("Editor/Samples"));
    }
}
