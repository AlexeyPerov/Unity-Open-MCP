//! M1.5-20 / M1.5-21 — Unity Hub CLI integration.
//!
//! Provides:
//! - Hub CLI executable path resolution (macOS, Windows, Linux)
//! - `install_unity_version` Tauri command for headless editor installs
//! - `fetch_releases_from_hub_cli` helper for the live releases catalog

use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use tauri::{Emitter, State};

use crate::config::commands::AppState;
use crate::config::discovery;
use crate::config::releases::{ReleaseEntry, ReleaseStream};

// ── Types ──────────────────────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallResult {
    pub version: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum InstallError {
    HubNotFound,
    InstallInProgress,
    VersionEmpty,
    #[serde(rename_all = "camelCase")]
    InstallFailed { message: String },
}

// ── Hub CLI path resolution ────────────────────────────────────────

pub fn resolve_hub_cli_path() -> Option<PathBuf> {
    if cfg!(target_os = "macos") {
        let p = PathBuf::from("/Applications/Unity Hub.app/Contents/MacOS/Unity Hub");
        if p.exists() {
            return Some(p);
        }
    } else if cfg!(target_os = "windows") {
        let p = PathBuf::from(r"C:\Program Files\Unity Hub\Unity Hub.exe");
        if p.exists() {
            return Some(p);
        }
        let p = PathBuf::from(r"C:\Program Files (x86)\Unity Hub\Unity Hub.exe");
        if p.exists() {
            return Some(p);
        }
    } else {
        let candidates: Vec<PathBuf> = vec![
            PathBuf::from("/opt/UnityHub/UnityHub"),
            PathBuf::from("/usr/bin/unity-hub"),
            dirs::home_dir()?.join("Applications/Unity Hub.AppImage"),
            dirs::home_dir()?.join("Unity Hub.AppImage"),
        ];
        for c in candidates {
            if c.exists() {
                return Some(c);
            }
        }
    }
    None
}

// ── Releases CLI fetch ─────────────────────────────────────────────

pub fn fetch_releases_from_hub_cli(include_archived: bool) -> Option<Vec<ReleaseEntry>> {
    let hub_path = resolve_hub_cli_path()?;
    let mut cmd = std::process::Command::new(&hub_path);
    cmd.arg("--")
        .arg("--headless")
        .arg("editors")
        .arg("-r");
    if include_archived {
        cmd.arg("--all");
    }
    cmd.stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::null());

    let output = cmd.output().ok()?;
    if !output.status.success() {
        log::warn!(
            "Unity Hub CLI editors -r exited with {:?}",
            output.status.code()
        );
        return None;
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    let entries = parse_releases_output(&stdout);
    if entries.is_empty() {
        log::warn!("Unity Hub CLI returned empty releases output");
        return None;
    }
    Some(entries)
}

fn parse_releases_output(output: &str) -> Vec<ReleaseEntry> {
    let mut entries: Vec<ReleaseEntry> = output
        .lines()
        .filter_map(|line| {
            let line = line.trim();
            if line.is_empty() || line.starts_with('#') {
                return None;
            }
            parse_release_line(line)
        })
        .collect();
    entries.sort_by(|a, b| {
        b.release_date
            .as_deref()
            .unwrap_or("")
            .cmp(a.release_date.as_deref().unwrap_or(""))
    });
    entries
}

fn parse_release_line(line: &str) -> Option<ReleaseEntry> {
    let parts: Vec<&str> = if line.contains(',') {
        line.splitn(4, ',').collect()
    } else {
        line.split_whitespace().take(4).collect()
    };
    if parts.is_empty() {
        return None;
    }
    let version = parts[0].trim().to_string();
    if version.is_empty() {
        return None;
    }
    let changeset = parts
        .get(1)
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty());
    let release_date = parts
        .get(2)
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty());
    let stream = parts
        .get(3)
        .and_then(|s| parse_stream(s.trim()))
        .unwrap_or_else(|| infer_stream(&version));
    Some(ReleaseEntry {
        version: version.clone(),
        stream,
        release_date,
        release_notes_url: format!(
            "https://unity.com/releases/editor/whats-new/{}",
            version
        ),
        changeset,
    })
}

fn parse_stream(s: &str) -> Option<ReleaseStream> {
    match s.to_lowercase().as_str() {
        "lts" | "ltsrelease" | "stable" => Some(ReleaseStream::Lts),
        "tech" | "techstream" | "release" => Some(ReleaseStream::Tech),
        "beta" => Some(ReleaseStream::Beta),
        "alpha" => Some(ReleaseStream::Alpha),
        _ => None,
    }
}

fn infer_stream(version: &str) -> ReleaseStream {
    if version.contains('a') {
        ReleaseStream::Alpha
    } else if version.contains('b') {
        ReleaseStream::Beta
    } else {
        ReleaseStream::Tech
    }
}

// ── Install command ────────────────────────────────────────────────

#[tauri::command]
pub async fn install_unity_version(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    version: String,
    changeset: Option<String>,
) -> Result<InstallResult, InstallError> {
    if version.trim().is_empty() {
        return Err(InstallError::VersionEmpty);
    }
    {
        let in_progress = state.install_in_progress.lock().unwrap();
        if *in_progress {
            return Err(InstallError::InstallInProgress);
        }
    }
    {
        let mut in_progress = state.install_in_progress.lock().unwrap();
        *in_progress = true;
    }
    let hub_path = resolve_hub_cli_path().ok_or_else(|| {
        reset_install_flag(&state);
        InstallError::HubNotFound
    })?;
    let version_c = version.clone();
    let changeset_c = changeset.clone();
    let app_c = app.clone();
    let install_result =
        tauri::async_runtime::spawn_blocking(move || {
            run_hub_install(&hub_path, &version_c, changeset_c.as_deref(), &app_c)
        })
        .await;
    reset_install_flag(&state);
    match install_result {
        Ok(Ok(())) => {
            let settings = state.settings.lock().unwrap().clone();
            let disc = discovery::discover_unity_installations(&settings);
            {
                let mut cache = state.discovery_cache.lock().unwrap();
                *cache = Some(disc);
            }
            let _ = app.emit("install-complete", &version);
            Ok(InstallResult { version })
        }
        Ok(Err(e)) => Err(e),
        Err(_) => Err(InstallError::InstallFailed {
            message: "install task panicked".to_string(),
        }),
    }
}

fn reset_install_flag(state: &State<'_, AppState>) {
    let mut flag = state.install_in_progress.lock().unwrap();
    *flag = false;
}

fn run_hub_install(
    hub_path: &Path,
    version: &str,
    changeset: Option<&str>,
    app: &tauri::AppHandle,
) -> Result<(), InstallError> {
    let mut cmd = std::process::Command::new(hub_path);
    cmd.arg("--")
        .arg("--headless")
        .arg("install")
        .arg("-v")
        .arg(version);
    if let Some(cs) = changeset {
        cmd.arg("--changeset").arg(cs);
    }
    cmd.stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped());
    let mut child = cmd.spawn().map_err(|e| InstallError::InstallFailed {
        message: format!("failed to spawn Unity Hub CLI: {}", e),
    })?;
    let stderr_lines = {
        let stderr = child.stderr.take().unwrap();
        std::thread::spawn(move || {
            let reader = BufReader::new(stderr);
            let mut lines = Vec::new();
            for line in reader.lines().flatten() {
                lines.push(line);
            }
            lines
        })
    };
    if let Some(stdout) = child.stdout.take() {
        let reader = BufReader::new(stdout);
        for line in reader.lines().flatten() {
            let _ = app.emit("install-log", &line);
        }
    }
    let stderr = stderr_lines.join().unwrap_or_default();
    for line in &stderr {
        let _ = app.emit("install-log", line);
    }
    let status = child.wait().map_err(|e| InstallError::InstallFailed {
        message: format!("failed to wait for Unity Hub CLI: {}", e),
    })?;
    if status.success() {
        Ok(())
    } else {
        let code = status.code().unwrap_or(-1);
        let msg = stderr.join("\n");
        Err(InstallError::InstallFailed {
            message: if msg.trim().is_empty() {
                format!("Unity Hub CLI exited with code {}", code)
            } else {
                msg
            },
        })
    }
}

#[tauri::command]
pub fn check_install_in_progress(state: State<AppState>) -> bool {
    *state.install_in_progress.lock().unwrap()
}

// ── Tests ──────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_release_line_comma_separated() {
        let entry = parse_release_line("6000.0.32f1,abc123,2026-05-14,lts").unwrap();
        assert_eq!(entry.version, "6000.0.32f1");
        assert_eq!(entry.changeset.as_deref(), Some("abc123"));
        assert_eq!(entry.release_date.as_deref(), Some("2026-05-14"));
        assert_eq!(entry.stream, ReleaseStream::Lts);
    }

    #[test]
    fn parse_release_line_whitespace_separated() {
        let entry = parse_release_line("2022.3.62f2 def456 2025-05-29 lts").unwrap();
        assert_eq!(entry.version, "2022.3.62f2");
        assert_eq!(entry.changeset.as_deref(), Some("def456"));
        assert_eq!(entry.stream, ReleaseStream::Lts);
    }

    #[test]
    fn parse_release_line_version_only() {
        let entry = parse_release_line("6000.0.0b16").unwrap();
        assert_eq!(entry.version, "6000.0.0b16");
        assert!(entry.changeset.is_none());
        assert_eq!(entry.stream, ReleaseStream::Beta);
    }

    #[test]
    fn parse_release_line_empty_returns_none() {
        assert!(parse_release_line("").is_none());
        assert!(parse_release_line("   ").is_none());
    }

    #[test]
    fn infer_stream_alpha() {
        assert_eq!(infer_stream("6000.0.0a25"), ReleaseStream::Alpha);
    }

    #[test]
    fn infer_stream_beta() {
        assert_eq!(infer_stream("6000.0.0b16"), ReleaseStream::Beta);
    }

    #[test]
    fn infer_stream_tech_default() {
        assert_eq!(infer_stream("2023.3.20f1"), ReleaseStream::Tech);
    }

    #[test]
    fn parse_stream_variants() {
        assert_eq!(parse_stream("lts"), Some(ReleaseStream::Lts));
        assert_eq!(parse_stream("LTS"), Some(ReleaseStream::Lts));
        assert_eq!(parse_stream("tech"), Some(ReleaseStream::Tech));
        assert_eq!(parse_stream("beta"), Some(ReleaseStream::Beta));
        assert_eq!(parse_stream("alpha"), Some(ReleaseStream::Alpha));
        assert_eq!(parse_stream("unknown"), None);
    }

    #[test]
    fn parse_releases_output_multiple_lines() {
        let output = "6000.0.32f1,abc,2026-05-14,lts\n2022.3.62f2,def,2025-05-29,lts\n";
        let entries = parse_releases_output(output);
        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].version, "6000.0.32f1");
        assert_eq!(entries[1].version, "2022.3.62f2");
    }

    #[test]
    fn parse_releases_output_skips_empty_and_comments() {
        let output = "# header\n\n6000.0.32f1,abc,2026-05-14,lts\n";
        let entries = parse_releases_output(output);
        assert_eq!(entries.len(), 1);
    }

    #[test]
    fn install_error_hub_not_found_serializes() {
        let err = InstallError::HubNotFound;
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"hubNotFound\""));
    }

    #[test]
    fn install_error_install_in_progress_serializes() {
        let err = InstallError::InstallInProgress;
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"installInProgress\""));
    }

    #[test]
    fn install_error_install_failed_serializes() {
        let err = InstallError::InstallFailed {
            message: "network error".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"installFailed\""));
        assert!(json.contains("network error"));
    }

    #[test]
    fn install_result_serializes() {
        let result = InstallResult {
            version: "6000.0.32f1".to_string(),
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(json.contains("\"version\""));
        assert!(json.contains("6000.0.32f1"));
    }

    #[test]
    fn resolve_hub_cli_path_returns_none_when_not_installed() {
        let result = resolve_hub_cli_path();
        if cfg!(target_os = "macos") {
            let exists = std::path::Path::new("/Applications/Unity Hub.app/Contents/MacOS/Unity Hub").exists();
            assert_eq!(result.is_some(), exists);
        }
    }

    #[test]
    fn release_entry_from_cli_has_changeset() {
        let entry = parse_release_line("6000.0.32f1,abc123,2026-05-14,lts").unwrap();
        assert_eq!(entry.changeset.as_deref(), Some("abc123"));
        let json = serde_json::to_string(&entry).unwrap();
        assert!(json.contains("\"changeset\""));
    }

    #[test]
    fn release_entry_without_changeset_omits_field() {
        let entry = parse_release_line("6000.0.32f1").unwrap();
        assert!(entry.changeset.is_none());
        let json = serde_json::to_string(&entry).unwrap();
        assert!(!json.contains("changeset"));
    }
}
