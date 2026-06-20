use std::collections::HashMap;
use std::fs;
use std::path::Path;

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

fn compute_size(dir: &Path, patterns: &[String]) -> u64 {
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
            if path.is_dir() {
                total += compute_size(&path, patterns);
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
        let size = compute_size(p, &patterns);
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
}
