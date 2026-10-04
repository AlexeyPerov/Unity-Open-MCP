use std::collections::HashMap;
use std::path::Path;
use std::sync::{Arc, Mutex};
use tauri::State;

use crate::config::discovery::DiscoveryResult;
use crate::config::persistence;
use crate::config::schemas::{os_default_hub_paths, ProjectsFile, Settings};
use crate::config::walk_up_scan::WalkUpRegistry;

pub struct AppState {
    pub settings: Arc<Mutex<Settings>>,
    pub projects: Arc<Mutex<ProjectsFile>>,
    pub discovery_cache: Mutex<Option<DiscoveryResult>>,
    pub walk_up_registry: Mutex<WalkUpRegistry>,
}

/// Locked mutate-and-persist helper for `projects.json`. Acquires the
/// `projects` Mutex, clones the CURRENT live state, runs `mutate` to
/// produce the next state, persists it, and swaps it back into the Mutex
/// — all under the same lock acquisition so no concurrent writer can land
/// a change between the read and the write.
///
/// H12: this closes the "lost update" / stale-snapshot race that
/// `line_count::count_lines` (and the other long-running scans) had. The
/// previous pattern cloned the state under a short lock, ran a
/// multi-minute scan with the lock released, then took the lock again
/// and assigned the STALE snapshot back — clobbering any concurrent
/// add/remove/launch/flag mutation that landed during the scan. Callers
/// that need to do long work should do it BEFORE calling this helper
/// (producing a patch, not a full snapshot), then pass a `mutate` that
/// applies the patch to the fresh live state.
///
/// Returns the persisted next state on success, or the IO error on
/// failure (the live Mutex is left untouched on failure).
///
/// B-N27 — the lock is deliberately held across the disk write: releasing
/// it would re-open the lost-update race this helper exists to close (a
/// concurrent writer could land between our read and our swap, and we'd
/// clobber it). The trade-off is that a slow config volume serializes
/// every projects-touching Tauri command behind one `fsync` while the
/// mutex is held. The mitigation is contractual: callers MUST do all long
/// work (scans, copies, network) BEFORE calling this helper and pass a
/// `mutate` that only applies the resulting patch to the fresh live state
/// — so the only cost under the lock is the atomic write itself, which is
/// unavoidable for the read/swap atomicity guarantee. All callers (the
/// line-count scans, the launch/upgrade/refresh bookkeeping stamps, and
/// the add/remove/relink/flag mutations — see B-R6) follow this pattern.
/// If a future caller needs to persist after a genuinely long
/// synchronous operation, make that command `async` and offload the whole
/// `with_projects` call to `spawn_blocking`, mirroring `save_settings`.
pub fn with_projects<F>(
    projects: &Mutex<ProjectsFile>,
    mutate: F,
) -> Result<ProjectsFile, std::io::Error>
where
    F: FnOnce(&mut ProjectsFile),
{
    let mut guard = projects.lock().unwrap();
    let mut next: ProjectsFile = guard.clone();
    mutate(&mut next);
    persistence::save_projects(&next)?;
    *guard = next.clone();
    Ok(next)
}

/// Merge only fields changed by the caller, keeping independent backend updates.
/// Same-field conflicts fail before any file or in-memory state is changed.
fn merge_projects(
    current: &ProjectsFile,
    base: &ProjectsFile,
    desired: &ProjectsFile,
) -> Result<ProjectsFile, String> {
    use serde_json::Value;
    use std::collections::HashSet;
    for file in [base, desired] {
        let mut ids = HashSet::new();
        if file.projects.iter().any(|p| !ids.insert(&p.id)) {
            return Err("Duplicate project id in save request".into());
        }
    }
    let mut next = current.clone();
    for before in &base.projects {
        let after = desired.projects.iter().find(|p| p.id == before.id);
        let index = next.projects.iter().position(|p| p.id == before.id);
        let old = serde_json::to_value(before).map_err(|e| e.to_string())?;
        let Some(after) = after else {
            if let Some(index) = index {
                if serde_json::to_value(&next.projects[index]).map_err(|e| e.to_string())? != old {
                    return Err(format!(
                        "Project save conflict: {} changed before removal; reload and retry",
                        before.id
                    ));
                }
                next.projects.remove(index);
            }
            continue;
        };
        let new = serde_json::to_value(after).map_err(|e| e.to_string())?;
        if old == new {
            continue;
        }
        let index = index.ok_or_else(|| {
            format!(
                "Project save conflict: {} was removed; reload and retry",
                before.id
            )
        })?;
        let mut live = serde_json::to_value(&next.projects[index]).map_err(|e| e.to_string())?;
        let old = old.as_object().unwrap();
        let new = new.as_object().unwrap();
        let live = live.as_object_mut().unwrap();
        for key in old.keys().chain(new.keys()).collect::<HashSet<_>>() {
            if old.get(key) == new.get(key) {
                continue;
            }
            if live.get(key) != old.get(key) && live.get(key) != new.get(key) {
                return Err(format!(
                    "Project save conflict: {}.{} changed; reload and retry",
                    before.id, key
                ));
            }
            if let Some(value) = new.get(key) {
                live.insert(key.clone(), value.clone());
            } else {
                live.remove(key);
            }
        }
        next.projects[index] =
            serde_json::from_value(Value::Object(live.clone())).map_err(|e| e.to_string())?;
    }
    for added in desired
        .projects
        .iter()
        .filter(|p| !base.projects.iter().any(|b| b.id == p.id))
    {
        if let Some(existing) = next.projects.iter().find(|p| p.id == added.id) {
            if serde_json::to_value(existing).map_err(|e| e.to_string())?
                != serde_json::to_value(added).map_err(|e| e.to_string())?
            {
                return Err(format!(
                    "Project save conflict: {} already exists",
                    added.id
                ));
            }
        } else {
            next.projects.push(added.clone());
        }
    }
    Ok(next)
}

fn merge_and_save_projects(
    projects: &Mutex<ProjectsFile>,
    base: &ProjectsFile,
    desired: &ProjectsFile,
    persist: impl FnOnce(&ProjectsFile) -> std::io::Result<()>,
) -> Result<ProjectsFile, String> {
    let mut guard = projects.lock().unwrap();
    let next = merge_projects(&guard, base, desired)?;
    persist(&next).map_err(|e| e.to_string())?;
    *guard = next.clone();
    Ok(next)
}

// Startup initializes these states from disk. IPC reads must not replace live
// state with a disk snapshot taken before a concurrent mutation completed.
#[tauri::command]
pub async fn load_settings(state: State<'_, AppState>) -> Result<Settings, String> {
    let settings = Arc::clone(&state.settings);
    tauri::async_runtime::spawn_blocking(move || settings.lock().unwrap().clone())
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn save_settings(state: State<'_, AppState>, settings: Settings) -> Result<(), String> {
    let shared = Arc::clone(&state.settings);
    tauri::async_runtime::spawn_blocking(move || {
        let mut guard = shared.lock().unwrap();
        persistence::save_settings(&settings).map_err(|e| e.to_string())?;
        *guard = settings;
        Ok(())
    })
    .await
    .map_err(|e| e.to_string())?
}

#[tauri::command]
pub async fn load_projects(state: State<'_, AppState>) -> Result<ProjectsFile, String> {
    let projects = Arc::clone(&state.projects);
    tauri::async_runtime::spawn_blocking(move || projects.lock().unwrap().clone())
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn save_projects(
    state: State<'_, AppState>,
    projects: ProjectsFile,
    base: ProjectsFile,
) -> Result<ProjectsFile, String> {
    let shared = Arc::clone(&state.projects);
    tauri::async_runtime::spawn_blocking(move || {
        merge_and_save_projects(&shared, &base, &projects, persistence::save_projects)
    })
    .await
    .map_err(|e| e.to_string())?
}

/// Sync helper: one `stat` per path. Runs on the blocking thread pool
/// (see `check_paths_exists`) so a path on an unresponsive volume
/// (spun-down external drive, stale SMB share) cannot stall the
/// webview thread while its filesystem timeout elapses.
fn check_paths_exists_inner(paths: Vec<String>) -> HashMap<String, bool> {
    let mut result: HashMap<String, bool> = HashMap::with_capacity(paths.len());
    for path in paths {
        let exists = Path::new(&path).exists();
        result.insert(path, exists);
    }
    result
}

/// `paths` → whether each exists. `async` + `spawn_blocking` so a
/// missing/unresponsive path's filesystem timeout (often 15-60s on a
/// stale network mount) does not freeze the window on launch.
#[tauri::command]
pub async fn check_paths_exists(paths: Vec<String>) -> HashMap<String, bool> {
    let count = paths.len();
    let start = std::time::Instant::now();
    let result = tauri::async_runtime::spawn_blocking(move || check_paths_exists_inner(paths))
        .await
        .unwrap_or_default();
    log::info!(
        "check_paths_exists: {} paths in {}ms",
        count,
        start.elapsed().as_millis()
    );
    result
}

/// Returns the OS-default Unity Hub editor folders for the current
/// host. The Settings tab compares each "additional parent folder"
/// row against this list to decide whether to render a Remove button
/// (default paths are informational and not user-removable).
#[tauri::command]
pub fn get_os_default_hub_paths() -> Vec<String> {
    os_default_hub_paths()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn check_paths_exists_returns_true_for_existing() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().to_string_lossy().to_string();
        let result = check_paths_exists_inner(vec![path.clone()]);
        assert_eq!(result.get(&path), Some(&true));
    }

    #[test]
    fn check_paths_exists_returns_false_for_missing() {
        let result = check_paths_exists_inner(vec!["/nonexistent/path/xyz_123".to_string()]);
        assert_eq!(result.get("/nonexistent/path/xyz_123"), Some(&false));
    }

    #[test]
    fn check_paths_exists_handles_empty_input() {
        let result = check_paths_exists_inner(vec![]);
        assert!(result.is_empty());
    }

    #[test]
    fn check_paths_exists_mixes_existing_and_missing() {
        let dir = tempfile::tempdir().unwrap();
        let existing = dir.path().to_string_lossy().to_string();
        let missing = format!("{}/nope", existing);
        let result = check_paths_exists_inner(vec![existing.clone(), missing.clone()]);
        assert_eq!(result.get(&existing), Some(&true));
        assert_eq!(result.get(&missing), Some(&false));
    }
}

#[cfg(test)]
mod project_save_tests {
    use super::*;
    use serde_json::json;

    fn file() -> ProjectsFile {
        serde_json::from_value(json!({"version":1,"projects":[{"id":"a","name":"A","path":"/a"}]}))
            .unwrap()
    }

    #[test]
    fn stale_save_preserves_backend_fields_and_new_projects() {
        let base = file();
        let mut live = base.clone();
        live.projects[0].frecency = 42;
        let mut added = live.projects[0].clone();
        added.id = "b".into();
        live.projects.push(added);
        let mut desired = base.clone();
        desired.projects[0].launch_args = Some("-batchmode".into());
        let merged = merge_projects(&live, &base, &desired).unwrap();
        assert_eq!(merged.projects.len(), 2);
        assert_eq!(merged.projects[0].frecency, 42);
        assert_eq!(
            merged.projects[0].launch_args.as_deref(),
            Some("-batchmode")
        );
    }

    #[test]
    fn changed_field_conflicts_and_removed_projects_are_not_resurrected() {
        let base = file();
        let mut live = base.clone();
        live.projects[0].name = "Backend name".into();
        let mut desired = base.clone();
        desired.projects[0].name = "Frontend name".into();
        assert!(merge_projects(&live, &base, &desired).is_err());
        live.projects.clear();
        assert!(merge_projects(&live, &base, &desired).is_err());
    }

    #[test]
    fn save_failure_keeps_memory_and_disk_unchanged() {
        let base = file();
        let shared = Mutex::new(base.clone());
        let mut desired = base.clone();
        desired.projects[0].name = "New name".into();
        let result = merge_and_save_projects(&shared, &base, &desired, |_| {
            Err(std::io::Error::other("disk full"))
        });
        assert!(result.is_err());
        assert_eq!(shared.lock().unwrap().projects[0].name, "A");
    }

    #[test]
    fn overlapping_saves_publish_the_same_state_to_disk_and_memory() {
        let dir = tempfile::tempdir().unwrap();
        let path = Arc::new(dir.path().join("projects.json"));
        let base = file();
        let shared = Arc::new(Mutex::new(base.clone()));
        let (entered_tx, entered_rx) = std::sync::mpsc::channel();
        let (release_tx, release_rx) = std::sync::mpsc::channel();
        let a = Arc::clone(&shared);
        let a_path = Arc::clone(&path);
        let a_base = base.clone();
        let first = std::thread::spawn(move || {
            let mut desired = a_base.clone();
            desired.projects[0].name = "New name".into();
            merge_and_save_projects(&a, &a_base, &desired, |next| {
                entered_tx.send(()).unwrap();
                release_rx.recv().unwrap();
                persistence::atomic_write_at(&a_path, &serde_json::to_string(next).unwrap())
            })
            .unwrap();
        });
        entered_rx.recv().unwrap();
        assert!(
            shared.try_lock().is_err(),
            "state stays locked while disk persistence runs"
        );
        let b = Arc::clone(&shared);
        let b_path = Arc::clone(&path);
        let second = std::thread::spawn(move || {
            let mut desired = base.clone();
            desired.projects[0].launch_args = Some("-batchmode".into());
            merge_and_save_projects(&b, &base, &desired, |next| {
                persistence::atomic_write_at(&b_path, &serde_json::to_string(next).unwrap())
            })
            .unwrap();
        });
        release_tx.send(()).unwrap();
        first.join().unwrap();
        second.join().unwrap();
        let disk: ProjectsFile =
            serde_json::from_str(&std::fs::read_to_string(&*path).unwrap()).unwrap();
        assert_eq!(disk.projects[0].name, "New name");
        assert_eq!(disk.projects[0].launch_args.as_deref(), Some("-batchmode"));
        assert_eq!(
            serde_json::to_value(&disk).unwrap(),
            serde_json::to_value(&*shared.lock().unwrap()).unwrap()
        );
    }
}
