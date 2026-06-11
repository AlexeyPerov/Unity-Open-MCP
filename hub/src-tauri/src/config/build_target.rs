use std::path::Path;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum BuildTargetSource {
    /// Value read from `ProjectSettings/ProjectSettings.asset` (`m_BuildTarget`,
    /// falling back to `m_BuildTargetGroup`).
    ProjectSettings,
    /// The project file is missing or the build-target keys are not present.
    /// Typical for projects that have never been opened by a Unity Editor on
    /// this machine — Unity writes `m_BuildTarget` only after the first save.
    NotRecorded,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DefaultBuildTarget {
    /// The BuildTarget/BuildTargetGroup enum name as Unity writes it
    /// (e.g. `StandaloneWindows64`, `Android`, `iPhone`, `iOS`, `WebGL`).
    pub target: Option<String>,
    pub source: BuildTargetSource,
}

const PROJECT_SETTINGS_FILE: &str = "ProjectSettings/ProjectSettings.asset";

/// Reads the project's stored active build target from
/// `ProjectSettings/ProjectSettings.asset`.
///
/// Unity persists the Editor's active build target into
/// `ProjectSettings/ProjectSettings.asset` under `m_BuildTarget` (newer
/// Unity: a `BuildTarget` enum value) and `m_BuildTargetGroup` (a
/// `BuildTargetGroup` enum value, present since Unity 5+). We prefer
/// `m_BuildTarget` because it matches the values Hub already exposes in
/// the platform-intent dropdown; if it's missing we fall back to
/// `m_BuildTargetGroup`.
///
/// On a freshly-cloned project that has never been opened by a Unity
/// Editor, neither key may be present — Unity writes them on first save.
/// In that case `target` is `None` and `source` is `NotRecorded` so the
/// UI can fall back to "Unity will use its default".
pub fn read_default_build_target(project_path: &Path) -> DefaultBuildTarget {
    let asset = project_path.join(PROJECT_SETTINGS_FILE);
    let Ok(contents) = std::fs::read_to_string(&asset) else {
        return DefaultBuildTarget {
            target: None,
            source: BuildTargetSource::NotRecorded,
        };
    };

    if let Some(value) = extract_yaml_value(&contents, "m_BuildTarget") {
        return DefaultBuildTarget {
            target: Some(value),
            source: BuildTargetSource::ProjectSettings,
        };
    }

    if let Some(value) = extract_yaml_value(&contents, "m_BuildTargetGroup") {
        return DefaultBuildTarget {
            target: Some(value),
            source: BuildTargetSource::ProjectSettings,
        };
    }

    DefaultBuildTarget {
        target: None,
        source: BuildTargetSource::NotRecorded,
    }
}

/// Extracts a top-level scalar YAML value for `key:` from a Unity-style
/// asset file. Handles both quoted (`m_BuildTarget: 'Android'`) and
/// unquoted (`m_BuildTarget: Android`) forms. Returns the raw enum
/// string, or `None` if the key is absent.
fn extract_yaml_value(contents: &str, key: &str) -> Option<String> {
    let needle = format!("{key}:");
    for line in contents.lines() {
        let trimmed = line.trim_start();
        let Some(rest) = trimmed.strip_prefix(needle.as_str()) else {
            continue;
        };
        let value = rest.trim();
        if value.is_empty() || value.starts_with('#') {
            continue;
        }
        let value = value
            .strip_prefix('\'')
            .and_then(|v| v.strip_suffix('\''))
            .or_else(|| value.strip_prefix('"').and_then(|v| v.strip_suffix('"')))
            .unwrap_or(value);
        if value.is_empty() {
            continue;
        }
        return Some(value.to_string());
    }
    None
}

#[tauri::command]
pub fn get_default_build_target(project_path: String) -> DefaultBuildTarget {
    read_default_build_target(Path::new(&project_path))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fresh_dir(name: &str) -> std::path::PathBuf {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(name);
        std::fs::create_dir_all(&path).unwrap();
        std::mem::forget(dir);
        path
    }

    fn write_project_settings(project: &Path, body: &str) {
        std::fs::create_dir_all(project.join("ProjectSettings")).unwrap();
        std::fs::write(project.join("ProjectSettings/ProjectSettings.asset"), body).unwrap();
    }

    #[test]
    fn reads_m_build_target_value() {
        let project = fresh_dir("ProjA");
        write_project_settings(
            &project,
            "%YAML 1.1\n---\nPlayerSettings:\n  m_BuildTarget: Android\n  m_BuildTargetGroup: Android\n",
        );
        let info = read_default_build_target(&project);
        assert_eq!(info.target.as_deref(), Some("Android"));
        assert_eq!(info.source, BuildTargetSource::ProjectSettings);
    }

    #[test]
    fn prefers_m_build_target_over_m_build_target_group() {
        let project = fresh_dir("ProjB");
        write_project_settings(
            &project,
            "  m_BuildTarget: iPhone\n  m_BuildTargetGroup: iOS\n",
        );
        let info = read_default_build_target(&project);
        assert_eq!(info.target.as_deref(), Some("iPhone"));
    }

    #[test]
    fn falls_back_to_m_build_target_group() {
        let project = fresh_dir("ProjC");
        write_project_settings(&project, "  m_BuildTargetGroup: Standalone\n");
        let info = read_default_build_target(&project);
        assert_eq!(info.target.as_deref(), Some("Standalone"));
        assert_eq!(info.source, BuildTargetSource::ProjectSettings);
    }

    #[test]
    fn handles_quoted_value() {
        let project = fresh_dir("ProjD");
        write_project_settings(&project, "  m_BuildTarget: 'WebGL'\n");
        let info = read_default_build_target(&project);
        assert_eq!(info.target.as_deref(), Some("WebGL"));
    }

    #[test]
    fn missing_file_yields_not_recorded() {
        let project = fresh_dir("ProjE");
        let info = read_default_build_target(&project);
        assert_eq!(info.target, None);
        assert_eq!(info.source, BuildTargetSource::NotRecorded);
    }

    #[test]
    fn missing_keys_yield_not_recorded() {
        let project = fresh_dir("ProjF");
        write_project_settings(&project, "  productName: New\n  bundleVersion: 0.1\n");
        let info = read_default_build_target(&project);
        assert_eq!(info.target, None);
        assert_eq!(info.source, BuildTargetSource::NotRecorded);
    }

    #[test]
    fn skips_commented_out_lines() {
        let project = fresh_dir("ProjG");
        write_project_settings(&project, "  # m_BuildTarget: OldValue\n  m_BuildTarget: Switch\n");
        let info = read_default_build_target(&project);
        assert_eq!(info.target.as_deref(), Some("Switch"));
    }

    #[test]
    fn command_matches_resolver() {
        let project = fresh_dir("ProjH");
        write_project_settings(&project, "  m_BuildTarget: iOS\n");
        let direct = read_default_build_target(&project);
        let via_cmd = get_default_build_target(project.to_string_lossy().to_string());
        assert_eq!(direct, via_cmd);
    }
}
