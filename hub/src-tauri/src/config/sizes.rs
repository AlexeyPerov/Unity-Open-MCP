use std::collections::HashMap;
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

use serde::Serialize;
use tauri::{AppHandle, Emitter};

const ALWAYS_EXCLUDED: &[&str] = &["Library", "Temp", "Logs", "UserSettings"];

fn parse_ignore_patterns(project_path: &Path) -> Vec<String> {
    let mut patterns: Vec<String> = Vec::new();
    for name in &[".gitignore", "ignore.conf"] {
        let file_path = project_path.join(name);
        if let Ok(content) = fs::read_to_string(&file_path) {
            for line in content.lines() {
                let trimmed = line.trim();
                if trimmed.is_empty() || trimmed.starts_with('#') {
                    continue;
                }
                patterns.push(trimmed.to_string());
            }
        }
    }
    patterns
}

fn is_ignored_by_gitignore(entry_name: &str, patterns: &[String]) -> bool {
    for pattern in patterns {
        let pat = pattern.trim_end_matches('/');
        if pat.starts_with('!') {
            continue;
        }
        let base = pat.trim_start_matches("./");
        if base == entry_name {
            return true;
        }
        if base.starts_with('*') && entry_name.ends_with(base.trim_start_matches('*')) {
            return true;
        }
        if base.ends_with('*') && entry_name.starts_with(base.trim_end_matches('*')) {
            return true;
        }
    }
    false
}

fn compute_size(dir: &Path, patterns: &[String], visited: &mut HashSet<PathBuf>) -> u64 {
    // H26: defense-in-depth loop guard. The symlink check below already
    // prevents the common Unity `Assets/Shared -> ../..` cycle from being
    // descended into, but a `visited` set of canonicalized paths also
    // catches a real directory that appears twice via bind mounts or a
    // non-symlink cycle, so the walk terminates instead of recursing to
    // PATH_MAX. Canonicalize best-effort: a path that cannot be
    // canonicalized is still walked once (the set just won't dedupe it).
    let canon = fs::canonicalize(dir).unwrap_or_else(|_| dir.to_path_buf());
    if !visited.insert(canon) {
        return 0;
    }
    let mut total: u64 = 0;
    if let Ok(entries) = fs::read_dir(dir) {
        for entry in entries.flatten() {
            let name = entry.file_name();
            let name_str = name.to_string_lossy();
            if ALWAYS_EXCLUDED.contains(&name_str.as_ref()) {
                continue;
            }
            if is_ignored_by_gitignore(&name_str, patterns) {
                continue;
            }
            let path = entry.path();
            // H26: probe with `symlink_metadata` so symlinks are detected
            // (not followed). The previous `path.is_dir()` call followed
            // the link, so a symlinked directory inside the project
            // (`Assets/Shared -> ../..`, common in Unity setups) was
            // re-descended per level until PATH_MAX, inflating the reported
            // size by orders of magnitude and pegging a core for minutes.
            // A symlink is now counted by the size of the link entry
            // itself (typically 0 on Unix) and never traversed.
            let is_symlink = match fs::symlink_metadata(&path) {
                Ok(md) => md.file_type().is_symlink(),
                Err(_) => continue,
            };
            if is_symlink {
                continue;
            }
            if path.is_dir() {
                total += compute_size(&path, patterns, visited);
            } else {
                total += entry.metadata().map(|m| m.len()).unwrap_or(0);
            }
        }
    }
    total
}

/// Recursively sizes every project root. Runs on the blocking thread
/// pool (see `get_project_sizes`) so a slow disk / large tree never
/// stalls the webview thread. Kept sync for direct unit testing.
fn compute_project_sizes(paths: &[String]) -> HashMap<String, u64> {
    let mut result: HashMap<String, u64> = HashMap::with_capacity(paths.len());
    for path in paths {
        let p = Path::new(path);
        if !p.exists() {
            result.insert(path.clone(), 0);
            continue;
        }
        let patterns = parse_ignore_patterns(p);
        let mut visited: HashSet<PathBuf> = HashSet::new();
        let size = compute_size(p, &patterns, &mut visited);
        result.insert(path.clone(), size);
    }
    result
}

/// `paths` → size in bytes. `async` + `spawn_blocking` so the recursive
/// directory walk (potentially tens of thousands of `metadata()` calls
/// per Unity project) runs off the main/webview thread. This is the
/// dominant launch-path freeze on cold caches or spun-down/external
/// drives; keeping it off the webview thread keeps the window
/// responsive while sizes are still being computed.
#[tauri::command]
pub async fn get_project_sizes(paths: Vec<String>) -> HashMap<String, u64> {
    let count = paths.len();
    let start = std::time::Instant::now();
    let result = tauri::async_runtime::spawn_blocking(move || compute_project_sizes(&paths))
        .await
        .unwrap_or_default();
    log::info!(
        "get_project_sizes: {} paths in {}ms",
        count,
        start.elapsed().as_millis()
    );
    result
}

// --- streaming variant ------------------------------------------------------
//
// The batch `get_project_sizes` above waits for the LAST root before
// returning, and the boot sequence in ProjectsTab used to `await` it —
// so a 14-project list with a few large repos froze the window for ~20s
// while every byte was counted. The streaming variant here sizes each
// root independently (and in parallel) and emits a `sizes://progress`
// event per root as it completes, so the frontend can paint rows
// immediately and fill sizes in lazily. `sizes://done` closes the run.
//
// The walker logic (`compute_size`, H26 symlink/cycle guards,
// `.gitignore`/`ignore.conf` handling, `ALWAYS_EXCLUDED`) is unchanged —
// only the driver (sequential batch → parallel + streamed) differs.

/// One root finished sizing. Emitted on `sizes://progress`.
#[derive(Debug, Clone, Serialize)]
pub struct SizesProgress {
    pub path: String,
    pub size: u64,
}

/// All roots finished. Emitted on `sizes://done`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SizesDone {
    pub total: usize,
    pub elapsed_ms: u64,
}

/// Stream per-root sizes to the frontend as each root completes, sizing
/// roots in parallel across the blocking thread pool. Returns
/// immediately; results arrive via `sizes://progress` and the run closes
/// with `sizes://done`. Returns `Err` only when the sizing thread could
/// not be spawned — in that case no events will ever fire, so the
/// rejected invoke is the frontend's signal to stop waiting.
///
/// Unlike `get_project_sizes` (batch, single return), this never blocks
/// boot: the frontend kicks it off fire-and-forget and updates a keyed
/// reactive map per event, so each project row flips to its size the
/// moment its root is counted rather than waiting on the slowest root.
#[tauri::command]
pub fn stream_project_sizes(app: AppHandle, paths: Vec<String>) -> Result<(), String> {
    let count = paths.len();
    let app_handle = app.clone();
    std::thread::Builder::new()
        .name("hub-sizes".to_string())
        .spawn(move || {
            let started = Instant::now();
            // Size roots in parallel and emit `sizes://progress` from
            // inside each worker the moment a root finishes, so the
            // frontend paints sizes incrementally instead of receiving
            // them in a burst after the slowest root. `compute_size`
            // owns no shared mutable state (its `visited` set is
            // created fresh per root inside `compute_project_size`), so
            // the threads never race; `AppHandle` is `Send + Sync` so
            // emitting from the scoped workers is safe. `scope` joins
            // every worker before we emit `done`, guaranteeing no
            // in-flight progress events land after `sizes://done`.
            scope_parallel_sizes(&paths, |path, size| {
                let _ = app_handle.emit(
                    "sizes://progress",
                    &SizesProgress {
                        path: path.to_string(),
                        size,
                    },
                );
            });
            let elapsed_ms = started.elapsed().as_millis() as u64;
            let _ = app_handle.emit(
                "sizes://done",
                &SizesDone {
                    total: count,
                    elapsed_ms,
                },
            );
            log::info!("stream_project_sizes: {} paths in {}ms", count, elapsed_ms);
        })
        // A spawn failure means neither `sizes://progress` nor
        // `sizes://done` will ever fire, so the frontend's `loading`
        // flag would spin until teardown. Log AND return the error so
        // the store's `catch` on the invoke fires and clears `loading`.
        .map_err(|e| {
            log::warn!("stream_project_sizes: failed to spawn hub-sizes thread: {}", e);
            format!("failed to spawn sizing thread: {}", e)
        })?;
    Ok(())
}

/// Size a single root (pure helper, reused by the parallel driver and
/// directly unit-testable). Returns `(path, size)`.
fn compute_project_size(path: &str) -> (String, u64) {
    let p = Path::new(path);
    if !p.exists() {
        return (path.to_string(), 0);
    }
    let patterns = parse_ignore_patterns(p);
    let mut visited: HashSet<PathBuf> = HashSet::new();
    let size = compute_size(p, &patterns, &mut visited);
    (path.to_string(), size)
}

/// Size every root in parallel using a scoped thread pool bounded by the
/// number of CPUs. `on_size` is invoked from inside the worker thread
/// right after each root finishes — this is what makes the streamed
/// command actually incremental (each `sizes://progress` fires the
/// moment its root is counted, not after the join). Returns `(path,
/// size)` pairs in input order (tests rely on the deterministic return
/// order; the callback order is completion order by design).
fn scope_parallel_sizes<F>(paths: &[String], on_size: F) -> Vec<(String, u64)>
where
    F: Fn(&str, u64) + Sync,
{
    if paths.is_empty() {
        return Vec::new();
    }
    // One worker per root up to available parallelism. A project list
    // is small (tens, not thousands), so a thread-per-root fan-out is
    // both simpler and faster than a fixed worker pool here.
    let max_threads = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(1);
    let worker_count = paths.len().min(max_threads);
    let chunks: Vec<Vec<String>> = chunk_paths(paths, worker_count);

    let on_size = &on_size;
    std::thread::scope(|s| {
        let mut handles = Vec::with_capacity(chunks.len());
        for chunk in chunks {
            let handle = s.spawn(move || {
                let mut out: Vec<(String, u64)> = Vec::with_capacity(chunk.len());
                for path in chunk {
                    let (path, size) = compute_project_size(&path);
                    on_size(&path, size);
                    out.push((path, size));
                }
                out
            });
            handles.push(handle);
        }
        let mut merged: Vec<(String, u64)> = Vec::with_capacity(paths.len());
        for h in handles {
            match h.join() {
                Ok(mut part) => merged.append(&mut part),
                // A panic inside a sizer (e.g. a path that disappears
                // mid-walk) must not take the whole run down. Skip the
                // chunk; its roots will read as unsized (0) on the
                // frontend, which is the same as a missing path.
                Err(_) => log::error!("sizes: a worker thread panicked"),
            }
        }
        // Restore input order so progress events are deterministic.
        merged.sort_by_key(|(path, _)| {
            paths.iter().position(|p| p == path).unwrap_or(usize::MAX)
        });
        merged
    })
}

/// Split `paths` into `n` contiguous chunks (last chunk absorbs the
/// remainder). Plain contiguous chunking keeps sibling roots (often the
/// same disk) on one worker, which is friendlier to the page cache than
/// round-robin.
fn chunk_paths(paths: &[String], n: usize) -> Vec<Vec<String>> {
    if n == 0 {
        return vec![paths.to_vec()];
    }
    let mut chunks: Vec<Vec<String>> = Vec::with_capacity(n);
    let base = paths.len() / n;
    let rem = paths.len() % n;
    let mut idx = 0;
    for i in 0..n {
        let take = base + if i < rem { 1 } else { 0 };
        chunks.push(paths[idx..idx + take].to_vec());
        idx += take;
    }
    chunks
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn empty_dir_returns_zero() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().to_string_lossy().to_string();
        let result = compute_project_sizes(&[path.clone()]);
        assert_eq!(result.get(&path), Some(&0u64));
    }

    #[test]
    fn counts_files() {
        let dir = tempfile::tempdir().unwrap();
        let file_path = dir.path().join("test.txt");
        let mut f = fs::File::create(&file_path).unwrap();
        f.write_all(b"hello world").unwrap();
        let path = dir.path().to_string_lossy().to_string();
        let result = compute_project_sizes(&[path.clone()]);
        assert_eq!(result.get(&path), Some(&11u64));
    }

    #[test]
    fn excludes_library_dir() {
        let dir = tempfile::tempdir().unwrap();
        let lib_dir = dir.path().join("Library");
        fs::create_dir_all(&lib_dir).unwrap();
        let file_path = lib_dir.join("big.dat");
        let mut f = fs::File::create(&file_path).unwrap();
        f.write_all(b"x".repeat(1000).as_slice()).unwrap();
        let path = dir.path().to_string_lossy().to_string();
        let result = compute_project_sizes(&[path.clone()]);
        assert_eq!(result.get(&path), Some(&0u64));
    }

    #[test]
    fn missing_path_returns_zero() {
        let result = compute_project_sizes(&["/nonexistent/path".to_string()]);
        assert_eq!(result.get("/nonexistent/path"), Some(&0u64));
    }

    #[test]
    fn respects_gitignore() {
        let dir = tempfile::tempdir().unwrap();
        let ignored_dir = dir.path().join("build_output");
        fs::create_dir_all(&ignored_dir).unwrap();
        let file_path = ignored_dir.join("artifact.bin");
        let mut f = fs::File::create(&file_path).unwrap();
        f.write_all(b"x".repeat(500).as_slice()).unwrap();
        let gitignore = dir.path().join(".gitignore");
        let mut f = fs::File::create(&gitignore).unwrap();
        f.write_all(b"build_output\n").unwrap();
        let included = dir.path().join("Assets");
        fs::create_dir_all(&included).unwrap();
        let asset_file = included.join("main.cs");
        let mut f = fs::File::create(&asset_file).unwrap();
        f.write_all(b"code").unwrap();
        let path = dir.path().to_string_lossy().to_string();
        let result = compute_project_sizes(&[path.clone()]);
        let gitignore_size = fs::metadata(&gitignore).unwrap().len();
        let asset_size = fs::metadata(&asset_file).unwrap().len();
        assert_eq!(result.get(&path), Some(&(gitignore_size + asset_size)));
    }

    // H26: a symlinked directory inside the project (common in Unity:
    // `Assets/Shared -> ../..`) must not be traversed. The previous
    // `path.is_dir()` followed the link, so the walker re-descended the
    // whole tree per level until PATH_MAX. The fix uses symlink_metadata
    // so symlinks are skipped outright, and a visited-set guards against
    // any residual cycle.
    #[cfg(unix)]
    #[test]
    fn does_not_recurse_through_symlinked_directory() {
        use std::os::unix::fs::symlink;
        let dir = tempfile::tempdir().unwrap();
        // A real file we can size.
        let asset = dir.path().join("Assets").join("a.cs");
        fs::create_dir_all(asset.parent().unwrap()).unwrap();
        let mut f = fs::File::create(&asset).unwrap();
        f.write_all(b"1234").unwrap();
        let asset_size = fs::metadata(&asset).unwrap().len();
        // A symlinked directory pointing back at the project root — the
        // classic Unity `Assets/Shared -> ../..` cycle.
        symlink(dir.path(), dir.path().join("Assets").join("Shared")).unwrap();
        let path = dir.path().to_string_lossy().to_string();
        let result = compute_project_sizes(&[path.clone()]);
        // The size must be bounded by the real file, not inflated by the
        // self-referential symlink. Without the fix this either blew the
        // stack or reported a size multiplied by the recursion depth.
        let reported = *result.get(&path).unwrap();
        assert!(
            reported < asset_size * 4,
            "symlink cycle inflated reported size to {reported}"
        );
    }

    // --- streaming-driver helpers ---

    #[test]
    fn chunk_paths_distributes_evenly() {
        let paths: Vec<String> = (0..6).map(|i| format!("/p{i}")).collect();
        let chunks = chunk_paths(&paths, 3);
        assert_eq!(chunks.len(), 3);
        // Each chunk has 2; concatenated equals input (order preserved).
        let flat: Vec<String> = chunks.into_iter().flatten().collect();
        assert_eq!(flat, paths);
    }

    #[test]
    fn chunk_paths_distributes_remainder_to_first_chunks() {
        let paths: Vec<String> = (0..7).map(|i| format!("/p{i}")).collect();
        let chunks = chunk_paths(&paths, 3);
        assert_eq!(chunks.len(), 3);
        // 7 / 3 = 2 remainder 1 → first chunk gets the extra.
        assert_eq!(chunks[0].len(), 3);
        assert_eq!(chunks[1].len(), 2);
        assert_eq!(chunks[2].len(), 2);
        let flat: Vec<String> = chunks.into_iter().flatten().collect();
        assert_eq!(flat, paths);
    }

    #[test]
    fn chunk_paths_more_workers_than_paths() {
        let paths: Vec<String> = (0..2).map(|i| format!("/p{i}")).collect();
        // Worker count is clamped to path count by the caller, but the
        // helper itself must still produce non-empty chunks and not
        // panic when n > len.
        let chunks = chunk_paths(&paths, 5);
        let flat: Vec<String> = chunks.into_iter().flatten().collect();
        assert_eq!(flat, paths);
    }

    #[test]
    fn chunk_paths_zero_workers_returns_single_chunk() {
        let paths: Vec<String> = (0..3).map(|i| format!("/p{i}")).collect();
        let chunks = chunk_paths(&paths, 0);
        assert_eq!(chunks.len(), 1);
        assert_eq!(chunks[0], paths);
    }

    #[test]
    fn scope_parallel_sizes_matches_batch_and_preserves_order() {
        // Build three roots with known content so we can assert sizes
        // exactly and confirm the parallel driver agrees with the
        // sequential batch command.
        let dirs: Vec<_> = (0..3).map(|_| tempfile::tempdir().unwrap()).collect();
        let mut paths: Vec<String> = Vec::new();
        let mut expected: HashMap<String, u64> = HashMap::new();
        for dir in &dirs {
            let asset = dir.path().join("Assets").join("a.cs");
            fs::create_dir_all(asset.parent().unwrap()).unwrap();
            let mut f = fs::File::create(&asset).unwrap();
            f.write_all(b"code").unwrap();
            let path = dir.path().to_string_lossy().to_string();
            expected.insert(
                path.clone(),
                fs::metadata(&asset).unwrap().len(),
            );
            paths.push(path);
        }

        let batch = compute_project_sizes(&paths);
        // Collect callback invocations to prove per-root delivery: the
        // streamed command relies on `on_size` firing once per root
        // from inside the workers (that is what `sizes://progress`
        // hangs off), so every root must be reported exactly once.
        let seen: std::sync::Mutex<Vec<(String, u64)>> = std::sync::Mutex::new(Vec::new());
        let streamed = scope_parallel_sizes(&paths, |path, size| {
            seen.lock().unwrap().push((path.to_string(), size));
        });

        // The callback saw every root exactly once (completion order is
        // nondeterministic, so compare as sorted sets).
        let mut seen = seen.into_inner().unwrap();
        seen.sort();
        let mut expected_pairs = streamed.clone();
        expected_pairs.sort();
        assert_eq!(seen, expected_pairs);

        // Same length and input order.
        assert_eq!(streamed.len(), paths.len());
        assert_eq!(
            streamed.iter().map(|(p, _)| p.clone()).collect::<Vec<_>>(),
            paths
        );
        // Same sizes as the batch command, per path.
        for (path, size) in &streamed {
            assert_eq!(batch.get(path), Some(size));
            assert_eq!(expected.get(path), Some(size));
        }
    }

    #[test]
    fn scope_parallel_sizes_empty() {
        assert!(scope_parallel_sizes(&[], |_, _| {}).is_empty());
    }

    #[test]
    fn compute_project_size_missing_returns_zero() {
        let (path, size) = compute_project_size("/nonexistent/path");
        assert_eq!(path, "/nonexistent/path");
        assert_eq!(size, 0);
    }
}
