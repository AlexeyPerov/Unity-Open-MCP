//! Multi-type project detection.
//!
//! The hub used to track Unity projects only. It now accepts any
//! folder and classifies it into one of four kinds so the list row,
//! settings popup, git widget, and line counter can adapt per type.
//!
//! Detection precedence (deepest-specific first):
//!   1. Unity project  — has both `Assets/` and `ProjectSettings/`
//!   2. Open-MCP repo  — has a top-level `mcp-server/` directory and a
//!                       root `package.json` (the shape of this repo)
//!   3. Package        — has a root `package.json` (pure UPM package)
//!   4. Custom         — anything else
//!
//! The detection is intentionally cheap and filesystem-only; it never
//! shells out to `git` or parses file contents beyond directory/list
//! existence. The richer per-type work (manifest parsing, build-status
//! detection, line counts) runs lazily from the settings popup.

use std::path::Path;

use crate::config::schemas::ProjectKind;

/// Returns true when `path` is a directory containing both `Assets/`
/// and `ProjectSettings/`. Kept here so the precedence ladder is in one
/// place; the original copy in `projects.rs` stays for the Unity-only
/// launch path which needs the friendly error reason.
pub(crate) fn is_unity_project_root(path: &Path) -> bool {
    path.is_dir() && path.join("Assets").is_dir() && path.join("ProjectSettings").is_dir()
}

/// Returns true when `path` is a directory containing a `mcp-server/`
/// subdirectory and a root `package.json` file. This is the shape of
/// the unity-open-mcp monorepo (this repo) and any fork following the
/// same root layout.
fn is_open_mcp_repo(path: &Path) -> bool {
    path.is_dir() && path.join("mcp-server").is_dir() && path.join("package.json").is_file()
}

/// Returns true when `path` is a directory containing a root
/// `package.json`. Used as the Package marker after Unity and Open-MCP
/// have been ruled out. We deliberately do not validate the JSON
/// contents here — any file named `package.json` qualifies; the
/// package settings popup surfaces a parse error if the manifest is
/// malformed.
fn has_root_package_json(path: &Path) -> bool {
    path.is_dir() && path.join("package.json").is_file()
}

/// Classify a folder into a [`ProjectKind`] using the documented
/// precedence ladder. The function never fails — a missing or
/// unreadable path simply returns `Custom`, matching the brief's "any
/// folder" acceptance rule. The caller (`add_project`) still rejects
/// non-directories upstream.
pub fn detect_kind(path: &Path) -> ProjectKind {
    if is_unity_project_root(path) {
        ProjectKind::Unity
    } else if is_open_mcp_repo(path) {
        ProjectKind::OpenMcp
    } else if has_root_package_json(path) {
        ProjectKind::Package
    } else {
        ProjectKind::Custom
    }
}

/// Relative path to the package manifest from the project root, or
/// `None` for kinds that do not have one (Unity / Custom). For Package
/// and Open-MCP kinds the manifest always sits at the root, so this
/// returns the constant `"package.json"`; the indirection exists so
/// the UI can read a single field rather than re-deriving it.
pub fn package_manifest_relative(kind: ProjectKind) -> Option<&'static str> {
    match kind {
        ProjectKind::Package | ProjectKind::OpenMcp => Some("package.json"),
        ProjectKind::Unity | ProjectKind::Custom => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn mkdir(p: &Path) {
        fs::create_dir_all(p).unwrap();
    }
    fn touch(p: &Path) {
        if let Some(parent) = p.parent() {
            mkdir(parent);
        }
        fs::write(p, b"{}").unwrap();
    }

    #[test]
    fn unity_project_detected_when_assets_and_project_settings_exist() {
        let dir = tempfile::tempdir().unwrap();
        mkdir(&dir.path().join("Assets"));
        mkdir(&dir.path().join("ProjectSettings"));
        assert_eq!(detect_kind(dir.path()), ProjectKind::Unity);
    }

    #[test]
    fn open_mcp_detected_when_mcp_server_and_package_json_exist() {
        // Even when the folder also has a Packages/manifest.json shaped
        // like a Unity project, the Unity marker (Assets/ +
        // ProjectSettings/) is what wins; here we only have the
        // Open-MCP markers.
        let dir = tempfile::tempdir().unwrap();
        mkdir(&dir.path().join("mcp-server"));
        touch(&dir.path().join("package.json"));
        assert_eq!(detect_kind(dir.path()), ProjectKind::OpenMcp);
    }

    #[test]
    fn package_detected_when_only_root_package_json_exists() {
        let dir = tempfile::tempdir().unwrap();
        touch(&dir.path().join("package.json"));
        assert_eq!(detect_kind(dir.path()), ProjectKind::Package);
    }

    #[test]
    fn custom_detected_for_arbitrary_folder() {
        let dir = tempfile::tempdir().unwrap();
        mkdir(&dir.path().join("anything"));
        assert_eq!(detect_kind(dir.path()), ProjectKind::Custom);
    }

    #[test]
    fn unity_beats_open_mcp_when_both_markers_present() {
        // A real Unity project that happens to also have an mcp-server/
        // folder and a package.json is still a Unity project — the
        // precedence ladder checks Unity first.
        let dir = tempfile::tempdir().unwrap();
        mkdir(&dir.path().join("Assets"));
        mkdir(&dir.path().join("ProjectSettings"));
        mkdir(&dir.path().join("mcp-server"));
        touch(&dir.path().join("package.json"));
        assert_eq!(detect_kind(dir.path()), ProjectKind::Unity);
    }

    #[test]
    fn open_mcp_beats_package_when_mcp_server_present() {
        // A repo with mcp-server/ AND a package.json is Open-MCP, not
        // a plain Package — the precedence ladder checks Open-MCP
        // before the generic package.json marker.
        let dir = tempfile::tempdir().unwrap();
        mkdir(&dir.path().join("mcp-server"));
        touch(&dir.path().join("package.json"));
        assert_eq!(detect_kind(dir.path()), ProjectKind::OpenMcp);
    }

    #[test]
    fn missing_directory_falls_through_to_custom() {
        // detect_kind never fails — a path that does not exist is
        // treated as Custom. add_project rejects non-directories
        // upstream with a typed error, so this is purely a safety net.
        let missing = std::path::PathBuf::from("/this/does/not/exist/xyz");
        assert_eq!(detect_kind(&missing), ProjectKind::Custom);
    }

    #[test]
    fn manifest_relative_returns_package_json_only_for_manifest_kinds() {
        assert_eq!(
            package_manifest_relative(ProjectKind::Unity),
            None,
            "Unity has no package manifest"
        );
        assert_eq!(package_manifest_relative(ProjectKind::Custom), None);
        assert_eq!(
            package_manifest_relative(ProjectKind::Package),
            Some("package.json")
        );
        assert_eq!(
            package_manifest_relative(ProjectKind::OpenMcp),
            Some("package.json")
        );
    }
}
