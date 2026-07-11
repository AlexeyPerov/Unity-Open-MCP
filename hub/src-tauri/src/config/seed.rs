use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

use chrono::TimeZone;
use serde::Deserialize;
use tauri::State;

use crate::config::commands::AppState;
use crate::config::paths;
use crate::config::persistence;
use crate::config::schemas::{ProjectEntry, ProjectKind, ProjectsFile};

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

fn unix_ms_to_iso8601(ms: u64) -> Option<String> {
    let secs = (ms / 1000) as i64;
    chrono::Utc.timestamp_opt(secs, 0).single().map(|dt| dt.to_rfc3339())
}

fn read_hub_projects() -> Result<(Vec<HubProjectData>, String), String> {
    let dir = paths::unity_hub_data_dir()
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
            path: hub_project.path.clone(),
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
            env_vars: Default::default(),
            // M15 T6.4: enrich the seeded rows with SRP + build-target
            // metadata so they show the same chips as manually-added
            // rows. We only run detection when the path actually
            // exists — a Hub entry pointing at a removed folder is
            // still seeded (so the user can relink it) but cannot be
            // inspected.
            render_pipeline: if path_exists {
                Some(
                    crate::config::render_pipeline::read_render_pipeline(Path::new(&hub_project.path))
                        .label()
                        .to_string(),
                )
            } else {
                None
            },
            default_build_target: if path_exists {
                crate::config::build_target::read_default_build_target(Path::new(&hub_project.path))
                    .target
            } else {
                None
            },
            kind: ProjectKind::Unity,
            package_manifest_path: None,
            migrate_source_folder: None,
            line_count_stats: None,
            ai_setup_wizard: None,
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

/// M15 T6.4: candidate project from a Unity Hub scan. The candidate
/// shape is a slimmed-down `ProjectEntry` (no `id`, `frecency`, or
/// per-project settings) plus the path-exists flag so the UI can show
/// "missing path" candidates the same way it shows missing tracked
/// rows. The frontend never persists these directly — each candidate
/// the user accepts goes through `add_project`, which produces a real
/// `ProjectEntry` and writes it to `projects.json`.
#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HubProjectCandidate {
    pub name: String,
    pub path: String,
    pub exists: bool,
    pub unity_version: Option<String>,
    pub last_modified_at: Option<String>,
    pub render_pipeline: Option<String>,
    pub default_build_target: Option<String>,
    /// `true` when the candidate's canonical path already matches an
    /// entry in `projects.json` — the frontend renders these as
    /// "tracked" and hides the import button.
    pub already_tracked: bool,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HubCandidatesResult {
    pub candidates: Vec<HubProjectCandidate>,
    /// `None` when the scan succeeded (even with zero candidates).
    /// `Some(message)` when Unity Hub's data directory or projects
    /// file could not be read — the UI surfaces the message inline
    /// rather than as a hard error.
    pub error: Option<String>,
}

/// Live, read-only scan of Unity Hub's recent-projects list. The
/// `projects-v1.json` file under Hub's data directory is the
/// authoritative source for "what Unity Hub thinks the user has been
/// working on". Unlike `seed_from_unity_hub`, this command does **not**
/// mutate `projects.json` — it returns the candidate list so the UI can
/// render an "import from Hub" panel and the user can pick which
/// candidates to add.
///
/// The candidate list is merged with the current `projects.json` so
/// `already_tracked` is correct: every candidate whose canonical path
/// matches an existing entry is flagged, regardless of how that entry
/// was originally added (hub-seed, manual, walk-up).
#[tauri::command]
pub fn discover_hub_projects(state: State<AppState>) -> HubCandidatesResult {
    let (hub_entries, _schema) = match read_hub_projects() {
        Ok(v) => v,
        Err(e) => {
            log::info!("Hub candidate scan skipped: {}", e);
            return HubCandidatesResult {
                candidates: Vec::new(),
                error: Some(e),
            };
        }
    };

    // Snapshot the current tracked paths so we can flag duplicates
    // without holding the lock through the per-candidate filesystem
    // probes (a path on a spun-down drive can stall for the filesystem
    // timeout).
    let tracked_paths: std::collections::HashSet<String> = {
        let guard = state.projects.lock().unwrap();
        guard
            .projects
            .iter()
            .map(|p| canonicalize_for_compare(&p.path))
            .collect()
    };

    let mut candidates: Vec<HubProjectCandidate> = Vec::new();
    for hub_project in hub_entries {
        let path_exists = Path::new(&hub_project.path).exists();
        let already_tracked = tracked_paths.contains(&canonicalize_for_compare(&hub_project.path));
        // SRP + build-target are only computed when the path actually
        // exists — a missing candidate keeps the fields `None` so the
        // UI can still show the entry (with a "missing path" chip) and
        // let the user relink it via Add Project.
        let (render_pipeline, default_build_target) = if path_exists {
            (
                Some(
                    crate::config::render_pipeline::read_render_pipeline(Path::new(&hub_project.path))
                        .label()
                        .to_string(),
                ),
                crate::config::build_target::read_default_build_target(Path::new(&hub_project.path))
                    .target,
            )
        } else {
            (None, None)
        };
        candidates.push(HubProjectCandidate {
            name: hub_project.title,
            path: hub_project.path,
            exists: path_exists,
            unity_version: hub_project.version,
            last_modified_at: hub_project.last_modified.and_then(unix_ms_to_iso8601),
            render_pipeline,
            default_build_target,
            already_tracked,
        });
    }

    // Sort most-recent first so the UI defaults to the projects the
    // user actually opened lately. `Option<&String>::cmp` puts `None`
    // last in a descending sort.
    candidates.sort_by(|a, b| b.last_modified_at.as_ref().cmp(&a.last_modified_at.as_ref()));

    HubCandidatesResult {
        candidates,
        error: None,
    }
}

/// Canonicalise a path for the duplicate check. Mirrors
/// `projects::canonicalize_for_compare` but lives here so the seed
/// module stays self-contained — both helpers must agree on the
/// canonical form or `already_tracked` would falsely report a fresh
/// candidate for a path Hub happens to spell differently.
fn canonicalize_for_compare(path: &str) -> String {
    let p = PathBuf::from(path);
    fs::canonicalize(&p)
        .map(|c| c.to_string_lossy().to_string())
        .unwrap_or_else(|_| path.to_string())
}
