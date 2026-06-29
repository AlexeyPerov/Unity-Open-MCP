use std::collections::HashMap;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::schemas::Settings;

/// Mapping from a Unity `Data/PlaybackEngines/<folder>` name to the
/// friendly build-target label the Unity Versions tab renders. Covers
/// the Windows-flavoured keys (used by Hub itself) plus the macOS / Linux
/// equivalents Hub sees on those hosts. Unknown folders fall back to
/// the lowercased folder name so a new platform Unity ships is not
/// silently dropped — the user sees *something* even before Hub knows
/// how to label it.
fn friendly_playback_engine_name(folder: &str) -> String {
    match folder.to_ascii_lowercase().as_str() {
        "androidplayer" => "Android".to_string(),
        "windowsstandalonesupport" => "Win64".to_string(),
        "linuxstandalonesupport" | "linuxstandalone" => "Linux64".to_string(),
        "osxstandalonesupport" | "osxstandalone" => "OSX".to_string(),
        "webglsupport" | "webgl" => "WebGL".to_string(),
        "metrosupport" => "UWP".to_string(),
        "iossupport" | "iphone-player" => "iOS".to_string(),
        "appletvsupport" => "tvOS".to_string(),
        "visionosplayer" => "visionOS".to_string(),
        "switchplayer" | "switchsupport" => "Switch".to_string(),
        "ps4player" | "ps5player" => folder.to_ascii_uppercase(),
        other => other.to_string(),
    }
}

/// Release stream inferred from the Unity version suffix. Uses the
/// `a`/`b`/`f` stream heuristics. Used by the Unity Versions tab to
/// render an LTS / TECH / BETA / ALPHA chip per installed editor in
/// addition to the OS source chip.
fn release_type_for(version: &str) -> String {
    // Unity versions follow `MAJOR.MINOR.PATCH<kind><seq>` where
    // `<kind>` is `a` (alpha), `b` (beta), `f` (final), `p` (patch),
    // or `c` (China variant). LTS lines are final releases on a
    // long-term branch; Unity 6 / 2022.3 / 2021.3 are LTS, others are
    // TECH. We can't tell LTS from TECH purely from the version string
    // (Unity did not encode it), so we treat `f` as `LTS` when the
    // branch is a known LTS line and `TECH` otherwise — the Releases
    // tab is the source of truth for the canonical stream label.
    //
    // We only look at the *kind marker*: the last non-digit character
    // of the version after stripping the trailing revision digits. A
    // simple `contains('a')` would false-fire on `garbage` (matches
    // the `a` in `gar`), which is why this helper inspects the suffix
    // shape rather than the whole string.
    let kind = version_kind_marker(version);
    match kind {
        'a' => "Alpha".to_string(),
        'b' => "Beta".to_string(),
        'f' => {
            let lower = version.to_ascii_lowercase();
            // Known Unity LTS lines (min supported by the packages is 2022.3 LTS).
            let is_known_lts = lower.starts_with("6000.0")
                || lower.starts_with("2022.3")
                || lower.starts_with("2021.3")
                || lower.starts_with("2020.3")
                || lower.starts_with("2019.4");
            if is_known_lts {
                "LTS".to_string()
            } else {
                "TECH".to_string()
            }
        }
        _ => String::new(),
    }
}

/// Returns the Unity version's kind marker character (`a` / `b` / `f` /
/// `p` / `c`) by scanning the version from the end and returning the
/// last alphabetic character before the trailing digits. Returns `\0`
/// for strings without a kind marker (e.g. `garbage`, empty, or a pure
/// numeric form).
fn version_kind_marker(version: &str) -> char {
    let chars: Vec<char> = version.chars().collect();
    // Walk from the end past any trailing digits, then return the next
    // character if it is an ASCII letter. This is the canonical Unity
    // form: `<...>.<patch><kind><seq>` (e.g. `6000.0.1f1`, `2022.3.48f1`,
    // `6000.1.0b5`, `6000.2.0a3`).
    let mut i = chars.len();
    while i > 0 && chars[i - 1].is_ascii_digit() {
        i -= 1;
    }
    if i > 0 {
        let c = chars[i - 1];
        if c.is_ascii_lowercase() || c.is_ascii_uppercase() {
            return c.to_ascii_lowercase();
        }
    }
    '\0'
}

/// Scan an editor install's `Data/PlaybackEngines/` directory and
/// return the friendly platform names of every build target Unity
/// shipped modules for. The list always includes the host editor's own
/// platform (the standalone player is not a PlaybackEngines folder — it
/// ships with the Editor binary itself — so we add it explicitly so
/// desktop versions are always present).
///
/// Returns an empty `Vec` when the editor install is missing the
/// `Data/PlaybackEngines/` directory (a minimal / custom build). The
/// caller renders an empty platforms chip in that case rather than
/// asserting the host platform — the Unity install genuinely does not
/// advertise any extra build targets.
fn scan_playback_engines(install_path: &Path) -> Vec<String> {
    let data_folder = editor_data_folder(install_path);
    let playback_engines = match data_folder {
        Some(d) => d.join("PlaybackEngines"),
        None => return Vec::new(),
    };

    let read = match fs::read_dir(&playback_engines) {
        Ok(r) => r,
        Err(_) => return Vec::new(),
    };

    let mut platforms: Vec<String> = Vec::new();
    for entry in read.flatten() {
        let path = entry.path();
        if !path.is_dir() {
            continue;
        }
        let Some(name) = path.file_name().and_then(|n| n.to_str()) else {
            continue;
        };
        let friendly = friendly_playback_engine_name(name);
        if !platforms.contains(&friendly) {
            platforms.push(friendly);
        }
    }
    platforms.sort();
    platforms
}

/// Resolve the editor install's `Data/` folder. On macOS the editor
/// lives inside `Unity.app/Contents/`; on Windows/Linux it sits next to
/// `Unity.exe` / `Unity` in an `Editor/` subfolder. Returns `None` for
/// source-build layouts (`build/WindowsEditor/...`) where `Data/` is a
/// sibling of the binary rather than nested under a bundle — handled by
/// the source-build fallback in `is_unity_editor_dir`.
fn editor_data_folder(install_path: &Path) -> Option<PathBuf> {
    if cfg!(target_os = "macos") {
        // <install>/Unity.app/Contents/Data
        let candidate = install_path
            .join("Unity.app")
            .join("Contents")
            .join("Data");
        if candidate.is_dir() {
            return Some(candidate);
        }
        None
    } else {
        // <install>/Editor/Data (Hub-style) or <install>/Data
        // (source-build sibling).
        let editor_data = install_path.join("Editor").join("Data");
        if editor_data.is_dir() {
            return Some(editor_data);
        }
        let sibling_data = install_path.join("Data");
        if sibling_data.is_dir() {
            return Some(sibling_data);
        }
        None
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UnityInstallation {
    pub version: String,
    pub path: String,
    pub source: String,
    pub install_date: Option<String>,
    /// M15 T6.4: build targets the editor can produce, scanned from
    /// `Data/PlaybackEngines/`. Empty for installs that ship without a
    /// PlaybackEngines folder (custom builds, source builds). The
    /// frontend renders the list as a per-row chip on the Unity
    /// Versions tab. `#[serde(default)]` keeps the cache payload
    /// loadable from older Hub builds that pre-date the field.
    #[serde(default)]
    pub platforms: Vec<String>,
    /// M15 T6.4: release stream inferred from the version suffix —
    /// `"LTS"`, `"TECH"`, `"Beta"`, `"Alpha"`, or `""` for unknown.
    /// Mirrors the `IsAlpha / IsBeta` stream heuristic so the Unity
    /// Versions tab can render the same stream chip it already renders
    /// on the Releases sub-section. `#[serde(default)]` keeps the cache
    /// payload loadable from older Hub builds.
    #[serde(default)]
    pub release_type: String,
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
        // Hub-style layout: <dir>/Editor/Unity.exe
        if dir.join("Editor").join("Unity.exe").is_file() {
            return true;
        }
        // Source-build fallback: a Unity fork built from source lays
        // out the binary at
        // `<dir>/build/WindowsEditor/x64/Release/Unity.exe` instead of
        // `<dir>/Editor/Unity.exe`. We accept the source-build path so
        // teams iterating on a custom Unity still appear in the
        // Versions tab.
        dir.join("build")
            .join("WindowsEditor")
            .join("x64")
            .join("Release")
            .join("Unity.exe")
            .is_file()
    } else {
        if dir.join("Editor").join("Unity").is_file() {
            return true;
        }
        // Linux source-build fallback mirrors the Windows one. Unity's
        // own Linux build pipeline uses the same `build/.../Release`
        // layout.
        dir.join("build")
            .join("LinuxEditor")
            .join("Unity")
            .is_file()
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
            version: version.clone(),
            path: path.to_string_lossy().to_string(),
            source: String::new(),
            install_date: get_install_date(&path),
            platforms: scan_playback_engines(&path),
            release_type: release_type_for(&version),
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

/// Tauri requires async commands that borrow `State` (a reference) to
/// return a `Result`. On the JS side `Ok(T)` is unwrapped to `T`, so the
/// frontend contract is unchanged; an `Err` rejects the invoke promise and
/// is surfaced by the discovery store's existing `try/catch`.
type DiscoveryCommandResult = Result<DiscoveryResult, String>;

#[tauri::command]
pub async fn discover_installations(state: State<'_, AppState>) -> DiscoveryCommandResult {
    // The cache check is a cheap Mutex read; keep it on the async task
    // so a warm cache returns without a thread-pool hop.
    {
        let cache = state.discovery_cache.lock().unwrap();
        if let Some(ref result) = *cache {
            return Ok(result.clone());
        }
    }

    // `discover_unity_installations` does `read_dir` + per-entry stat
    // across every configured parent folder, the OS default, and the
    // `UNITY_HUB` env path. On a slow / networked volume that stalls
    // for the filesystem timeout, so run it on the blocking pool to
    // keep the webview thread responsive (the Unity Versions tab fires
    // this on mount and on every manual refresh).
    let settings = state.settings.lock().unwrap().clone();
    let start = std::time::Instant::now();
    let result = tauri::async_runtime::spawn_blocking(move || {
        discover_unity_installations(&settings)
    })
    .await
    .map_err(|e| format!("discovery task failed: {e}"))?;
    log::info!(
        "discover_installations: {} installs in {}ms",
        result.installations.len(),
        start.elapsed().as_millis()
    );

    {
        let mut cache = state.discovery_cache.lock().unwrap();
        *cache = Some(result.clone());
    }

    Ok(result)
}

#[tauri::command]
pub async fn refresh_discovery(state: State<'_, AppState>) -> DiscoveryCommandResult {
    let settings = state.settings.lock().unwrap().clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        discover_unity_installations(&settings)
    })
    .await
    .map_err(|e| format!("discovery task failed: {e}"))?;

    {
        let mut cache = state.discovery_cache.lock().unwrap();
        *cache = Some(result.clone());
    }

    Ok(result)
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

    /// Add a PlaybackEngines tree under an editor install dir created
    /// by `make_editor_dir`. `engines` is the list of folder names
    /// (e.g. `["androidplayer", "webglsupport"]`) to materialise.
    fn add_playback_engines(install_dir: &std::path::Path, engines: &[&str]) {
        let data_folder = if cfg!(target_os = "macos") {
            install_dir
                .join("Unity.app")
                .join("Contents")
                .join("Data")
                .join("PlaybackEngines")
        } else {
            install_dir
                .join("Editor")
                .join("Data")
                .join("PlaybackEngines")
        };
        fs::create_dir_all(&data_folder).unwrap();
        for engine in engines {
            fs::create_dir_all(data_folder.join(engine)).unwrap();
        }
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

    #[test]
    fn discover_enumerates_playback_engines_as_platforms() {
        // T6.4: an editor that ships the Android + WebGL + iOS
        // PlaybackEngines should report those three friendly names on
        // the installation row. The scan is filesystem-only — it does
        // not invoke Unity — so a fake tree is enough to exercise it.
        let dir = tempfile::tempdir().unwrap();
        let install = make_editor_dir(dir.path(), "6000.0.1f1");
        add_playback_engines(&install, &["androidplayer", "webglsupport", "iossupport"]);

        let result = discover_isolated(dir.path());
        assert_eq!(result.installations.len(), 1);
        let platforms = &result.installations[0].platforms;
        // Sorted alphabetically by the scanner.
        assert_eq!(platforms, &vec!["Android", "WebGL", "iOS"]);
    }

    #[test]
    fn discover_platforms_empty_without_playback_engines_folder() {
        // A minimal / custom editor without a PlaybackEngines folder
        // reports an empty platform list rather than asserting the host
        // platform. The Unity Versions tab renders an empty chip.
        let dir = tempfile::tempdir().unwrap();
        make_editor_dir(dir.path(), "6000.0.1f1");

        let result = discover_isolated(dir.path());
        assert_eq!(result.installations.len(), 1);
        assert!(result.installations[0].platforms.is_empty());
    }

    #[test]
    fn discover_platforms_dedupe_known_aliases() {
        // `windowsstandalonesupport` and `osxstandalonesupport` both
        // resolve to single entries; the scanner must not produce
        // duplicates when the same friendly name maps from two folders.
        let dir = tempfile::tempdir().unwrap();
        let install = make_editor_dir(dir.path(), "6000.0.1f1");
        add_playback_engines(
            &install,
            &[
                "windowsstandalonesupport",
                "LinuxStandalone",
                "LinuxStandaloneSupport",
            ],
        );

        let result = discover_isolated(dir.path());
        let platforms = &result.installations[0].platforms;
        // Two distinct friendly names: Win64 and Linux64. The Linux
        // aliases collapse into a single entry.
        assert_eq!(platforms, &vec!["Linux64", "Win64"]);
    }

    #[test]
    fn discover_release_type_lts_for_known_lines() {
        let dir = tempfile::tempdir().unwrap();
        make_editor_dir(dir.path(), "6000.0.1f1");
        make_editor_dir(dir.path(), "2022.3.48f1");
        make_editor_dir(dir.path(), "2021.3.45f1");

        let result = discover_isolated(dir.path());
        let by_version: HashMap<&str, &str> = result
            .installations
            .iter()
            .map(|i| (i.version.as_str(), i.release_type.as_str()))
            .collect();
        assert_eq!(by_version.get("6000.0.1f1"), Some(&"LTS"));
        assert_eq!(by_version.get("2022.3.48f1"), Some(&"LTS"));
        assert_eq!(by_version.get("2021.3.45f1"), Some(&"LTS"));
    }

    #[test]
    fn discover_release_type_tech_beta_alpha() {
        let dir = tempfile::tempdir().unwrap();
        make_editor_dir(dir.path(), "6000.1.0b5");
        make_editor_dir(dir.path(), "6000.2.0a3");
        make_editor_dir(dir.path(), "6000.1.5f1");

        let result = discover_isolated(dir.path());
        let by_version: HashMap<&str, &str> = result
            .installations
            .iter()
            .map(|i| (i.version.as_str(), i.release_type.as_str()))
            .collect();
        // 6000.1.x is TECH (not in the known LTS list).
        assert_eq!(by_version.get("6000.1.5f1"), Some(&"TECH"));
        assert_eq!(by_version.get("6000.1.0b5"), Some(&"Beta"));
        assert_eq!(by_version.get("6000.2.0a3"), Some(&"Alpha"));
    }

    #[test]
    fn friendly_playback_engine_name_maps_known_and_falls_back() {
        assert_eq!(friendly_playback_engine_name("androidplayer"), "Android");
        assert_eq!(
            friendly_playback_engine_name("WindowsStandaloneSupport"),
            "Win64"
        );
        assert_eq!(friendly_playback_engine_name("WebGLSupport"), "WebGL");
        assert_eq!(friendly_playback_engine_name("iOSSupport"), "iOS");
        // Unknown folder names are passed through lowercased so the
        // user sees *something* rather than a silent drop.
        assert_eq!(friendly_playback_engine_name("NewFuturePlatform"), "newfutureplatform");
    }

    #[test]
    fn release_type_for_handles_blank_and_unknown() {
        assert_eq!(release_type_for(""), "");
        assert_eq!(release_type_for("garbage"), "");
        assert_eq!(release_type_for("6000.0.1f1"), "LTS");
        assert_eq!(release_type_for("2023.3.0b1"), "Beta");
    }

    #[test]
    fn editor_data_folder_resolves_per_platform_layouts() {
        // Hub-style macOS layout: <install>/Unity.app/Contents/Data
        let mac_dir = tempfile::tempdir().unwrap();
        let mac_install = mac_dir.path().join("6000.0.1f1");
        let mac_data = mac_install
            .join("Unity.app")
            .join("Contents")
            .join("Data")
            .join("PlaybackEngines");
        fs::create_dir_all(&mac_data).unwrap();
        fs::create_dir_all(mac_data.join("androidplayer")).unwrap();
        let platforms = scan_playback_engines(&mac_install);
        assert_eq!(platforms, vec!["Android"]);
    }

    #[test]
    fn is_unity_editor_dir_accepts_source_build_layout_on_non_macos() {
        // The source-build fallback is gated on a non-macOS target; on
        // macOS the bundle path is the only valid layout. We exercise
        // the function under its cfg! gate so the assertion is
        // meaningful on the host that runs CI.
        let dir = tempfile::tempdir().unwrap();
        let install = dir.path().join("6000.0.1f1-src");
        if cfg!(target_os = "macos") {
            // On macOS the source-build path is not consulted.
            fs::create_dir_all(&install).unwrap();
            assert!(!is_unity_editor_dir(&install));
        } else if cfg!(target_os = "windows") {
            let bin = install
                .join("build")
                .join("WindowsEditor")
                .join("x64")
                .join("Release");
            fs::create_dir_all(&bin).unwrap();
            fs::write(bin.join("Unity.exe"), "").unwrap();
            assert!(is_unity_editor_dir(&install));
        } else {
            let bin = install.join("build").join("LinuxEditor");
            fs::create_dir_all(&bin).unwrap();
            fs::write(bin.join("Unity"), "").unwrap();
            assert!(is_unity_editor_dir(&install));
        }
    }
}
