use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

use chrono::TimeZone;
use serde::Deserialize;
use tauri::State;

use crate::config::commands::AppState;
use crate::config::persistence;
use crate::config::schemas::{ProjectEntry, ProjectsFile};

#[derive(Debug, Deserialize)]
struct HubProjectsV1 {
    #[serde(rename = "schema_version")]
    schema_version: String,
    data: HashMap<String, HubProjectData>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct HubProjectData {
    title: String,
    last_modified: Option<u64>,
    path: String,
    version: Option<String>,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SeedResult {
    pub projects: ProjectsFile,
    pub seeded_count: usize,
    pub skipped_paths: Vec<String>,
    pub error: Option<String>,
}

/// OS-specific Unity Hub data directory.
///
/// - macOS: `~/Library/Application Support/UnityHub/`
/// - Windows: `%APPDATA%\UnityHub\`
/// - Linux: `~/.config/UnityHub/`
fn unity_hub_data_dir() -> Option<PathBuf> {
    if cfg!(target_os = "macos") {
        dirs::home_dir().map(|h| h.join("Library/Application Support/UnityHub"))
    } else if cfg!(target_os = "windows") {
        dirs::data_dir().map(|d| d.join("UnityHub"))
    } else {
        dirs::home_dir().map(|h| h.join(".config/UnityHub"))
    }
}

fn unix_ms_to_iso8601(ms: u64) -> Option<String> {
    let secs = (ms / 1000) as i64;
    chrono::Utc.timestamp_opt(secs, 0).single().map(|dt| dt.to_rfc3339())
}

fn read_hub_projects() -> Result<(Vec<HubProjectData>, String), String> {
    let dir = unity_hub_data_dir()
        .ok_or("Cannot determine Unity Hub data directory for this platform")?;

    if !dir.exists() {
        return Err(format!(
            "Unity Hub data directory not found: {}",
            dir.display()
        ));
    }

    let path = dir.join("projects-v1.json");
    if !path.exists() {
        return Err(format!(
            "Unity Hub projects file not found: {}",
            path.display()
        ));
    }

    let content = fs::read_to_string(&path)
        .map_err(|e| format!("Failed to read Unity Hub projects: {}", e))?;

    let hub_projects: HubProjectsV1 = serde_json::from_str(&content)
        .map_err(|e| format!("Failed to parse Unity Hub projects: {}", e))?;

    Ok((
        hub_projects.data.into_values().collect(),
        format!("hub-v{}", hub_projects.schema_version),
    ))
}

#[tauri::command]
pub fn seed_from_unity_hub(state: State<AppState>) -> SeedResult {
    {
        // Skip on a non-empty list. Note: an empty array also counts as
        // "already has projects" (the user has explicitly cleared the
        // list), so the seed will not re-run after a full removal. There
        // is no M1 UI to re-import from Unity Hub — see backlog.md.
        let guard = state.projects.lock().unwrap();
        if !guard.projects.is_empty() {
            return SeedResult {
                projects: guard.clone(),
                seeded_count: 0,
                skipped_paths: vec![],
                error: None,
            };
        }
    }

    let (hub_entries, _schema) = match read_hub_projects() {
        Ok(v) => v,
        Err(e) => {
            log::info!("Unity Hub seed skipped: {}", e);
            return SeedResult {
                projects: ProjectsFile::default(),
                seeded_count: 0,
                skipped_paths: vec![],
                error: Some(e),
            };
        }
    };

    let mut entries: Vec<ProjectEntry> = Vec::new();
    let mut skipped_paths: Vec<String> = Vec::new();

    for hub_project in hub_entries {
        let path_exists = Path::new(&hub_project.path).exists();
        let last_modified_at = hub_project
            .last_modified
            .and_then(unix_ms_to_iso8601);

        if !path_exists {
            log::warn!(
                "Seed project path does not exist: {}",
                hub_project.path
            );
            skipped_paths.push(hub_project.path.clone());
        }

        entries.push(ProjectEntry {
            id: uuid::Uuid::new_v4().to_string(),
            name: hub_project.title,
            path: hub_project.path,
            unity_version: hub_project.version,
            last_opened_at: None,
            last_modified_at,
            launch_args: None,
            platform_intent: None,
            last_launch_pid: None,
            last_launch_at: None,
            frecency: 0,
            git_branch: None,
            source: "hub-seed".to_string(),
            hidden: false,
            stale: false,
        });
    }

    // Most-recent first. `Option<&String>::cmp` puts `None` last in a
    // descending sort (since `None < Some(_)`), so projects with a
    // missing timestamp are pushed to the bottom.
    entries.sort_by(|a, b| {
        b.last_modified_at
            .as_ref()
            .cmp(&a.last_modified_at.as_ref())
    });

    let seeded_count = entries.len();
    let projects_file = ProjectsFile {
        version: 1,
        projects: entries,
    };

    if let Err(e) = persistence::save_projects(&projects_file) {
        log::error!("Failed to save seeded projects: {}", e);
        return SeedResult {
            projects: ProjectsFile::default(),
            seeded_count: 0,
            skipped_paths,
            error: Some(format!("Failed to save seeded projects: {}", e)),
        };
    }

    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects_file.clone();
    }

    log::info!(
        "Seeded {} projects from Unity Hub",
        seeded_count
    );

    SeedResult {
        projects: projects_file,
        seeded_count,
        skipped_paths,
        error: None,
    }
}
