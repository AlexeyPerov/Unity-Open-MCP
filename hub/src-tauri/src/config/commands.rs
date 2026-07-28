use std::collections::HashMap;
use std::path::Path;
use tauri::State;
use std::sync::Mutex;

use crate::config::discovery::DiscoveryResult;
use crate::config::persistence;
use crate::config::schemas::{os_default_hub_paths, ProjectsFile, Settings};
use crate::config::walk_up_scan::WalkUpRegistry;

pub struct AppState {
    pub settings: Mutex<Settings>,
    pub projects: Mutex<ProjectsFile>,
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
/// unavoidable for the read/swap atomicity guarantee. The two current
/// callers (`count_lines`, `count_lines_cached`) already follow this
/// pattern. If a future caller needs to persist after a genuinely long
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

#[tauri::command]
pub async fn load_settings(state: State<'_, AppState>) -> Result<Settings, String> {
    // The `persistence::load_settings` disk read (which can create the
    // default file on first run) is the only main-thread disk I/O on the
    // critical launch path — `projectsStore.load()` awaits this before
    // the path/size/branch scans even start. Offload the read to the
    // blocking pool so the webview thread never stalls on it. The
    // Mutex lock+clone stays on the async task (cheap).
    //
    // Tauri requires async commands that borrow `State` to return a
    // `Result` (see `discover_installations`); `Ok(T)` is unwrapped to
    // `T` on the JS side, so the frontend contract is unchanged.
    //
    // Entry/lock/return spans (visible under `RUST_LOG=info`) pinpoint a
    // hang: no `entered` ⇒ stuck in the Tauri IPC layer; `entered` but
    // no `lock acquired` ⇒ the Mutex is held elsewhere; both present but
    // no `returning` ⇒ the disk read is wedged.
    log::info!("load_settings: entered");
    let start = std::time::Instant::now();
    let settings = tauri::async_runtime::spawn_blocking(persistence::load_settings)
        .await
        .map_err(|e| format!("load_settings task failed: {e}"))?;
    log::info!(
        "load_settings: spawn_blocking done in {}ms, awaiting lock",
        start.elapsed().as_millis()
    );
    let mut guard = state.settings.lock().unwrap();
    log::info!(
        "load_settings: lock acquired after {}ms total",
        start.elapsed().as_millis()
    );
    *guard = settings.clone();
    log::info!("load_settings: returning after {}ms", start.elapsed().as_millis());
    Ok(settings)
}

#[tauri::command]
pub async fn save_settings(state: State<'_, AppState>, settings: Settings) -> Result<(), String> {
    // Mirror `load_settings`: offload the disk write (atomic tmp + `fsync`
    // + rename) to the blocking pool so a slow/cloud-synced config volume
    // cannot stall the WebView main thread. The Mutex clone-update stays
    // on the async task. `spawn_blocking` needs an owned `Settings`; the
    // caller's value is cloned for the disk write and moved into the guard.
    let disk = settings.clone();
    tauri::async_runtime::spawn_blocking(move || persistence::save_settings(&disk))
        .await
        .map_err(|e| format!("save_settings task failed: {e}"))?
        .map_err(|e| e.to_string())?;
    let mut guard = state.settings.lock().unwrap();
    *guard = settings;
    Ok(())
}

#[tauri::command]
pub async fn load_projects(state: State<'_, AppState>) -> Result<ProjectsFile, String> {
    // Entry/lock/return spans mirror `load_settings` — see the comment
    // there for how to read them under a launch freeze.
    log::info!("load_projects: entered");
    let start = std::time::Instant::now();
    let projects = tauri::async_runtime::spawn_blocking(persistence::load_projects)
        .await
        .map_err(|e| format!("load_projects task failed: {e}"))?;
    log::info!(
        "load_projects: spawn_blocking done in {}ms, awaiting lock",
        start.elapsed().as_millis()
    );
    let mut guard = state.projects.lock().unwrap();
    log::info!(
        "load_projects: lock acquired after {}ms total",
        start.elapsed().as_millis()
    );
    *guard = projects.clone();
    log::info!("load_projects: returning after {}ms", start.elapsed().as_millis());
    Ok(projects)
}

#[tauri::command]
pub async fn save_projects(state: State<'_, AppState>, projects: ProjectsFile) -> Result<(), String> {
    // Mirror `save_settings` / `load_projects`: the atomic write (tmp +
    // `fsync` + rename of projects.json) runs on the blocking pool so the
    // wizard's draft-persistence path cannot freeze the UI on a slow disk.
    let disk = projects.clone();
    tauri::async_runtime::spawn_blocking(move || persistence::save_projects(&disk))
        .await
        .map_err(|e| format!("save_projects task failed: {e}"))?
        .map_err(|e| e.to_string())?;
    let mut guard = state.projects.lock().unwrap();
    *guard = projects;
    Ok(())
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
        assert_eq!(
            result.get("/nonexistent/path/xyz_123"),
            Some(&false)
        );
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
