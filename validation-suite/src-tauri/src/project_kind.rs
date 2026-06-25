//! Unity project detection for the Validation Suite.
//!
//! Mirrors the Hub's `project_kind.rs` detection philosophy (cheap,
//! filesystem-only, never shells out), but uses the engine profile's
//! declared markers instead of a hardcoded Unity ladder — so a future
//! engine profile can declare its own detection rules without backend
//! changes (idea.md → Multi-engine reuse strategy).
//!
//! For the Unity profile a folder is valid when it has all of
//! `markers.dirs` and at least one of `markers.files`
//! (unity.md → Project detection: `Assets/` + `ProjectSettings/`, and a
//! project marker file like `ProjectSettings/ProjectVersion.txt`).

use std::path::Path;

use crate::schemas::{EngineProfile, ProjectCheck};

/// Validate a candidate project folder against an engine profile's
/// markers. Never panics — returns a `ProjectCheck` with a clear,
/// human-readable reason on rejection so the project bar can show
/// actionable copy (phase-1 task 3: reject non-Unity folders with
/// clear error).
pub fn check_project(path: &Path, profile: &EngineProfile) -> ProjectCheck {
    let path_str = path.to_string_lossy().to_string();
    if !path.is_dir() {
        return ProjectCheck {
            valid: false,
            path: path_str.clone(),
            reason: Some(format!(
                "Not a directory: {path_str}. Pick the Unity project root folder."
            )),
        };
    }
    // All declared dirs must exist.
    for d in &profile.markers.dirs {
        if !path.join(d).is_dir() {
            return ProjectCheck {
                valid: false,
                path: path_str,
                reason: Some(format!(
                    "Not a {display} project: missing required folder \"{d}\".",
                    display = profile.display_name
                )),
            };
        }
    }
    // At least one declared marker file must exist.
    if !profile.markers.files.is_empty() {
        let any = profile.markers.files.iter().any(|f| path.join(f).is_file());
        if !any {
            return ProjectCheck {
                valid: false,
                path: path_str,
                reason: Some(format!(
                    "Not a {display} project: none of the marker files ({files}) were found.",
                    display = profile.display_name,
                    files = profile.markers.files.join(", ")
                )),
            };
        }
    }
    ProjectCheck {
        valid: true,
        path: path_str,
        reason: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schemas::{CompanionRule, ProfilePaths, ProjectMarkers};

    fn unity_profile() -> EngineProfile {
        EngineProfile {
            id: "unity".to_string(),
            display_name: "Unity Open MCP".to_string(),
            mcp_cli_binary: "unity-open-mcp".to_string(),
            paths: ProfilePaths {
                fixture_root: "Assets/_ValidationSuite/<test-id>/".to_string(),
                state_root: "UserSettings/ValidationSuite/".to_string(),
                state_file: "UserSettings/ValidationSuite/.state.json".to_string(),
                actuals_dir: "UserSettings/ValidationSuite/actuals/".to_string(),
                exports_dir: "UserSettings/ValidationSuite/exports/".to_string(),
            },
            markers: ProjectMarkers {
                dirs: vec!["Assets".to_string(), "ProjectSettings".to_string()],
                files: vec!["ProjectSettings/ProjectVersion.txt".to_string()],
            },
            companions: vec![CompanionRule {
                primary: "*.prefab".to_string(),
                companion: "*.prefab.meta".to_string(),
            }],
            placeholders: vec!["{fixtureRoot}".to_string(), "{projectRoot}".to_string()],
            tool_name_prefix: "unity_open_mcp_".to_string(),
        }
    }

    fn mkdir(p: &Path) {
        std::fs::create_dir_all(p).unwrap();
    }
    fn touch(p: &Path) {
        if let Some(parent) = p.parent() {
            mkdir(parent);
        }
        std::fs::write(p, b"2022.3").unwrap();
    }

    #[test]
    fn valid_unity_project_passes() {
        let dir = tempfile::tempdir().unwrap();
        mkdir(&dir.path().join("Assets"));
        mkdir(&dir.path().join("ProjectSettings"));
        touch(&dir.path().join("ProjectSettings/ProjectVersion.txt"));
        let check = check_project(dir.path(), &unity_profile());
        assert!(check.valid);
        assert!(check.reason.is_none());
    }

    #[test]
    fn missing_assets_dir_rejected_with_reason() {
        let dir = tempfile::tempdir().unwrap();
        mkdir(&dir.path().join("ProjectSettings"));
        touch(&dir.path().join("ProjectSettings/ProjectVersion.txt"));
        let check = check_project(dir.path(), &unity_profile());
        assert!(!check.valid);
        assert!(check.reason.unwrap().contains("Assets"));
    }

    #[test]
    fn missing_marker_file_rejected_with_reason() {
        let dir = tempfile::tempdir().unwrap();
        mkdir(&dir.path().join("Assets"));
        mkdir(&dir.path().join("ProjectSettings"));
        let check = check_project(dir.path(), &unity_profile());
        assert!(!check.valid);
        assert!(check.reason.unwrap().contains("marker files"));
    }

    #[test]
    fn non_directory_rejected() {
        let dir = tempfile::tempdir().unwrap();
        let file = dir.path().join("notafolder");
        std::fs::write(&file, b"x").unwrap();
        let check = check_project(&file, &unity_profile());
        assert!(!check.valid);
        assert!(check.reason.unwrap().contains("Not a directory"));
    }
}
