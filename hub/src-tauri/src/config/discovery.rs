use std::collections::HashMap;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::schemas::Settings;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UnityInstallation {
    pub version: String,
    pub path: String,
    pub source: String,
    pub install_date: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscoveryError {
    pub parent_path: String,
    pub message: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscoveryResult {
    pub installations: Vec<UnityInstallation>,
    pub errors: Vec<DiscoveryError>,
}

fn get_os_default_hub_paths() -> Vec<PathBuf> {
    if cfg!(target_os = "macos") {
        vec![PathBuf::from("/Applications/Unity/Hub/Editor")]
    } else if cfg!(target_os = "windows") {
        vec![PathBuf::from("C:\\Program Files\\Unity\\Hub\\Editor")]
    } else {
        dirs::home_dir()
            .map(|h| h.join("Unity/Hub/Editor"))
            .into_iter()
            .collect()
    }
}

fn get_env_hub_path() -> Option<PathBuf> {
    env::var("UNITY_HUB").ok().map(PathBuf::from)
}

fn is_unity_editor_dir(dir: &Path) -> bool {
    if cfg!(target_os = "macos") {
        dir.join("Unity.app").is_dir()
    } else if cfg!(target_os = "windows") {
        dir.join("Editor").join("Unity.exe").is_file()
    } else {
        dir.join("Editor").join("Unity").is_file()
    }
}

fn get_install_date(dir: &Path) -> Option<String> {
    let meta = fs::metadata(dir).ok()?;
    let time = meta.modified().ok().or_else(|| meta.created().ok())?;
    let duration = time.duration_since(std::time::SystemTime::UNIX_EPOCH).ok()?;
    let secs = duration.as_secs() as i64;
    chrono::DateTime::from_timestamp(secs, 0)
        .map(|dt| dt.format("%Y-%m-%d").to_string())
}

fn scan_parent_folder(parent: &Path) -> (Vec<UnityInstallation>, Vec<DiscoveryError>) {
    let mut installations = Vec::new();
    let mut errors = Vec::new();

    let entries = match fs::read_dir(parent) {
        Ok(e) => e,
        Err(e) => {
            errors.push(DiscoveryError {
                parent_path: parent.display().to_string(),
                message: format!("Cannot read directory: {}", e),
            });
            return (installations, errors);
        }
    };

    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_dir() {
            continue;
        }

        let version = match path.file_name().and_then(|n| n.to_str()) {
            Some(v) => v.to_string(),
            None => continue,
        };

        if !is_unity_editor_dir(&path) {
            continue;
        }

        installations.push(UnityInstallation {
            version,
            path: path.to_string_lossy().to_string(),
            source: String::new(),
            install_date: get_install_date(&path),
        });
    }

    (installations, errors)
}

fn determine_source(install_path: &Path, os_defaults: &[PathBuf], env_path: &Option<PathBuf>) -> String {
    if let Some(ref env) = env_path {
        if install_path.starts_with(env) {
            return "Env".to_string();
        }
    }
    for default in os_defaults {
        if install_path.starts_with(default) {
            return "Hub".to_string();
        }
    }
    "Manual".to_string()
}

pub fn discover_unity_installations(settings: &Settings) -> DiscoveryResult {
    let os_defaults = get_os_default_hub_paths();
    let env_path = get_env_hub_path();

    let mut seen: HashMap<String, UnityInstallation> = HashMap::new();
    let mut all_errors: Vec<DiscoveryError> = Vec::new();
    let mut scanned: Vec<PathBuf> = Vec::new();

    for folder in &settings.unity_discovery.parent_folders {
        let parent = PathBuf::from(folder);
        if !parent.exists() {
            continue;
        }
        scanned.push(parent.clone());
        let (installs, errs) = scan_parent_folder(&parent);
        all_errors.extend(errs);
        for mut install in installs {
            let key = install.path.clone();
            if !seen.contains_key(&key) {
                install.source = determine_source(
                    &PathBuf::from(&install.path),
                    &os_defaults,
                    &env_path,
                );
                seen.insert(key, install);
            }
        }
    }

    for default in &os_defaults {
        if !default.exists() || scanned.iter().any(|s| s == default) {
            continue;
        }
        let (installs, errs) = scan_parent_folder(default);
        all_errors.extend(errs);
        for mut install in installs {
            let key = install.path.clone();
            if !seen.contains_key(&key) {
                install.source = "Hub".to_string();
                seen.insert(key, install);
            }
        }
    }

    if let Some(ref env) = env_path {
        if env.exists() && !scanned.iter().any(|s| s == env) {
            let (installs, errs) = scan_parent_folder(env);
            all_errors.extend(errs);
            for mut install in installs {
                let key = install.path.clone();
                if !seen.contains_key(&key) {
                    install.source = "Env".to_string();
                    seen.insert(key, install);
                }
            }
        }
    }

    let mut installations: Vec<UnityInstallation> = seen.into_values().collect();
    installations.sort_by(|a, b| b.version.cmp(&a.version));

    DiscoveryResult {
        installations,
        errors: all_errors,
    }
}

fn run_discovery(state: &State<AppState>) -> DiscoveryResult {
    let settings = state.settings.lock().unwrap().clone();
    discover_unity_installations(&settings)
}

#[tauri::command]
pub fn discover_installations(state: State<AppState>) -> DiscoveryResult {
    {
        let cache = state.discovery_cache.lock().unwrap();
        if let Some(ref result) = *cache {
            return result.clone();
        }
    }

    let result = run_discovery(&state);

    {
        let mut cache = state.discovery_cache.lock().unwrap();
        *cache = Some(result.clone());
    }

    result
}

#[tauri::command]
pub fn refresh_discovery(state: State<AppState>) -> DiscoveryResult {
    let result = run_discovery(&state);

    {
        let mut cache = state.discovery_cache.lock().unwrap();
        *cache = Some(result.clone());
    }

    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::schemas::Settings;

    fn make_editor_dir(dir: &std::path::Path, version: &str) -> std::path::PathBuf {
        let version_dir = dir.join(version);
        if cfg!(target_os = "macos") {
            let app_dir = version_dir.join("Unity.app").join("Contents").join("MacOS");
            fs::create_dir_all(&app_dir).unwrap();
            fs::write(app_dir.join("Unity"), "").unwrap();
        } else if cfg!(target_os = "windows") {
            let editor_dir = version_dir.join("Editor");
            fs::create_dir_all(&editor_dir).unwrap();
            fs::write(editor_dir.join("Unity.exe"), "").unwrap();
        } else {
            let editor_dir = version_dir.join("Editor");
            fs::create_dir_all(&editor_dir).unwrap();
            fs::write(editor_dir.join("Unity"), "").unwrap();
        }
        version_dir
    }

    fn isolated_settings(parent: &std::path::Path) -> Settings {
        let mut settings = Settings::default();
        settings.unity_discovery.parent_folders = vec![parent.to_string_lossy().to_string()];
        settings
    }

    fn discover_isolated(dir: &std::path::Path) -> DiscoveryResult {
        let settings = isolated_settings(dir);
        let mut result = discover_unity_installations(&settings);
        let prefix = dir.to_string_lossy().to_string();
        result.installations.retain(|i| i.path.starts_with(&prefix));
        result
    }

    #[test]
    fn discover_finds_installations_in_settings_folder() {
        let dir = tempfile::tempdir().unwrap();
        make_editor_dir(dir.path(), "6000.0.1f1");
        make_editor_dir(dir.path(), "2022.3.48f1");

        let result = discover_isolated(dir.path());
        assert_eq!(result.installations.len(), 2);
        assert!(result.errors.is_empty());
        assert!(result.installations.iter().any(|i| i.version == "6000.0.1f1"));
        assert!(result.installations.iter().any(|i| i.version == "2022.3.48f1"));
    }

    #[test]
    fn discover_results_sorted_version_desc() {
        let dir = tempfile::tempdir().unwrap();
        make_editor_dir(dir.path(), "2022.3.48f1");
        make_editor_dir(dir.path(), "6000.0.1f1");
        make_editor_dir(dir.path(), "6000.0.2f1");

        let result = discover_isolated(dir.path());
        let versions: Vec<&str> = result.installations.iter().map(|i| i.version.as_str()).collect();
        assert_eq!(versions, vec!["6000.0.2f1", "6000.0.1f1", "2022.3.48f1"]);
    }

    #[test]
    fn discover_skips_non_editor_subdirs() {
        let dir = tempfile::tempdir().unwrap();
        make_editor_dir(dir.path(), "6000.0.1f1");
        fs::create_dir_all(dir.path().join("not-an-editor")).unwrap();

        let result = discover_isolated(dir.path());
        assert_eq!(result.installations.len(), 1);
    }

    #[test]
    fn discover_nonexistent_parent_is_skipped() {
        let mut settings = Settings::default();
        settings.unity_discovery.parent_folders = vec!["/nonexistent/path/12345".to_string()];

        let result = discover_unity_installations(&settings);
        let from_test_path: Vec<_> = result
            .installations
            .iter()
            .filter(|i| i.path.contains("/nonexistent/path/12345"))
            .collect();
        assert!(from_test_path.is_empty());
    }

    #[test]
    fn discover_unreadable_dir_returns_error() {
        let dir = tempfile::tempdir().unwrap();
        let not_a_dir = dir.path().join("file.txt");
        fs::write(&not_a_dir, "not a dir").unwrap();

        let mut settings = Settings::default();
        settings.unity_discovery.parent_folders = vec![not_a_dir.to_string_lossy().to_string()];

        let result = discover_unity_installations(&settings);
        let test_errors: Vec<_> = result
            .errors
            .iter()
            .filter(|e| e.parent_path.contains("file.txt"))
            .collect();
        assert_eq!(test_errors.len(), 1);
        assert!(test_errors[0].message.contains("Cannot read directory"));
    }

    #[test]
    fn discover_source_manual_for_non_default_path() {
        let dir = tempfile::tempdir().unwrap();
        make_editor_dir(dir.path(), "6000.0.1f1");

        let result = discover_isolated(dir.path());
        assert_eq!(result.installations.len(), 1);
        assert_eq!(result.installations[0].source, "Manual");
    }

    #[test]
    fn discover_deduplicates_by_path() {
        let dir = tempfile::tempdir().unwrap();
        make_editor_dir(dir.path(), "6000.0.1f1");

        let path_str = dir.path().to_string_lossy().to_string();
        let mut settings = Settings::default();
        settings.unity_discovery.parent_folders = vec![path_str.clone(), path_str.clone()];

        let mut result = discover_unity_installations(&settings);
        let prefix = dir.path().to_string_lossy().to_string();
        result.installations.retain(|i| i.path.starts_with(&prefix));
        assert_eq!(result.installations.len(), 1);
    }

    #[test]
    fn discover_install_date_present() {
        let dir = tempfile::tempdir().unwrap();
        make_editor_dir(dir.path(), "6000.0.1f1");

        let result = discover_isolated(dir.path());
        assert_eq!(result.installations.len(), 1);
        assert!(result.installations[0].install_date.is_some());
    }

    #[test]
    fn discover_empty_settings_finds_nothing_without_os_default() {
        let mut settings = Settings::default();
        settings.unity_discovery.parent_folders = vec![];

        let result = discover_unity_installations(&settings);
        assert!(result.installations.is_empty() || result
            .installations
            .iter()
            .all(|i| i.source == "Hub"));
    }
}
