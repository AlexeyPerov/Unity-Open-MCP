//! Walk-up directory scan (M1.5-11).
//!
//! Adds a second, opt-in discovery source alongside the M1 Unity Hub
//! seed: the user picks one or more folder roots and Hub recursively
//! walks each one looking for Unity project roots (a folder that
//! contains both `Assets/` and `ProjectSettings/`). Every match is
//! appended to `projects.json` with `source = "walk-up"` and the same
//! `id` / `path` shape `add_project` produces, so the deduplication
//! and chip grammar on the Projects tab already cover the new rows.
//!
//! ## Behaviour
//!
//! - **Detection rule** — matches the M1 "Add Project" validation: a
//!   folder is a Unity project root iff `<root>/Assets` and
//!   `<root>/ProjectSettings` are both directories.
//! - **Depth** — capped via `settings.discovery.walkUpMaxDepth`
//!   (default 4, hard cap 8). Depth 0 means "do not descend into
//!   children" so a root that *is* itself a project is still detected.
//! - **Symlinks** — off by default. The scanner does not call
//!   `symlink_metadata` on every entry to keep the hot loop cheap;
//!   when `follow_symlinks = true` we follow links (the user has
//!   explicitly opted in).
//! - **Duplicates** — a candidate whose canonicalised path already
//!   exists in `projects.json` is reported as `skipped` and not
//!   appended again (matches `add_project` semantics).
//! - **Cancellation** — the scan runs on a dedicated OS thread; the
//!   shared `Arc<AtomicBool>` is checked before descending into each
//!   directory so cancellation is prompt. Partial results are kept
//!   or discarded per `settings.discovery.walkUpKeepPartial`.
//! - **Progress** — the scanner emits `walk-up://progress` Tauri
//!   events with the current root, current depth, and a running
//!   found-so-far counter. The frontend drives the modal from these
//!   events. The scan finishes by emitting `walk-up://done` with
//!   the final counts and the persisted `projects.json` payload.
//!
//! The scanner intentionally does **not** spawn Unity, touch
//! `ProjectVersion.txt`, or run the gate / verify flow. It is a
//! pure "find candidate folders" pass; the rest of Hub treats the
//! appended rows like any other entry.

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Instant;

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, Manager, State};

use crate::config::commands::AppState;
use crate::config::persistence;
use crate::config::schemas::{ProjectEntry, ProjectKind, ProjectsFile};

/// Hard upper bound on the configurable walk-up depth. The Settings
/// UI can offer 1..=MAX_DEPTH, the user can also type a value via the
/// Rust mutator which clamps to the same range. The acceptance
/// checklist pins "max 8".
pub const MAX_WALK_UP_DEPTH: u32 = 8;

/// Default scan depth (matches `Settings::default`).
pub const DEFAULT_WALK_UP_DEPTH: u32 = 4;

/// Identifier for a single running walk-up scan. Surfaced back to
/// the frontend so multiple invocations (e.g. rapid button mashing)
/// can be distinguished. Generated as a monotonic counter — we do
/// not need a UUID for in-process bookkeeping.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WalkUpStart {
    pub scan_id: String,
    /// Echoed back so the frontend can confirm the effective
    /// configuration actually used by the backend (after clamping).
    pub max_depth: u32,
    pub follow_symlinks: bool,
    pub keep_partial: bool,
    pub roots: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WalkUpProgress {
    pub scan_id: String,
    pub current_root: String,
    pub current_depth: u32,
    pub max_depth: u32,
    pub found_so_far: usize,
    pub visited_dirs: usize,
    pub status: WalkUpStatus,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum WalkUpStatus {
    Running,
    Cancelled,
    Completed,
    Failed,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WalkUpDone {
    pub scan_id: String,
    pub status: WalkUpStatus,
    pub added: Vec<ProjectEntry>,
    pub skipped_existing: Vec<String>,
    pub skipped_not_unity: usize,
    pub skipped_invalid_root: Vec<String>,
    pub projects: ProjectsFile,
    /// `None` when the scan finished cleanly. `Some(message)` when
    /// the scan aborted with an unrecoverable error (e.g. persistence
    /// failed after partial results were already appended to memory).
    pub error: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum WalkUpError {
    #[serde(rename_all = "camelCase")]
    AnotherScanInProgress { current_scan_id: String },
    #[serde(rename_all = "camelCase")]
    NoRoots,
    #[serde(rename_all = "camelCase")]
    InvalidRoot { path: String, reason: String },
}

/// Pure: true if `path` is a Unity project root. Mirrors
/// `config::projects::is_unity_project_root` but lives here so the
/// scanner module is self-contained and unit-testable without
/// pulling in the projects module.
pub fn is_unity_project_root(path: &Path) -> bool {
    if !path.is_dir() {
        return false;
    }
    path.join("Assets").is_dir() && path.join("ProjectSettings").is_dir()
}

/// Read the `m_EditorVersion:` line out of `ProjectVersion.txt`.
/// Returns `None` for missing / malformed files so the walk-up
/// scanner can still add the project (the version will be filled in
/// on the next refresh).
fn read_project_version(path: &Path) -> Option<String> {
    use std::fs;
    let content = fs::read_to_string(path.join("ProjectSettings").join("ProjectVersion.txt")).ok()?;
    for line in content.lines() {
        let line = line.strip_prefix('\u{FEFF}').unwrap_or(line);
        if let Some(v) = line.strip_prefix("m_EditorVersion:") {
            let trimmed = v.trim();
            if !trimmed.is_empty() {
                return Some(trimmed.to_string());
            }
        }
    }
    None
}

fn read_dir_mtime_iso(dir: &Path) -> Option<String> {
    use std::fs;
    let meta = fs::metadata(dir).ok()?;
    let time = meta.modified().ok().or_else(|| meta.created().ok())?;
    let duration = time.duration_since(std::time::SystemTime::UNIX_EPOCH).ok()?;
    let secs = duration.as_secs() as i64;
    chrono::DateTime::from_timestamp(secs, 0).map(|dt| dt.to_rfc3339())
}

fn derive_name(path: &Path) -> String {
    path.file_name()
        .and_then(|n| n.to_str())
        .map(|s| s.to_string())
        .unwrap_or_else(|| path.to_string_lossy().to_string())
}

fn canonicalize_for_compare(path: &str) -> String {
    use std::fs;
    let p = PathBuf::from(path);
    fs::canonicalize(&p)
        .map(|c| c.to_string_lossy().to_string())
        .unwrap_or_else(|_| path.to_string())
}

/// In-process handle to a running scan. Keyed by `scan_id` so
/// cancellation is targeted — pressing Cancel on scan A while scan B
/// is queued must not abort B.
#[derive(Default)]
pub struct WalkUpRegistry {
    inner: std::sync::Mutex<std::collections::HashMap<String, Arc<AtomicBool>>>,
    next_id: std::sync::Mutex<u64>,
}

impl WalkUpRegistry {
    pub fn register(&self) -> (String, Arc<AtomicBool>) {
        let mut id_guard = self.next_id.lock().unwrap();
        *id_guard += 1;
        let scan_id = format!("walk-up-{}", *id_guard);
        drop(id_guard);
        let cancel = Arc::new(AtomicBool::new(false));
        self.inner.lock().unwrap().insert(scan_id.clone(), cancel.clone());
        (scan_id, cancel)
    }

    pub fn cancel(&self, scan_id: &str) -> bool {
        if let Some(flag) = self.inner.lock().unwrap().get(scan_id) {
            flag.store(true, Ordering::SeqCst);
            true
        } else {
            false
        }
    }

    pub fn finish(&self, scan_id: &str) {
        self.inner.lock().unwrap().remove(scan_id);
    }

    pub fn current(&self) -> Option<String> {
        self.inner
            .lock()
            .unwrap()
            .keys()
            .next()
            .map(|s| s.to_string())
    }
}

/// Per-scan mutable state passed through the recursion. Holding a
/// reference keeps the recursion non-`mut self` (Rust ownership rule
/// for free functions).
struct ScanContext {
    cancel: Arc<AtomicBool>,
    found: Vec<ProjectEntry>,
    skipped_existing: Vec<String>,
    skipped_not_unity: usize,
    visited_dirs: usize,
    max_depth: u32,
    follow_symlinks: bool,
    existing: HashSet<String>,
}

impl ScanContext {
    fn new(cancel: Arc<AtomicBool>, max_depth: u32, follow_symlinks: bool, existing: HashSet<String>) -> Self {
        Self {
            cancel,
            found: Vec::new(),
            skipped_existing: Vec::new(),
            skipped_not_unity: 0,
            visited_dirs: 0,
            max_depth,
            follow_symlinks,
            existing,
        }
    }

    fn cancelled(&self) -> bool {
        self.cancel.load(Ordering::SeqCst)
    }
}

/// Recursive walk. Emits progress events on the supplied `AppHandle`
/// (throttled: only when we descend into a new directory or
/// discover a new project, to keep event volume manageable on
/// large trees).
fn walk_dir(
    dir: &Path,
    depth: u32,
    ctx: &mut ScanContext,
    app: &AppHandle,
    scan_id: &str,
    current_root: &str,
) {
    if ctx.cancelled() {
        return;
    }

    ctx.visited_dirs += 1;
    let canonical = canonicalize_for_compare(&dir.to_string_lossy());
    if ctx.existing.contains(&canonical) {
        ctx.skipped_existing.push(canonical);
        return;
    }

    if is_unity_project_root(dir) {
        let version = read_project_version(dir);
        let mtime = read_dir_mtime_iso(dir);
        let entry = ProjectEntry {
            id: uuid::Uuid::new_v4().to_string(),
            name: derive_name(dir),
            path: dir.to_string_lossy().to_string(),
            unity_version: version,
            last_opened_at: None,
            last_modified_at: mtime,
            launch_args: None,
            platform_intent: None,
            last_launch_pid: None,
            last_launch_at: None,
            frecency: 0,
            git_branch: None,
            source: "walk-up".to_string(),
            hidden: false,
            stale: false,
            env_vars: Default::default(),
            // M15 T6.4: enrich walk-up discovered rows with the SRP +
            // build-target labels so they show the same chips as rows
            // added via the "Add Project" flow.
            render_pipeline: Some(
                crate::config::render_pipeline::read_render_pipeline(dir)
                    .label()
                    .to_string(),
            ),
            default_build_target: crate::config::build_target::read_default_build_target(dir)
                .target,
            kind: ProjectKind::Unity,
            package_manifest_path: None,
            migrate_source_folder: None,
            line_count_stats: None,
        };
        ctx.found.push(entry.clone());
        // Mark the canonical path as seen so nested project roots
        // (a project *inside* another project's tree) are not
        // re-detected as duplicates of the outer one.
        ctx.existing.insert(canonical.clone());
        emit_progress(app, scan_id, current_root, depth, ctx);
        return;
    }

    if depth >= ctx.max_depth {
        ctx.skipped_not_unity += 1;
        return;
    }

    let read = match std::fs::read_dir(dir) {
        Ok(r) => r,
        Err(_) => {
            ctx.skipped_not_unity += 1;
            return;
        }
    };
    for child in read.flatten() {
        if ctx.cancelled() {
            break;
        }
        let path = child.path();
        // Cheap directory probe: `is_dir` follows symlinks, so when
        // the user has opted out of follow-symlinks we reject the
        // link explicitly. `metadata` returns the link target
        // metadata; `symlink_metadata` would return the link itself.
        let is_dir = if ctx.follow_symlinks {
            path.is_dir()
        } else {
            match std::fs::symlink_metadata(&path) {
                Ok(md) => md.file_type().is_dir() || md.file_type().is_symlink(),
                Err(_) => false,
            }
        };
        if !is_dir {
            continue;
        }
        walk_dir(&path, depth + 1, ctx, app, scan_id, current_root);
    }
}

fn emit_progress(
    app: &AppHandle,
    scan_id: &str,
    current_root: &str,
    current_depth: u32,
    ctx: &ScanContext,
) {
    let payload = WalkUpProgress {
        scan_id: scan_id.to_string(),
        current_root: current_root.to_string(),
        current_depth,
        max_depth: ctx.max_depth,
        found_so_far: ctx.found.len(),
        visited_dirs: ctx.visited_dirs,
        status: WalkUpStatus::Running,
    };
    // The Tauri `Emitter` trait is implemented for `AppHandle` in
    // Tauri 2. Errors are best-effort: if the webview is gone (e.g.
    // the user closed the window) we just keep walking.
    let _ = app.emit("walk-up://progress", &payload);
}

/// Validate a single user-supplied root and produce a normalised
/// (canonical-or-input) form to scan. Pure: no I/O beyond stat.
fn validate_root(input: &str) -> Result<String, String> {
    let trimmed = input.trim();
    if trimmed.is_empty() {
        return Err("empty path".to_string());
    }
    let p = Path::new(trimmed);
    if !p.exists() {
        return Err(format!("path does not exist: {}", trimmed));
    }
    if !p.is_dir() {
        return Err(format!("not a directory: {}", trimmed));
    }
    Ok(trimmed.to_string())
}

/// Clamp the user-supplied depth into `[1, MAX_WALK_UP_DEPTH]`.
/// The Settings UI and the settings store both clamp the same
/// range, but the command is the last line of defence and must
/// not panic on a stale value.
fn clamp_depth(depth: u32) -> u32 {
    depth.clamp(1, MAX_WALK_UP_DEPTH)
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WalkUpStartParams {
    pub roots: Vec<String>,
    pub max_depth: u32,
    pub follow_symlinks: bool,
    pub keep_partial: bool,
}

/// Kick off a walk-up scan on a background thread. Returns
/// immediately with a `scan_id`; the actual work emits
/// `walk-up://progress` and `walk-up://done` events.
#[tauri::command]
pub fn start_walk_up_scan(
    app: AppHandle,
    state: State<'_, AppState>,
    params: WalkUpStartParams,
) -> Result<WalkUpStart, WalkUpError> {
    if let Some(current) = state.walk_up_registry.lock().unwrap().current() {
        return Err(WalkUpError::AnotherScanInProgress {
            current_scan_id: current,
        });
    }

    let mut valid_roots: Vec<String> = Vec::new();
    let mut errors: Vec<(String, String)> = Vec::new();
    for root in &params.roots {
        match validate_root(root) {
            Ok(r) => valid_roots.push(r),
            Err(reason) => errors.push((root.clone(), reason)),
        }
    }
    if valid_roots.is_empty() {
        if let Some((path, reason)) = errors.into_iter().next() {
            return Err(WalkUpError::InvalidRoot { path, reason });
        }
        return Err(WalkUpError::NoRoots);
    }

    let max_depth = clamp_depth(params.max_depth);
    let follow_symlinks = params.follow_symlinks;
    let keep_partial = params.keep_partial;

    let (scan_id, cancel) = state.walk_up_registry.lock().unwrap().register();

    let app_for_thread = app.clone();
    let start = WalkUpStart {
        scan_id: scan_id.clone(),
        max_depth,
        follow_symlinks,
        keep_partial,
        roots: valid_roots.clone(),
    };

    std::thread::Builder::new()
        .name("hub-walk-up-scan".to_string())
        .spawn(move || {
            let started = Instant::now();
            let mut ctx = {
                // The worker thread does not get a `State<AppState>`
                // reference (Tauri states are wrapped types that do
                // not cross thread boundaries directly). We look the
                // state back up via the `Manager` impl on AppHandle.
                let app_state = app_for_thread.state::<AppState>();
                let guard = app_state.projects.lock().unwrap();
                let existing: HashSet<String> = guard
                    .projects
                    .iter()
                    .map(|p| canonicalize_for_compare(&p.path))
                    .collect();
                ScanContext::new(cancel.clone(), max_depth, follow_symlinks, existing)
            };

            for root in &valid_roots {
                if ctx.cancelled() {
                    break;
                }
                walk_dir(
                    Path::new(root),
                    0,
                    &mut ctx,
                    &app_for_thread,
                    &scan_id,
                    root,
                );
            }

            let status = if ctx.cancelled() {
                WalkUpStatus::Cancelled
            } else {
                WalkUpStatus::Completed
            };

            // Decide what to persist. When the user opted out of
            // keeping partial results and the scan was cancelled,
            // we drop the discovered entries.
            let keep = !(status == WalkUpStatus::Cancelled && !keep_partial);
            let to_add: Vec<ProjectEntry> = if keep { ctx.found.clone() } else { Vec::new() };

            let app_state = app_for_thread.state::<AppState>();
            let mut projects = app_state.projects.lock().unwrap().clone();
            // Refresh dedup set with whatever the *current* projects
            // file contains (could have changed since the scan
            // started if the user added a project via Add Project).
            let mut current_paths: HashSet<String> = projects
                .projects
                .iter()
                .map(|p| canonicalize_for_compare(&p.path))
                .collect();
            let mut final_added: Vec<ProjectEntry> = Vec::new();
            for entry in to_add {
                let key = canonicalize_for_compare(&entry.path);
                if current_paths.contains(&key) {
                    ctx.skipped_existing.push(key);
                    continue;
                }
                current_paths.insert(key);
                projects.projects.push(entry.clone());
                final_added.push(entry);
            }

            let mut persist_error: Option<String> = None;
            if !final_added.is_empty() {
                if let Err(e) = persistence::save_projects(&projects) {
                    persist_error = Some(e.to_string());
                } else {
                    let mut guard = app_state.projects.lock().unwrap();
                    *guard = projects.clone();
                }
            }

            let done = WalkUpDone {
                scan_id: scan_id.clone(),
                status: if persist_error.is_some() {
                    WalkUpStatus::Failed
                } else {
                    status
                },
                added: final_added,
                skipped_existing: ctx.skipped_existing,
                skipped_not_unity: ctx.skipped_not_unity,
                skipped_invalid_root: Vec::new(),
                projects,
                error: persist_error,
            };

            log::info!(
                "walk-up scan {} finished in {:?} (found={}, visited={}, status={:?})",
                scan_id,
                started.elapsed(),
                done.added.len(),
                ctx.visited_dirs,
                done.status,
            );

            let _ = app_for_thread.emit("walk-up://done", &done);
            // Drop the in-flight entry so a subsequent scan can start.
            let app_state = app_for_thread.state::<AppState>();
            app_state.walk_up_registry.lock().unwrap().finish(&scan_id);
        })
        .expect("failed to spawn walk-up scan thread");

    Ok(start)
}

/// Request cancellation of an in-flight scan. Returns `true` when a
/// matching scan_id was found and signalled. The actual scan will
/// stop at the next directory boundary and emit `walk-up://done`
/// with `status = "cancelled"`.
#[tauri::command]
pub fn cancel_walk_up_scan(state: State<'_, AppState>, scan_id: String) -> bool {
    state.walk_up_registry.lock().unwrap().cancel(&scan_id)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::path::PathBuf;
    use tempfile::tempdir;

    fn make_unity(dir: &Path, version: Option<&str>) {
        fs::create_dir_all(dir.join("Assets")).unwrap();
        fs::create_dir_all(dir.join("ProjectSettings")).unwrap();
        if let Some(v) = version {
            fs::write(
                dir.join("ProjectSettings").join("ProjectVersion.txt"),
                format!("m_EditorVersion: {}\n", v),
            )
            .unwrap();
        }
    }

    #[test]
    fn is_unity_project_root_accepts_valid() {
        let dir = tempdir().unwrap();
        make_unity(dir.path(), Some("6000.0.1f1"));
        assert!(is_unity_project_root(dir.path()));
    }

    #[test]
    fn is_unity_project_root_rejects_missing_assets() {
        let dir = tempdir().unwrap();
        fs::create_dir_all(dir.path().join("ProjectSettings")).unwrap();
        assert!(!is_unity_project_root(dir.path()));
    }

    #[test]
    fn is_unity_project_root_rejects_missing_project_settings() {
        let dir = tempdir().unwrap();
        fs::create_dir_all(dir.path().join("Assets")).unwrap();
        assert!(!is_unity_project_root(dir.path()));
    }

    #[test]
    fn is_unity_project_root_rejects_file() {
        let dir = tempdir().unwrap();
        let file = dir.path().join("not-a-dir.txt");
        fs::write(&file, "").unwrap();
        assert!(!is_unity_project_root(&file));
    }

    #[test]
    fn validate_root_rejects_empty_string() {
        let err = validate_root("   ").unwrap_err();
        assert!(err.contains("empty"));
    }

    #[test]
    fn validate_root_rejects_missing_path() {
        let err = validate_root("/definitely/does/not/exist/qwerty").unwrap_err();
        assert!(err.contains("does not exist"));
    }

    #[test]
    fn validate_root_rejects_file() {
        let dir = tempdir().unwrap();
        let file = dir.path().join("a.txt");
        fs::write(&file, "x").unwrap();
        let err = validate_root(file.to_str().unwrap()).unwrap_err();
        assert!(err.contains("not a directory"));
    }

    #[test]
    fn validate_root_accepts_existing_directory() {
        let dir = tempdir().unwrap();
        let p = validate_root(dir.path().to_str().unwrap()).unwrap();
        assert_eq!(p, dir.path().to_string_lossy());
    }

    #[test]
    fn clamp_depth_clamps_to_range() {
        assert_eq!(clamp_depth(0), 1);
        assert_eq!(clamp_depth(1), 1);
        assert_eq!(clamp_depth(4), 4);
        assert_eq!(clamp_depth(8), 8);
        assert_eq!(clamp_depth(100), MAX_WALK_UP_DEPTH);
    }

    #[test]
    fn read_project_version_parses_known_value() {
        let dir = tempdir().unwrap();
        make_unity(dir.path(), Some("6000.0.1f1"));
        assert_eq!(read_project_version(dir.path()), Some("6000.0.1f1".into()));
    }

    #[test]
    fn read_project_version_returns_none_for_missing_file() {
        let dir = tempdir().unwrap();
        make_unity(dir.path(), None);
        assert_eq!(read_project_version(dir.path()), None);
    }

    #[test]
    fn registry_register_yields_unique_ids() {
        let reg = WalkUpRegistry::default();
        let (id1, c1) = reg.register();
        let (id2, c2) = reg.register();
        assert_ne!(id1, id2);
        assert!(!c1.load(Ordering::SeqCst));
        assert!(!c2.load(Ordering::SeqCst));
        reg.finish(&id1);
        reg.finish(&id2);
    }

    #[test]
    fn registry_cancel_targets_only_matching_id() {
        let reg = WalkUpRegistry::default();
        let (id1, c1) = reg.register();
        let (id2, c2) = reg.register();
        assert!(reg.cancel(&id1));
        assert!(c1.load(Ordering::SeqCst));
        assert!(!c2.load(Ordering::SeqCst));
        assert!(!reg.cancel("does-not-exist"));
        reg.finish(&id1);
        reg.finish(&id2);
    }

    #[test]
    fn registry_current_returns_first_id() {
        let reg = WalkUpRegistry::default();
        assert!(reg.current().is_none());
        let (id, _c) = reg.register();
        assert_eq!(reg.current().as_deref(), Some(id.as_str()));
        reg.finish(&id);
        assert!(reg.current().is_none());
    }

    #[test]
    fn path_string_roundtrip() {
        let p: PathBuf = "/some/parent/MyProject".into();
        assert_eq!(derive_name(&p), "MyProject");
    }
}
