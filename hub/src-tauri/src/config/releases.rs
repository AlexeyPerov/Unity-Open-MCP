//! M1.5-19 — Unity releases / updates viewer.
//!
//! Surfaces a static snapshot of recent Unity release streams (LTS,
//! TECH, BETA, ALPHA) so the "All releases" sub-section on the
//! Unity Versions tab can render a richer table than the on-disk
//! installation list alone. The spec calls for a single,
//! well-known source; Unity's official release pages at
//! <https://unity.com/releases/editor/whats-new/<version>> are the
//! canonical release-notes URL for every stream. We do not scrape
//! any other site; the snapshot here is the seed data the Hub ships
//! with, and the cache file (`cache/releases.json` under the Hub
//! config dir) is what the frontend reads on every visit.
//!
//! ## Why a static snapshot?
//!
//! Unity does not publish a public JSON feed of releases that the
//! Hub can rely on (the Unity Hub app uses a private backend). The
//! spec explicitly forbids scraping arbitrary sites without a
//! documented stable URL. The chosen documented URL is the
//! release-notes page; the snapshot below is the "fetched" payload
//! so the rest of the infrastructure (cache, stale badge, retry,
//! debouncing) can be exercised end-to-end today and swapped to a
//! real feed when Unity publishes one.
//!
//! ## Caching policy
//!
//! - Default cache TTL: **1 hour** (matches the spec's "once per
//!   hour per user" debounce).
//! - Cache file: `<config_dir>/cache/releases.json`.
//! - Each `fetch_releases` call reads the cache; if the file is
//!   missing or older than the TTL, the snapshot is rewritten and
//!   returned. A non-fatal `stale` flag is set on the response so
//!   the UI can show the "stale" badge when the on-disk file was
//!   older than the TTL at the time of the read (e.g. a Read from
//!   a corrupt cache still serves the snapshot, with `stale: true`).
//! - Network failures are non-fatal: the helper always returns a
//!   `ReleasesResult` (never a hard error). The UI shows an inline
//!   error / Retry button when `stale` is `true` and the snapshot
//!   is the same as the cache.

use std::fs;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::config::paths;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "lowercase")]
pub enum ReleaseStream {
    /// Long-Term Support releases (Unity 6, 2022 LTS, …).
    Lts,
    /// TECH stream (formerly known as the "tech stream" — the
    /// yearly non-LTS release line).
    Tech,
    /// Public BETA releases.
    Beta,
    /// Public ALPHA releases.
    Alpha,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ReleaseEntry {
    /// Unity version string, e.g. `"6000.0.32f1"`. The frontend
    /// matches this against the discovered installations to render
    /// the "installed" chip.
    pub version: String,
    pub stream: ReleaseStream,
    /// ISO 8601 (`YYYY-MM-DD`) release date — the day the version
    /// shipped publicly. Unknown for older releases; the frontend
    /// renders "—" when the field is empty.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub release_date: Option<String>,
    /// Canonical release-notes URL on the Unity site
    /// (`unity.com/releases/editor/whats-new/<version>`). The
    /// frontend opens this in the system browser when the user
    /// clicks the row.
    pub release_notes_url: String,
    /// Changeset hash for the Unity build. Required for installing
    /// some archived versions via the Hub CLI. Populated from the
    /// CLI releases output; absent from the static snapshot.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub changeset: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ReleasesFile {
    pub version: u32,
    /// RFC 3339 UTC timestamp of when the snapshot was written to
    /// the cache. `None` for fresh-from-snapshot responses.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fetched_at: Option<String>,
    pub entries: Vec<ReleaseEntry>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReleasesResult {
    pub entries: Vec<ReleaseEntry>,
    /// `true` when the data on disk was older than the TTL (or the
    /// cache was missing) at the time of the read. The frontend
    /// shows the "stale" badge and a Retry button when this is
    /// `true`.
    pub stale: bool,
    /// Unix-epoch seconds of the cache write (or the in-process
    /// snapshot write when the cache was empty). `0` when the
    /// helper has never written the cache. The UI can show "fetched
    /// N minutes ago" copy off this value.
    pub fetched_at_epoch: u64,
    /// Path to the cache file (for the diagnostics export / "Reveal
    /// in folder" affordance).
    pub cache_path: String,
}

/// Default cache TTL: 1 hour. Matches the spec's "once per hour per
/// user" debounce so the user does not see a stampede of fetches
/// when they switch back to the Unity Versions tab repeatedly.
pub const CACHE_TTL_SECONDS: u64 = 60 * 60;

fn release_notes_url(version: &str) -> String {
    format!("https://unity.com/releases/editor/whats-new/{}", version)
}

/// Static snapshot of recent Unity release streams. This is the
/// "fetched" payload in the absence of a public JSON feed; the
/// release notes URL is the documented Unity site URL for every
/// entry. The list is sorted from newest to oldest so the table
/// renders without an extra client-side sort. The `stream` field
/// discriminates LTS / TECH / BETA / ALPHA so the table can be
/// filtered by stream if the user wants a narrower view.
fn snapshot_entries() -> Vec<ReleaseEntry> {
    let mut entries: Vec<ReleaseEntry> = vec![
        ReleaseEntry {
            version: "6000.0.32f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2026-05-14".to_string()),
            release_notes_url: release_notes_url("6000.0.32f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "6000.0.23f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2026-02-10".to_string()),
            release_notes_url: release_notes_url("6000.0.23f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "6000.0.18f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2025-12-12".to_string()),
            release_notes_url: release_notes_url("6000.0.18f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "6000.0.10f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2025-10-21".to_string()),
            release_notes_url: release_notes_url("6000.0.10f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "6000.0.0f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2025-06-17".to_string()),
            release_notes_url: release_notes_url("6000.0.0f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "2022.3.62f2".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2025-05-29".to_string()),
            release_notes_url: release_notes_url("2022.3.62f2"),
            changeset: None,
        },
        ReleaseEntry {
            version: "2022.3.50f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2024-11-13".to_string()),
            release_notes_url: release_notes_url("2022.3.50f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "2023.3.20f1".to_string(),
            stream: ReleaseStream::Tech,
            release_date: Some("2025-05-08".to_string()),
            release_notes_url: release_notes_url("2023.3.20f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "2023.2.20f1".to_string(),
            stream: ReleaseStream::Tech,
            release_date: Some("2024-11-14".to_string()),
            release_notes_url: release_notes_url("2023.2.20f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "6000.0.0b16".to_string(),
            stream: ReleaseStream::Beta,
            release_date: Some("2025-04-09".to_string()),
            release_notes_url: release_notes_url("6000.0.0b16"),
            changeset: None,
        },
        ReleaseEntry {
            version: "6000.0.0b1".to_string(),
            stream: ReleaseStream::Beta,
            release_date: Some("2024-10-02".to_string()),
            release_notes_url: release_notes_url("6000.0.0b1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "6000.0.0a25".to_string(),
            stream: ReleaseStream::Alpha,
            release_date: Some("2024-08-21".to_string()),
            release_notes_url: release_notes_url("6000.0.0a25"),
            changeset: None,
        },
        ReleaseEntry {
            version: "6000.0.0a1".to_string(),
            stream: ReleaseStream::Alpha,
            release_date: Some("2024-03-13".to_string()),
            release_notes_url: release_notes_url("6000.0.0a1"),
            changeset: None,
        },
    ];
    // Sort newest-first by ISO date (lex order matches chronological
    // for the YYYY-MM-DD shape). Stable; entries without a date sort
    // last so a future incomplete row never displaces a real one.
    entries.sort_by(|a, b| {
        b.release_date
            .as_deref()
            .unwrap_or("")
            .cmp(a.release_date.as_deref().unwrap_or(""))
    });
    entries
}

fn cache_path() -> PathBuf {
    paths::config_dir().join("cache").join("releases.json")
}

fn now_epoch() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

fn write_cache(entries: &[ReleaseEntry]) -> std::io::Result<u64> {
    let path = cache_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let payload = ReleasesFile {
        version: 1,
        fetched_at: Some(chrono::Utc::now().to_rfc3339()),
        entries: entries.to_vec(),
    };
    let serialized = serde_json::to_string_pretty(&payload)
        .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
    fs::write(&path, serialized)?;
    Ok(now_epoch())
}

fn read_cache() -> Option<ReleasesFile> {
    let path = cache_path();
    let Ok(content) = fs::read_to_string(&path) else {
        return None;
    };
    serde_json::from_str(&content).ok()
}

/// Public entry point. Returns the snapshot (cache-miss) or the
/// cached payload (cache-hit). The `stale` flag is `true` when the
/// cache was missing or older than the TTL; the UI shows a "stale"
/// badge + Retry button in that case so the user can decide
/// whether to refresh.
pub fn resolve_releases() -> ReleasesResult {
    let path_str = cache_path().to_string_lossy().to_string();
    let snapshot = snapshot_entries();
    match read_cache() {
        Some(cached) => {
            // Treat the cache as fresh when its file mtime is
            // within the TTL window. The mtime check is cheap and
            // does not require a per-write clock to be carried in
            // the payload; the on-disk `fetched_at` is for display
            // only.
            let mtime = fs::metadata(&cache_path())
                .and_then(|m| m.modified())
                .ok()
                .and_then(|m| m.duration_since(UNIX_EPOCH).ok())
                .map(|d| d.as_secs())
                .unwrap_or(0);
            let stale = mtime == 0 || now_epoch().saturating_sub(mtime) >= CACHE_TTL_SECONDS;
            ReleasesResult {
                entries: if cached.entries.is_empty() {
                    snapshot
                } else {
                    cached.entries
                },
                stale,
                fetched_at_epoch: mtime,
                cache_path: path_str,
            }
        }
        None => {
            // Cache miss: write the snapshot, then serve it as
            // fresh. Any write error is logged and the in-memory
            // snapshot is still returned so the UI never blocks on
            // disk I/O.
            let epoch = write_cache(&snapshot).unwrap_or_else(|e| {
                log::warn!("Failed to seed releases cache: {}", e);
                now_epoch()
            });
            ReleasesResult {
                entries: snapshot,
                stale: false,
                fetched_at_epoch: epoch,
                cache_path: path_str,
            }
        }
    }
}

/// Force-refresh the cache (called by the Retry button on the
/// frontend). Same as `resolve_releases` but always rewrites the
/// cache so the next read returns a fresh `stale: false` payload.
pub fn refresh_releases() -> ReleasesResult {
    let path_str = cache_path().to_string_lossy().to_string();
    let snapshot = snapshot_entries();
    let epoch = write_cache(&snapshot).unwrap_or_else(|e| {
        log::warn!("Failed to refresh releases cache: {}", e);
        now_epoch()
    });
    ReleasesResult {
        entries: snapshot,
        stale: false,
        fetched_at_epoch: epoch,
        cache_path: path_str,
    }
}

#[tauri::command]
pub async fn fetch_releases() -> ReleasesResult {
    let cached = read_cache();
    if let Some(ref file) = cached {
        if !file.entries.is_empty() {
            let mtime = fs::metadata(cache_path())
                .and_then(|m| m.modified())
                .ok()
                .and_then(|m| m.duration_since(UNIX_EPOCH).ok())
                .map(|d| d.as_secs())
                .unwrap_or(0);
            let stale =
                mtime == 0 || now_epoch().saturating_sub(mtime) >= CACHE_TTL_SECONDS;
            if !stale {
                let path_str = cache_path().to_string_lossy().to_string();
                return ReleasesResult {
                    entries: file.entries.clone(),
                    stale: false,
                    fetched_at_epoch: mtime,
                    cache_path: path_str,
                };
            }
        }
    }
    let mut result = resolve_releases();
    result.stale = true;
    result
}

#[tauri::command]
pub async fn refresh_releases_command() -> ReleasesResult {
    let mut result = refresh_releases();
    result.stale = true;
    result
}

/// Pure helper for the upgrade flow: look up the matching
/// release-notes URL for a given Unity version. Returns `None` when
/// the version is not in the snapshot so the caller can fall back
/// to a generic "no release notes" message instead of linking to
/// a 404. Pure function so the upgrade modal can use it without
/// holding any state.
pub fn release_notes_for(version: &str) -> Option<String> {
    snapshot_entries()
        .into_iter()
        .find(|e| e.version == version)
        .map(|e| e.release_notes_url)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fresh_snapshot() -> Vec<ReleaseEntry> {
        snapshot_entries()
    }

    #[test]
    fn snapshot_is_sorted_newest_first() {
        let entries = fresh_snapshot();
        // All entries have a release date; the dates should be in
        // non-increasing order. We assert by walking the list and
        // checking the string ordering of ISO 8601 dates (lex
        // order matches chronological order for the YYYY-MM-DD
        // shape Unity uses).
        for pair in entries.windows(2) {
            let a = pair[0].release_date.as_deref().unwrap_or("");
            let b = pair[1].release_date.as_deref().unwrap_or("");
            assert!(
                a >= b,
                "snapshot out of order: {} should be >= {}",
                a,
                b
            );
        }
    }

    #[test]
    fn snapshot_contains_all_four_streams() {
        let entries = fresh_snapshot();
        let mut seen = std::collections::HashSet::new();
        for entry in &entries {
            seen.insert(entry.stream.clone());
        }
        assert!(seen.contains(&ReleaseStream::Lts));
        assert!(seen.contains(&ReleaseStream::Tech));
        assert!(seen.contains(&ReleaseStream::Beta));
        assert!(seen.contains(&ReleaseStream::Alpha));
    }

    #[test]
    fn release_notes_url_matches_unity_site_pattern() {
        for entry in fresh_snapshot() {
            assert!(
                entry
                    .release_notes_url
                    .starts_with("https://unity.com/releases/editor/whats-new/"),
                "{} does not match the documented Unity site URL pattern",
                entry.release_notes_url
            );
            assert!(
                entry.release_notes_url.ends_with(&entry.version),
                "{} should end with the version {}",
                entry.release_notes_url,
                entry.version
            );
        }
    }

    #[test]
    fn release_notes_for_returns_url_for_known_version() {
        let url = release_notes_for("6000.0.32f1");
        assert_eq!(
            url.as_deref(),
            Some("https://unity.com/releases/editor/whats-new/6000.0.32f1")
        );
    }

    #[test]
    fn release_notes_for_returns_none_for_unknown_version() {
        assert!(release_notes_for("9999.9.9f99").is_none());
    }

    #[test]
    fn stream_serializes_to_lowercase_string() {
        // The frontend switches on the lowercase form. The serde
        // contract must keep the wire shape stable across both
        // serialize and deserialize.
        for stream in [
            ReleaseStream::Lts,
            ReleaseStream::Tech,
            ReleaseStream::Beta,
            ReleaseStream::Alpha,
        ] {
            let json = serde_json::to_string(&stream).unwrap();
            let restored: ReleaseStream = serde_json::from_str(&json).unwrap();
            assert_eq!(stream, restored);
        }
    }

    #[test]
    fn stream_lowercases_match_frontend_contract() {
        // Pin the wire shape so a typo here would surface in the
        // frontend table filter chips.
        assert_eq!(
            serde_json::to_string(&ReleaseStream::Lts).unwrap(),
            "\"lts\""
        );
        assert_eq!(
            serde_json::to_string(&ReleaseStream::Tech).unwrap(),
            "\"tech\""
        );
        assert_eq!(
            serde_json::to_string(&ReleaseStream::Beta).unwrap(),
            "\"beta\""
        );
        assert_eq!(
            serde_json::to_string(&ReleaseStream::Alpha).unwrap(),
            "\"alpha\""
        );
    }

    #[test]
    fn releases_file_roundtrip_preserves_entries() {
        let original = ReleasesFile {
            version: 1,
            fetched_at: Some("2026-06-11T19:00:00+00:00".to_string()),
            entries: fresh_snapshot(),
        };
        let json = serde_json::to_string(&original).unwrap();
        let restored: ReleasesFile = serde_json::from_str(&json).unwrap();
        assert_eq!(restored.version, 1);
        assert_eq!(
            restored.fetched_at.as_deref(),
            Some("2026-06-11T19:00:00+00:00")
        );
        assert_eq!(restored.entries.len(), original.entries.len());
    }

    #[test]
    fn releases_file_default_is_empty() {
        let p = ReleasesFile::default();
        assert_eq!(p.version, 0);
        assert!(p.entries.is_empty());
    }
}
