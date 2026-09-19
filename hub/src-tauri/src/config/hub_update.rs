//! Background update discovery and user-initiated installer download for the Hub.
//!
//! GitHub Releases contains two independent release lines. Only `hub-v*` tags
//! are considered here; the shared MCP/package `v*` releases are deliberately
//! ignored. Checks are cached for one hour and every attempt advances the
//! timestamp, including network failures, so an offline machine cannot enter a
//! retry storm.

use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use semver::Version;
use serde::{Deserialize, Serialize};

use crate::config::paths;

const RELEASES_API: &str =
    "https://api.github.com/repos/AlexeyPerov/Unity-Open-MCP/releases?per_page=100";
const RELEASES_PAGE: &str = "https://github.com/AlexeyPerov/Unity-Open-MCP/releases";
const ASSET_URL_PREFIX: &str = "https://github.com/AlexeyPerov/Unity-Open-MCP/releases/download/";
const CACHE_VERSION: u32 = 1;
pub const CHECK_INTERVAL_SECONDS: u64 = 60 * 60;
const REMIND_LATER_SECONDS: u64 = 24 * 60 * 60;
const HTTP_TIMEOUT_SECONDS: u64 = 20;
const DOWNLOAD_TIMEOUT_SECONDS: u64 = 10 * 60;
const MAX_DOWNLOAD_BYTES: u64 = 750 * 1024 * 1024;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct HubUpdateRelease {
    pub version: String,
    pub tag_name: String,
    pub release_notes_url: String,
    pub asset_name: String,
    pub asset_url: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct UpdateCache {
    version: u32,
    #[serde(default)]
    checked_at_epoch: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    available: Option<HubUpdateRelease>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    dismissed_version: Option<String>,
    #[serde(default)]
    remind_after_epoch: u64,
}

impl Default for UpdateCache {
    fn default() -> Self {
        Self {
            version: CACHE_VERSION,
            checked_at_epoch: 0,
            available: None,
            dismissed_version: None,
            remind_after_epoch: 0,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct HubUpdateStatus {
    pub state: String,
    pub running_version: String,
    pub checked_at_epoch: u64,
    pub next_check_at_epoch: u64,
    pub manual_download_url: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub release: Option<HubUpdateRelease>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct HubUpdateApplyResult {
    pub installer_path: String,
    pub installer_launched: bool,
    pub manual_download_url: String,
    pub message: String,
}

#[derive(Debug, Clone, Deserialize)]
struct GithubAsset {
    name: String,
    browser_download_url: String,
}

#[derive(Debug, Clone, Deserialize)]
struct GithubRelease {
    tag_name: String,
    html_url: String,
    #[serde(default)]
    draft: bool,
    #[serde(default)]
    prerelease: bool,
    #[serde(default)]
    assets: Vec<GithubAsset>,
}

fn now_epoch() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

fn cache_path() -> PathBuf {
    paths::config_dir().join("cache").join("hub-update.json")
}

fn read_cache_from(path: &Path) -> UpdateCache {
    fs::read_to_string(path)
        .ok()
        .and_then(|body| serde_json::from_str::<UpdateCache>(&body).ok())
        .filter(|cache| cache.version == CACHE_VERSION)
        .unwrap_or_default()
}

fn write_cache_to(path: &Path, cache: &UpdateCache) -> Result<(), String> {
    let parent = path
        .parent()
        .ok_or_else(|| "update cache path has no parent".to_string())?;
    fs::create_dir_all(parent).map_err(|e| format!("create update cache dir: {e}"))?;
    let temp = path.with_extension("json.tmp");
    let body =
        serde_json::to_vec_pretty(cache).map_err(|e| format!("serialize update cache: {e}"))?;
    fs::write(&temp, body).map_err(|e| format!("write update cache: {e}"))?;
    if path.exists() {
        fs::remove_file(path).map_err(|e| format!("replace update cache: {e}"))?;
    }
    fs::rename(&temp, path).map_err(|e| format!("commit update cache: {e}"))
}

fn official_release_build() -> bool {
    !cfg!(debug_assertions) && option_env!("HUB_OFFICIAL_RELEASE") == Some("1")
}

fn asset_matches_current_platform(name: &str, os: &str, arch: &str) -> bool {
    let lower = name.to_ascii_lowercase();
    match os {
        "windows" => {
            let arch_matches = match arch {
                "x86_64" => lower.contains("_x64"),
                "aarch64" => lower.contains("_aarch64") || lower.contains("_arm64"),
                _ => false,
            };
            arch_matches && lower.ends_with("-setup.exe")
        }
        "macos" => {
            let arch_matches = match arch {
                "x86_64" => lower.contains("_x64"),
                "aarch64" => lower.contains("_aarch64") || lower.contains("_arm64"),
                _ => false,
            };
            arch_matches && lower.ends_with(".dmg")
        }
        "linux" => {
            let arch_matches = match arch {
                "x86_64" => lower.contains("x86_64") || lower.contains("x64"),
                "aarch64" => lower.contains("aarch64") || lower.contains("arm64"),
                _ => false,
            };
            arch_matches && lower.ends_with(".appimage")
        }
        _ => false,
    }
}

fn parse_latest_release(
    body: &str,
    running: &Version,
    os: &str,
    arch: &str,
) -> Result<Option<HubUpdateRelease>, String> {
    let releases: Vec<GithubRelease> =
        serde_json::from_str(body).map_err(|e| format!("parse GitHub releases: {e}"))?;

    let mut candidates = releases
        .into_iter()
        .filter(|release| !release.draft && !release.prerelease)
        .filter_map(|release| {
            let raw = release.tag_name.strip_prefix("hub-v")?;
            let version = Version::parse(raw).ok()?;
            Some((version, release))
        })
        .filter(|(version, _)| version > running)
        .collect::<Vec<_>>();
    candidates.sort_by(|a, b| b.0.cmp(&a.0));

    for (version, release) in candidates {
        if let Some(asset) = release
            .assets
            .iter()
            .find(|asset| asset_matches_current_platform(&asset.name, os, arch))
        {
            if !asset.browser_download_url.starts_with(ASSET_URL_PREFIX) {
                return Err("GitHub returned a non-HTTPS or unexpected update asset URL".into());
            }
            return Ok(Some(HubUpdateRelease {
                version: version.to_string(),
                tag_name: release.tag_name,
                release_notes_url: release.html_url,
                asset_name: asset.name.clone(),
                asset_url: asset.browser_download_url.clone(),
            }));
        }
    }
    Ok(None)
}

fn visible_release(cache: &UpdateCache, running: &Version, now: u64) -> Option<HubUpdateRelease> {
    let release = cache.available.as_ref()?;
    let available = Version::parse(&release.version).ok()?;
    if available <= *running
        || cache.dismissed_version.as_deref() == Some(release.version.as_str())
        || cache.remind_after_epoch > now
    {
        None
    } else {
        Some(release.clone())
    }
}

fn status_from_cache(
    cache: &UpdateCache,
    running: &Version,
    now: u64,
    state_if_hidden: &str,
    message: Option<String>,
) -> HubUpdateStatus {
    let release = visible_release(cache, running, now);
    HubUpdateStatus {
        state: if release.is_some() {
            "available".to_string()
        } else {
            state_if_hidden.to_string()
        },
        running_version: running.to_string(),
        checked_at_epoch: cache.checked_at_epoch,
        next_check_at_epoch: cache
            .checked_at_epoch
            .saturating_add(CHECK_INTERVAL_SECONDS),
        manual_download_url: RELEASES_PAGE.to_string(),
        release,
        message,
    }
}

fn fetch_release_body() -> Result<String, String> {
    let agent = ureq::AgentBuilder::new()
        .timeout(Duration::from_secs(HTTP_TIMEOUT_SECONDS))
        .build();
    let response = agent
        .get(RELEASES_API)
        .set("Accept", "application/vnd.github+json")
        .set("User-Agent", "unity-hub-pro")
        .call()
        .map_err(|e| format!("GitHub release check failed: {e}"))?;
    if !response.get_url().starts_with("https://") {
        return Err("GitHub release check redirected to a non-HTTPS URL".into());
    }
    response
        .into_string()
        .map_err(|e| format!("read GitHub release response: {e}"))
}

fn check_sync(force: bool, official: bool, path: &Path, now: u64) -> HubUpdateStatus {
    let running =
        Version::parse(env!("CARGO_PKG_VERSION")).unwrap_or_else(|_| Version::new(0, 0, 0));
    let mut cache = read_cache_from(path);

    if !official {
        return status_from_cache(
            &UpdateCache::default(),
            &running,
            now,
            "skipped",
            Some("Update checks are disabled for development and local builds.".into()),
        );
    }

    if !force
        && cache.checked_at_epoch > 0
        && now.saturating_sub(cache.checked_at_epoch) < CHECK_INTERVAL_SECONDS
    {
        return status_from_cache(&cache, &running, now, "throttled", None);
    }

    // Record the attempt before touching the network. A write failure is
    // non-fatal, but a successful write prevents an offline retry storm.
    cache.checked_at_epoch = now;
    let _ = write_cache_to(path, &cache);

    match fetch_release_body().and_then(|body| {
        parse_latest_release(
            &body,
            &running,
            std::env::consts::OS,
            std::env::consts::ARCH,
        )
    }) {
        Ok(available) => {
            if cache.available.as_ref().map(|r| &r.version)
                != available.as_ref().map(|r| &r.version)
            {
                cache.dismissed_version = None;
                cache.remind_after_epoch = 0;
            }
            cache.available = available;
            let _ = write_cache_to(path, &cache);
            status_from_cache(&cache, &running, now, "upToDate", None)
        }
        Err(error) => {
            let _ = write_cache_to(path, &cache);
            status_from_cache(&cache, &running, now, "error", Some(error))
        }
    }
}

#[tauri::command]
pub async fn check_hub_update(force: Option<bool>) -> HubUpdateStatus {
    tauri::async_runtime::spawn_blocking(move || {
        check_sync(
            force.unwrap_or(false),
            official_release_build(),
            &cache_path(),
            now_epoch(),
        )
    })
    .await
    .unwrap_or_else(|e| HubUpdateStatus {
        state: "error".into(),
        running_version: env!("CARGO_PKG_VERSION").into(),
        checked_at_epoch: 0,
        next_check_at_epoch: 0,
        manual_download_url: RELEASES_PAGE.into(),
        release: None,
        message: Some(format!("update check task failed: {e}")),
    })
}

fn update_suppression(action: &str) -> Result<HubUpdateStatus, String> {
    let now = now_epoch();
    let path = cache_path();
    let mut cache = read_cache_from(&path);
    let running = Version::parse(env!("CARGO_PKG_VERSION"))
        .map_err(|e| format!("invalid running Hub version: {e}"))?;
    let available_version = cache
        .available
        .as_ref()
        .map(|release| release.version.clone());
    match action {
        "dismiss" => {
            cache.dismissed_version = available_version;
            cache.remind_after_epoch = 0;
        }
        "remindLater" => {
            cache.dismissed_version = None;
            cache.remind_after_epoch = now.saturating_add(REMIND_LATER_SECONDS);
        }
        _ => return Err(format!("unsupported update notice action: {action}")),
    }
    write_cache_to(&path, &cache)?;
    Ok(status_from_cache(&cache, &running, now, "suppressed", None))
}

#[tauri::command]
pub async fn set_hub_update_notice(action: String) -> Result<HubUpdateStatus, String> {
    tauri::async_runtime::spawn_blocking(move || update_suppression(&action))
        .await
        .map_err(|e| format!("update notice task failed: {e}"))?
}

fn safe_installer_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 180
        && !name.contains('/')
        && !name.contains('\\')
        && !name.contains("..")
}

fn download_installer(release: &HubUpdateRelease) -> Result<PathBuf, String> {
    if !release.asset_url.starts_with(ASSET_URL_PREFIX) || !safe_installer_name(&release.asset_name)
    {
        return Err("refusing an unexpected update asset path".into());
    }
    let dir = paths::config_dir()
        .join("cache")
        .join("updates")
        .join(&release.version);
    fs::create_dir_all(&dir).map_err(|e| format!("create update download dir: {e}"))?;
    let target = dir.join(&release.asset_name);
    let temp = dir.join(format!("{}.part", release.asset_name));

    let agent = ureq::AgentBuilder::new()
        .timeout(Duration::from_secs(DOWNLOAD_TIMEOUT_SECONDS))
        .build();
    let response = agent
        .get(&release.asset_url)
        .set("Accept", "application/octet-stream")
        .set("User-Agent", "unity-hub-pro")
        .call()
        .map_err(|e| format!("download update: {e}"))?;
    if !response.get_url().starts_with("https://") {
        return Err("update download redirected to a non-HTTPS URL".into());
    }
    if let Some(length) = response.header("Content-Length") {
        if length.parse::<u64>().unwrap_or(MAX_DOWNLOAD_BYTES + 1) > MAX_DOWNLOAD_BYTES {
            return Err("update installer exceeds the 750 MiB safety limit".into());
        }
    }

    let mut reader = response.into_reader().take(MAX_DOWNLOAD_BYTES + 1);
    let mut file = fs::File::create(&temp).map_err(|e| format!("create update file: {e}"))?;
    let copied =
        std::io::copy(&mut reader, &mut file).map_err(|e| format!("write update file: {e}"))?;
    file.flush()
        .map_err(|e| format!("flush update file: {e}"))?;
    if copied == 0 || copied > MAX_DOWNLOAD_BYTES {
        let _ = fs::remove_file(&temp);
        return Err("downloaded update has an invalid size".into());
    }
    if target.exists() {
        fs::remove_file(&target).map_err(|e| format!("replace cached installer: {e}"))?;
    }
    fs::rename(&temp, &target).map_err(|e| format!("commit downloaded installer: {e}"))?;
    Ok(target)
}

fn launch_installer(path: &Path) -> Result<(), String> {
    #[cfg(target_os = "windows")]
    let mut command = Command::new(path);
    #[cfg(target_os = "macos")]
    let mut command = {
        let mut command = Command::new("open");
        command.arg(path);
        command
    };
    #[cfg(target_os = "linux")]
    let mut command = {
        use std::os::unix::fs::PermissionsExt;
        let metadata = fs::metadata(path).map_err(|e| format!("read AppImage metadata: {e}"))?;
        let mut permissions = metadata.permissions();
        permissions.set_mode(permissions.mode() | 0o111);
        fs::set_permissions(path, permissions)
            .map_err(|e| format!("make AppImage executable: {e}"))?;
        Command::new(path)
    };
    #[cfg(not(any(target_os = "windows", target_os = "macos", target_os = "linux")))]
    return Err("automatic Hub installation is not supported on this platform".into());

    command
        .spawn()
        .map(|_| ())
        .map_err(|e| format!("launch update installer: {e}"))
}

fn apply_sync(release: HubUpdateRelease) -> Result<HubUpdateApplyResult, String> {
    if !official_release_build() {
        return Err("updates cannot be applied from a development or local build".into());
    }
    if !asset_matches_current_platform(
        &release.asset_name,
        std::env::consts::OS,
        std::env::consts::ARCH,
    ) {
        return Err("the selected update asset does not match this OS and architecture".into());
    }
    let cached = read_cache_from(&cache_path())
        .available
        .ok_or_else(|| "no checked Hub update is available to apply".to_string())?;
    if cached != release {
        return Err(
            "the requested update no longer matches the checked GitHub release; check again".into(),
        );
    }
    let installer = download_installer(&release)?;
    launch_installer(&installer)?;
    let message = if cfg!(target_os = "macos") {
        "The disk image is open. Replace Unity Hub Pro in Applications, then relaunch the Hub."
    } else if cfg!(target_os = "windows") {
        "The installer is running. Finish installation, then relaunch the Hub."
    } else {
        "The updated app has been launched. Close this Hub after confirming it starts correctly."
    };
    Ok(HubUpdateApplyResult {
        installer_path: installer.to_string_lossy().into_owned(),
        installer_launched: true,
        manual_download_url: release.release_notes_url,
        message: message.into(),
    })
}

#[tauri::command]
pub async fn apply_hub_update(release: HubUpdateRelease) -> Result<HubUpdateApplyResult, String> {
    tauri::async_runtime::spawn_blocking(move || apply_sync(release))
        .await
        .map_err(|e| format!("update apply task failed: {e}"))?
}

/// Explicit post-installer relaunch. This is never called automatically: the
/// user first completes the platform installer, then chooses Relaunch in the
/// banner so Windows/macOS have finished replacing the app on disk.
#[tauri::command]
pub fn relaunch_hub(app: tauri::AppHandle) {
    app.restart();
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::tempdir;

    const RELEASES: &str = r#"[
      {"tag_name":"v9.0.0","html_url":"https://example.invalid/trio","draft":false,"prerelease":false,"assets":[]},
      {"tag_name":"hub-v1.3.0","html_url":"https://github.com/AlexeyPerov/Unity-Open-MCP/releases/tag/hub-v1.3.0","draft":false,"prerelease":false,"assets":[
        {"name":"Unity.Hub.Pro_1.3.0_aarch64.dmg","browser_download_url":"https://github.com/AlexeyPerov/Unity-Open-MCP/releases/download/hub-v1.3.0/Unity.Hub.Pro_1.3.0_aarch64.dmg"},
        {"name":"Unity.Hub.Pro_1.3.0_x64.dmg","browser_download_url":"https://github.com/AlexeyPerov/Unity-Open-MCP/releases/download/hub-v1.3.0/Unity.Hub.Pro_1.3.0_x64.dmg"},
        {"name":"Unity.Hub.Pro_1.3.0_x64-setup.exe","browser_download_url":"https://github.com/AlexeyPerov/Unity-Open-MCP/releases/download/hub-v1.3.0/Unity.Hub.Pro_1.3.0_x64-setup.exe"}
      ]},
      {"tag_name":"hub-v1.2.0","html_url":"https://example.invalid/old","draft":false,"prerelease":false,"assets":[]}
    ]"#;

    #[test]
    fn selects_newest_hub_asset_for_platform_and_ignores_trio_release() {
        let update = parse_latest_release(
            RELEASES,
            &Version::parse("1.2.3").unwrap(),
            "macos",
            "aarch64",
        )
        .unwrap()
        .unwrap();
        assert_eq!(update.version, "1.3.0");
        assert_eq!(update.asset_name, "Unity.Hub.Pro_1.3.0_aarch64.dmg");
    }

    #[test]
    fn returns_none_when_running_version_is_current() {
        let update = parse_latest_release(
            RELEASES,
            &Version::parse("1.3.0").unwrap(),
            "windows",
            "x86_64",
        )
        .unwrap();
        assert!(update.is_none());
    }

    #[test]
    fn rejects_asset_from_unexpected_host() {
        let body = RELEASES.replace(
            "https://github.com/AlexeyPerov/Unity-Open-MCP/releases/download/",
            "http://attacker.invalid/",
        );
        let error = parse_latest_release(
            &body,
            &Version::parse("1.2.3").unwrap(),
            "windows",
            "x86_64",
        )
        .unwrap_err();
        assert!(error.contains("unexpected update asset URL"));
    }

    #[test]
    fn cache_round_trip_preserves_last_known_release() {
        let dir = tempdir().unwrap();
        let path = dir.path().join("cache").join("hub-update.json");
        let release = parse_latest_release(
            RELEASES,
            &Version::parse("1.2.3").unwrap(),
            "windows",
            "x86_64",
        )
        .unwrap();
        let cache = UpdateCache {
            checked_at_epoch: 123,
            available: release,
            ..UpdateCache::default()
        };
        write_cache_to(&path, &cache).unwrap();
        let loaded = read_cache_from(&path);
        assert_eq!(loaded.checked_at_epoch, 123);
        assert_eq!(loaded.available.unwrap().version, "1.3.0");
    }

    #[test]
    fn dismissal_and_reminder_hide_only_the_notice() {
        let release = parse_latest_release(
            RELEASES,
            &Version::parse("1.2.3").unwrap(),
            "windows",
            "x86_64",
        )
        .unwrap();
        let mut cache = UpdateCache {
            available: release,
            ..UpdateCache::default()
        };
        let running = Version::parse("1.2.3").unwrap();
        assert!(visible_release(&cache, &running, 100).is_some());
        cache.remind_after_epoch = 200;
        assert!(visible_release(&cache, &running, 100).is_none());
        assert!(visible_release(&cache, &running, 201).is_some());
        cache.dismissed_version = Some("1.3.0".into());
        assert!(visible_release(&cache, &running, 201).is_none());
    }
}
