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
