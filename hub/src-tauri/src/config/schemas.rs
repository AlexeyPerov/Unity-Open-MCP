use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub version: u32,
    pub launch: LaunchSettings,
    pub project_list: ProjectListSettings,
    pub safety: SafetySettings,
    pub unity_discovery: UnityDiscoverySettings,
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
    pub search_includes_path: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SafetySettings {
    pub confirm_kill_unity: bool,
    pub confirm_remove_project: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UnityDiscoverySettings {
    pub parent_folders: Vec<String>,
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
                search_includes_path: true,
            },
            safety: SafetySettings {
                confirm_kill_unity: true,
                confirm_remove_project: true,
            },
            unity_discovery: UnityDiscoverySettings {
                parent_folders: vec![
                    "/Applications/Unity/Hub/Editor".to_string(),
                    "C:\\Program Files\\Unity\\Hub\\Editor".to_string(),
                ],
            },
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
    }

    #[test]
    fn settings_default_project_list_all_visible() {
        let s = Settings::default();
        assert!(s.project_list.show_path_column);
        assert!(s.project_list.show_modified_column);
        assert!(s.project_list.search_includes_path);
    }

    #[test]
    fn settings_default_discovery_has_two_folders() {
        let s = Settings::default();
        assert_eq!(s.unity_discovery.parent_folders.len(), 2);
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
        };
        let json = serde_json::to_string(&entry).unwrap();
        assert!(!json.contains("unityVersion"));
        assert!(!json.contains("lastOpenedAt"));
        assert!(!json.contains("lastLaunchPid"));
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
