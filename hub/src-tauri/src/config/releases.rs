//! Unity releases / updates viewer.
//!
//! Surfaces the live Unity Editor release catalog so the "All releases"
//! sub-section on the Unity Versions tab can show every release Unity
//! publishes — LTS, SUPPORTED, TECH, BETA, ALPHA — with the right stream
//! label, release date, and changeset (the latter is required to install
//! archived versions via the Unity Hub CLI).
//!
//! ## Source of truth
//!
//! Unity does not publish a stable public JSON feed of releases. The old
//! `public-cdn.cloud.unity3d.com/hub/prod/releases*.json` endpoints 404,
//! and Unity Hub's own catalog is fetched from a private authenticated
//! GraphQL service. The most stable public source is the server-rendered
//! download archive page at
//! <https://unity.com/releases/editor/archive>. That page embeds the
//! result of Unity's `getUnityReleases` GraphQL query directly in a
//! Next.js RSC payload — the full catalog (~245 entries as of 2026), no
//! auth required. Each node carries the authoritative `stream` field
//! (`LTS` / `SUPPORTED` / `TECH` / `BETA` / `ALPHA`), an ISO release
//! date, and a `unityHubDeepLink` of the form `unityhub://<version>/
//! <changeset>` from which the install changeset is extracted.
//!
//! ## Caching policy
//!
//! - Default cache TTL: **1 hour** (debounce so repeated tab switches do
//!   not stampede the network).
//! - Cache file: `<config_dir>/cache/releases.json`.
//! - `fetch_releases` reads the cache; if the file is missing or older
//!   than the TTL, it triggers a live network fetch. If the fetch fails
//!   (offline, parse error, …), the snapshot fallback is served and
//!   `stale: true` is returned so the UI shows the "stale" badge + Retry.
//! - `refresh_releases_command` (the Retry button) always re-fetches
//!   live and rewrites the cache.
//! - Network failures are non-fatal: every entry point always returns a
//!   `ReleasesResult`, never a hard error.

use std::fs;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::config::paths;
use crate::config::constants::RELEASE_NOTES_URL_PREFIX;

/// Public download-archive page. Server-renders the full Unity release
/// catalog as a Next.js RSC payload (see module docs). Used as the live
/// source of truth because no stable public JSON feed exists.
const ARCHIVE_URL: &str = "https://unity.com/releases/editor/archive";
/// HTTP timeout for the live fetch. Generous because the archive page
/// is large (~300 KB) and the network may be slow; the caller runs this
/// inside `spawn_blocking` so we never block the UI thread.
const FETCH_TIMEOUT_SECS: u64 = 20;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "lowercase")]
pub enum ReleaseStream {
    /// Long-Term Support releases (Unity 6.3 LTS, 2022.3 LTS, …).
    Lts,
    /// Active stable release line that is not yet LTS — the newest
    /// supported engine release (Unity 6.4, 6.5, …). Surfaced by Unity's
    /// archive feed as `SUPPORTED`.
    Supported,
    /// TECH stream (formerly known as the "tech stream" — the yearly
    /// non-LTS release line; e.g. Unity 6.0.x is now classified TECH).
    Tech,
    /// Public BETA releases.
    Beta,
    /// Public ALPHA releases.
    Alpha,
}

impl ReleaseStream {
    /// Map the raw `stream` string from Unity's `getUnityReleases`
    /// GraphQL response to our enum. Unknown values fall back to `Tech`
    /// (a conservative "stable-ish" default) so a future Unity string we
    /// have not seen yet still renders with a sane tone rather than
    /// dropping the row.
    fn from_unity_str(raw: &str) -> Self {
        match raw {
            "LTS" => ReleaseStream::Lts,
            "SUPPORTED" => ReleaseStream::Supported,
            "TECH" => ReleaseStream::Tech,
            "BETA" => ReleaseStream::Beta,
            "ALPHA" => ReleaseStream::Alpha,
            _ => ReleaseStream::Tech,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ReleaseEntry {
    /// Unity version string, e.g. `"6000.3.18f1"`. The frontend matches
    /// this against the discovered installations to render the
    /// "installed" chip.
    pub version: String,
    pub stream: ReleaseStream,
    /// ISO 8601 (`YYYY-MM-DD`) release date — the day the version
    /// shipped publicly. Normalized to the date portion of the
    /// `releaseDate` timestamp the archive feed returns.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub release_date: Option<String>,
    /// Canonical release-notes URL on the Unity site
    /// (`unity.com/releases/editor/whats-new/<version>`). The frontend
    /// opens this in the system browser when the user clicks the row.
    pub release_notes_url: String,
    /// Changeset hash for the Unity build. Required for installing some
    /// archived versions via the Hub CLI. Extracted from the
    /// `unityHubDeepLink` (`unityhub://<version>/<changeset>`) the
    /// archive feed exposes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub changeset: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ReleasesFile {
    pub version: u32,
    /// RFC 3339 UTC timestamp of when the snapshot was written to the
    /// cache. `None` for fresh-from-snapshot responses.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fetched_at: Option<String>,
    pub entries: Vec<ReleaseEntry>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReleasesResult {
    pub entries: Vec<ReleaseEntry>,
    /// `true` when the data served is stale — either the on-disk cache
    /// was older than the TTL, or the live fetch failed and we fell
    /// back to the bundled snapshot. The frontend shows the "stale"
    /// badge and a Retry button when this is `true`.
    pub stale: bool,
    /// Unix-epoch seconds of the cache write (or the in-process
    /// snapshot write when the cache was empty). `0` when the helper
    /// has never written the cache. The UI can show "fetched N minutes
    /// ago" copy off this value.
    pub fetched_at_epoch: u64,
    /// Path to the cache file (for the diagnostics export / "Reveal in
    /// folder" affordance).
    pub cache_path: String,
}

/// Default cache TTL: 1 hour. Debounces repeated tab switches so the
/// user does not see a stampede of fetches.
pub const CACHE_TTL_SECONDS: u64 = 60 * 60;

fn release_notes_url(version: &str) -> String {
    format!("{}{}", RELEASE_NOTES_URL_PREFIX, version)
}

// ── Snapshot (offline fallback) ────────────────────────────────────

/// Bundled snapshot of recent Unity release streams. This is **not** the
/// primary source — it is the offline / network-failure fallback served
/// when the live archive fetch fails on a cold cache. Kept deliberately
/// small (a recent representative cross-stream sample) and corrected to
/// use the right streams so we never mislabel `6000.0.x` as LTS. The
/// list is sorted newest-first so the table renders without an extra
/// client-side sort.
fn snapshot_entries() -> Vec<ReleaseEntry> {
    let mut entries: Vec<ReleaseEntry> = vec![
        ReleaseEntry {
            version: "6000.4.12f1".to_string(),
            stream: ReleaseStream::Supported,
            release_date: Some("2026-06-17".to_string()),
            release_notes_url: release_notes_url("6000.4.12f1"),
            changeset: Some("3ca267ce8005".to_string()),
        },
        ReleaseEntry {
            version: "6000.3.18f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2026-06-17".to_string()),
            release_notes_url: release_notes_url("6000.3.18f1"),
            changeset: Some("5ebeb53e4c07".to_string()),
        },
        ReleaseEntry {
            version: "6000.5.0f1".to_string(),
            stream: ReleaseStream::Supported,
            release_date: Some("2026-06-15".to_string()),
            release_notes_url: release_notes_url("6000.5.0f1"),
            changeset: Some("88b47c5e7076".to_string()),
        },
        ReleaseEntry {
            version: "6000.0.32f1".to_string(),
            stream: ReleaseStream::Tech,
            release_date: Some("2026-05-14".to_string()),
            release_notes_url: release_notes_url("6000.0.32f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "6000.3.10f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2026-02-25".to_string()),
            release_notes_url: release_notes_url("6000.3.10f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "6000.3.0f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2025-12-04".to_string()),
            release_notes_url: release_notes_url("6000.3.0f1"),
            changeset: None,
        },
        ReleaseEntry {
            version: "2022.3.62f2".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2025-10-03".to_string()),
            release_notes_url: release_notes_url("2022.3.62f2"),
            changeset: Some("7670c08855a9".to_string()),
        },
    ];
    entries.sort_by(|a, b| {
        b.release_date
            .as_deref()
            .unwrap_or("")
            .cmp(a.release_date.as_deref().unwrap_or(""))
    });
    entries
}

// ── Legacy LTS (curated, always-merged) ────────────────────────────

/// Curated older Unity LTS releases that Unity's public archive
/// `getUnityReleases` feed no longer surfaces (Unity now publishes only
/// Unity 6 / `6000.x` in that feed). These versions are still hosted on
/// Unity's download archive and fully installable via the Hub deep-link
/// protocol, so we merge them into every result to keep them reachable
/// from the Install tab.
///
/// Scoped to **2022.3 LTS** — the only year-named LTS in the 2022–2023
/// range. Unity never shipped a 2023.x LTS: 2023.1/2023.2 were
/// Tech-stream only, and 2023.3 was renamed to Unity 6 before release.
/// Each entry's `changeset` is verified against Unity's official
/// release-notes pages (`/releases/editor/whats-new/<version>`).
fn legacy_lts_entries() -> Vec<ReleaseEntry> {
    vec![
        ReleaseEntry {
            version: "2022.3.62f3".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2025-10-28".to_string()),
            release_notes_url: release_notes_url("2022.3.62f3"),
            changeset: Some("96770f904ca7".to_string()),
        },
        ReleaseEntry {
            version: "2022.3.62f2".to_string(),
            stream: ReleaseStream::Lts,
            // CVE-2025-59489 security hotfix.
            release_date: Some("2025-10-03".to_string()),
            release_notes_url: release_notes_url("2022.3.62f2"),
            changeset: Some("7670c08855a9".to_string()),
        },
        ReleaseEntry {
            version: "2022.3.62f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2025-05-07".to_string()),
            release_notes_url: release_notes_url("2022.3.62f1"),
            changeset: Some("4af31df58517".to_string()),
        },
        ReleaseEntry {
            version: "2022.3.60f1".to_string(),
            stream: ReleaseStream::Lts,
            // MbedTLS 3.6 upgrade — TLS 1.0/1.1 support removed.
            release_date: Some("2025-03-12".to_string()),
            release_notes_url: release_notes_url("2022.3.60f1"),
            changeset: Some("5f63fdee6d95".to_string()),
        },
    ]
}

/// Merge [`legacy_lts_entries`] into `entries`, deduplicating by
/// `version` (the first/live occurrence wins — so if Unity's feed ever
/// re-adds one of these versions, the live entry supersedes the
/// curated one) and re-sorting newest-first by `release_date`. Applied
/// at every `ReleasesResult` construction point so legacy LTS is present
/// in the Install tab regardless of cache state or network outcome.
fn merge_legacy_lts(mut entries: Vec<ReleaseEntry>) -> Vec<ReleaseEntry> {
    let mut seen: std::collections::HashSet<String> =
        entries.iter().map(|e| e.version.clone()).collect();
    for legacy in legacy_lts_entries() {
        if seen.insert(legacy.version.clone()) {
            entries.push(legacy);
        }
    }
    entries.sort_by(|a, b| {
        b.release_date
            .as_deref()
            .unwrap_or("")
            .cmp(a.release_date.as_deref().unwrap_or(""))
    });
    entries
}

// ── Live fetch (archive page RSC payload parser) ───────────────────

/// Wire shape of a single `node` inside the `getUnityReleases` GraphQL
/// response embedded in the archive page. Mirrors exactly the fields
/// Unity publishes; everything we do not read is ignored.
#[derive(Debug, Clone, Deserialize)]
struct UnityReleaseNode {
    version: String,
    #[serde(default, rename = "releaseDate")]
    release_date: Option<String>,
    /// `unityhub://<version>/<changeset>`. Used both as the changeset
    /// source and (historically) to launch the Hub. Parsed with
    /// [`extract_changeset`].
    #[serde(default, rename = "unityHubDeepLink")]
    unity_hub_deep_link: Option<String>,
    stream: String,
}

#[derive(Debug, Clone, Deserialize)]
struct UnityReleaseEdges {
    #[serde(default)]
    edges: Vec<UnityReleaseEdge>,
}

#[derive(Debug, Clone, Deserialize)]
struct UnityReleaseEdge {
    node: UnityReleaseNode,
}

#[derive(Debug, Clone, Deserialize)]
struct UnityReleaseResponse {
    #[serde(rename = "getUnityReleases")]
    get_unity_releases: UnityReleaseEdges,
}

/// Extract the changeset hash from a `unityhub://<version>/<changeset>`
/// deep link. Returns `None` if the link is missing or malformed (some
/// older releases in the feed ship without a deep link).
fn extract_changeset(deep_link: Option<&str>) -> Option<String> {
    let link = deep_link?;
    let after_scheme = link.strip_prefix("unityhub://")?;
    // The path is `<version>/<changeset>`; the changeset is the last
    // segment. Split from the right so a version containing a slash
    // (none observed, but defensive) still resolves.
    let cs = after_scheme.rsplit('/').next()?;
    if cs.is_empty() {
        None
    } else {
        Some(cs.to_string())
    }
}

/// Normalize the archive feed's full ISO timestamp
/// (`2026-06-17T15:09:23.805Z`) down to the date portion the UI shows
/// (`2026-06-17`). Passes through values that do not look like an ISO
/// date unchanged so a future format change never blanks the column.
fn normalize_release_date(raw: &Option<String>) -> Option<String> {
    let s = raw.as_ref()?;
    if s.len() >= 10 && s.as_bytes()[4] == b'-' && s.as_bytes()[7] == b'-' {
        Some(s[..10].to_string())
    } else {
        Some(s.clone())
    }
}

/// Parse the Next.js RSC payload embedded in the archive page HTML and
/// return the list of releases. Public so it can be unit-tested against
/// a captured fixture without touching the network.
///
/// The archive page emits the GraphQL result as one
/// `self.__next_f.push([1,"31:<json>"])` script segment, where `<json>`
/// is a JSON-string-escaped object whose `getUnityReleases.edges` array
/// holds the release nodes. We locate that segment, JSON-decode the
/// string literal once (un-escaping the inner JSON), strip the `31:`
/// RSC prefix, and deserialize the result.
pub fn parse_archive_payload(html: &str) -> Option<Vec<ReleaseEntry>> {
    // Scan every `self.__next_f.push([1,"…"])` segment and pick the one
    // whose decoded body starts with `31:` and contains the releases
    // payload. The page emits many such segments; only one carries the
    // catalog, so we short-circuit on the first match.
    let mut i = 0;
    while let Some(open) = html[i..].find("self.__next_f.push([1,\"") {
        let abs = i + open;
        let start = abs + "self.__next_f.push([1,\"".len();
        // Find the closing `"])` for this segment. The segment body is a
        // JSON string literal, so a naive search for `"` would match
        // escaped quotes; walk character pairs instead.
        let Some(end) = find_segment_close(&html[start..]) else {
            break;
        };
        let raw_literal = &html[start..start + end];
        i = start + end;
        // Decode the JSON string literal (handles \" \\ \n …).
        let decoded: String = serde_json::from_str(&format!("\"{}\"", raw_literal)).ok()?;
        let Some(json_str) = decoded.strip_prefix("31:") else {
            continue;
        };
        if !json_str.contains("getUnityReleases") {
            continue;
        }
        let response: UnityReleaseResponse = serde_json::from_str(json_str).ok()?;
        let mut entries: Vec<ReleaseEntry> = response
            .get_unity_releases
            .edges
            .into_iter()
            .map(|e| {
                let node = e.node;
                ReleaseEntry {
                    changeset: extract_changeset(node.unity_hub_deep_link.as_deref()),
                    release_date: normalize_release_date(&node.release_date),
                    release_notes_url: release_notes_url(&node.version),
                    stream: ReleaseStream::from_unity_str(&node.stream),
                    version: node.version,
                }
            })
            .collect();
        // Sort newest-first by date; stable so entries sharing a date
        // keep feed order. Entries without a date sort last.
        entries.sort_by(|a, b| {
            b.release_date
                .as_deref()
                .unwrap_or("")
                .cmp(a.release_date.as_deref().unwrap_or(""))
        });
        return Some(entries);
    }
    None
}

/// Find the index of the closing `"` of a JSON string literal that
/// starts at offset 0 of `s` (i.e. `s` begins with the literal body,
/// not the opening quote). Returns the index of the closing quote so
/// `&s[..idx]` is the body. Honors `\"` escapes.
fn find_segment_close(s: &str) -> Option<usize> {
    let bytes = s.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'\\' {
            // Skip the escaped char (handles \" \\ \n \uXXXX … — the
            // latter is multiple chars but the \u is one byte we step
            // past; the following 4 hex digits are inert).
            i += 2;
            continue;
        }
        if bytes[i] == b'"' {
            return Some(i);
        }
        i += 1;
    }
    None
}

/// Fetch the archive page over HTTP and parse it. Returns `None` on any
/// network or parse failure; the caller falls back to the snapshot.
fn fetch_live_releases_sync() -> Option<Vec<ReleaseEntry>> {
    let start = std::time::Instant::now();
    // Separate connect timeout (5s) from the total timeout (20s): a hung
    // TCP connect (corporate proxy silently dropping the SYN, dead DNS)
    // would otherwise burn the whole 20s budget before we fall back to
    // the snapshot. With this, a dead connect fails fast.
    let agent = ureq::AgentBuilder::new()
        .timeout(std::time::Duration::from_secs(FETCH_TIMEOUT_SECS))
        .timeout_connect(std::time::Duration::from_secs(5))
        .build();
    let resp = agent
        .get(ARCHIVE_URL)
        .set("User-Agent", concat!("UnityHubPro/", env!("CARGO_PKG_VERSION")))
        .call()
        .ok()?;
    let body = resp.into_string().ok()?;
    let parsed = parse_archive_payload(&body)?;
    log::info!(
        "fetch_live_releases: {} entries in {}ms",
        parsed.len(),
        start.elapsed().as_millis()
    );
    if parsed.is_empty() {
        None
    } else {
        Some(parsed)
    }
}

// ── Cache I/O ──────────────────────────────────────────────────────

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

fn cache_is_stale() -> bool {
    let mtime = fs::metadata(cache_path())
        .and_then(|m| m.modified())
        .ok()
        .and_then(|m| m.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_secs())
        .unwrap_or(0);
    mtime == 0 || now_epoch().saturating_sub(mtime) >= CACHE_TTL_SECONDS
}

/// Fetch live releases on a blocking thread (network I/O must not run on
/// the Tauri async runtime). Falls back to the bundled snapshot on any
/// failure. Async because the network call is awaited via
/// [`spawn_blocking`]; callers are the async Tauri commands.
async fn fetch_or_snapshot_async() -> (Vec<ReleaseEntry>, bool) {
    match tauri::async_runtime::spawn_blocking(fetch_live_releases_sync).await {
        Ok(Some(entries)) => (merge_legacy_lts(entries), false),
        // Network/parse failure: serve the snapshot and flag stale so
        // the UI shows the Retry affordance.
        Ok(None) => (merge_legacy_lts(snapshot_entries()), true),
        Err(e) => {
            log::warn!("releases fetch task failed: {}", e);
            (merge_legacy_lts(snapshot_entries()), true)
        }
    }
}

// ── Public entry points ────────────────────────────────────────────

/// Read the cache. If it is fresh (within the TTL), serve it as
/// non-stale. Otherwise trigger a live fetch and rewrite the cache; on
/// fetch failure, serve the snapshot with `stale: true`. Async because a
/// cold/stale cache performs network I/O on a blocking thread.
pub async fn resolve_releases() -> ReleasesResult {
    let path_str = cache_path().to_string_lossy().to_string();
    if let Some(cached) = read_cache() {
        if !cached.entries.is_empty() && !cache_is_stale() {
            let mtime = fs::metadata(cache_path())
                .and_then(|m| m.modified())
                .ok()
                .and_then(|m| m.duration_since(UNIX_EPOCH).ok())
                .map(|d| d.as_secs())
                .unwrap_or(0);
            return ReleasesResult {
                entries: merge_legacy_lts(cached.entries),
                stale: false,
                fetched_at_epoch: mtime,
                cache_path: path_str,
            };
        }
    }
    // Cold or stale cache → live fetch.
    let (entries, fallback) = fetch_or_snapshot_async().await;
    let epoch = write_cache(&entries).unwrap_or_else(|e| {
        log::warn!("Failed to write releases cache: {}", e);
        now_epoch()
    });
    ReleasesResult {
        entries,
        // `stale` only when we served the fallback snapshot — a
        // successful live fetch on a cold cache is fresh.
        stale: fallback,
        fetched_at_epoch: epoch,
        cache_path: path_str,
    }
}

/// Force-refresh (the Retry button). Always re-fetches live and rewrites
/// the cache; falls back to the snapshot only on failure.
pub async fn refresh_releases() -> ReleasesResult {
    let path_str = cache_path().to_string_lossy().to_string();
    let (entries, fallback) = fetch_or_snapshot_async().await;
    let epoch = write_cache(&entries).unwrap_or_else(|e| {
        log::warn!("Failed to refresh releases cache: {}", e);
        now_epoch()
    });
    ReleasesResult {
        entries,
        stale: fallback,
        fetched_at_epoch: epoch,
        cache_path: path_str,
    }
}

#[tauri::command]
pub async fn fetch_releases() -> ReleasesResult {
    let cached = read_cache();
    if let Some(file) = cached {
        if !file.entries.is_empty() && !cache_is_stale() {
            let mtime = fs::metadata(cache_path())
                .and_then(|m| m.modified())
                .ok()
                .and_then(|m| m.duration_since(UNIX_EPOCH).ok())
                .map(|d| d.as_secs())
                .unwrap_or(0);
            let path_str = cache_path().to_string_lossy().to_string();
            return ReleasesResult {
                entries: merge_legacy_lts(file.entries),
                stale: false,
                fetched_at_epoch: mtime,
                cache_path: path_str,
            };
        }
    }
    // Stale or missing → resolve (which may fetch live) and surface the
    // result. We mark `stale: true` here so the UI shows the badge until
    // the user explicitly hits Retry and gets a confirmed-fresh fetch —
    // matches the original UX contract.
    let mut result = resolve_releases().await;
    result.stale = true;
    result
}

#[tauri::command]
pub async fn refresh_releases_command() -> ReleasesResult {
    // The Retry button: a confirmed live fetch. The result is `stale:
    // true` only if the live fetch genuinely failed and we fell back.
    refresh_releases().await
}

/// Pure helper for the upgrade flow: look up the matching release-notes
/// URL for a given Unity version. Returns `None` when the version is not
/// in the snapshot so the caller can fall back to a generic "no release
/// notes" message instead of linking to a 404. Pure function so the
/// upgrade modal can use it without holding any state.
pub fn release_notes_for(version: &str) -> Option<String> {
    snapshot_entries()
        .into_iter()
        .find(|e| e.version == version)
        .map(|e| e.release_notes_url)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture_html() -> String {
        // Captured from a live fetch of
        // https://unity.com/releases/editor/archive and trimmed to a
        // representative cross-stream sample (all 5 streams). Lives at
        // tests/fixtures/archive_page.html so the parser is exercised
        // against the real Next.js RSC payload shape.
        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("tests/fixtures/archive_page.html");
        std::fs::read_to_string(&path)
            .expect("archive_page.html fixture must be present")
    }

    #[test]
    fn snapshot_is_sorted_newest_first() {
        let entries = snapshot_entries();
        for pair in entries.windows(2) {
            let a = pair[0].release_date.as_deref().unwrap_or("");
            let b = pair[1].release_date.as_deref().unwrap_or("");
            assert!(a >= b, "snapshot out of order: {} should be >= {}", a, b);
        }
    }

    #[test]
    fn snapshot_does_not_mislabel_6000_0_as_lts() {
        // Regression guard: the old snapshot marked 6000.0.x as LTS,
        // which Unity's feed classifies as TECH. The bundled snapshot
        // must reflect the corrected stream.
        let entries = snapshot_entries();
        for e in &entries {
            if e.version.starts_with("6000.0.") {
                assert_ne!(
                    e.stream,
                    ReleaseStream::Lts,
                    "{} must not be LTS",
                    e.version
                );
            }
        }
        // The snapshot must include at least one real LTS line.
        assert!(
            entries.iter().any(|e| e.stream == ReleaseStream::Lts),
            "snapshot should contain at least one LTS entry"
        );
    }

    #[test]
    fn release_notes_url_matches_unity_site_pattern() {
        for entry in snapshot_entries() {
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
        let url = release_notes_for("6000.3.18f1");
        assert_eq!(
            url.as_deref(),
            Some("https://unity.com/releases/editor/whats-new/6000.3.18f1")
        );
    }

    #[test]
    fn release_notes_for_returns_none_for_unknown_version() {
        assert!(release_notes_for("9999.9.9f99").is_none());
    }

    #[test]
    fn legacy_lts_always_present_after_merge() {
        // Regression guard: legacy 2022.3 LTS entries must survive the
        // merge even when the input is empty (cold-start / no live data),
        // and each must carry an install changeset so the Install button
        // actually works.
        let merged = merge_legacy_lts(vec![]);
        for legacy in legacy_lts_entries() {
            let found = merged.iter().find(|e| e.version == legacy.version);
            assert!(found.is_some(), "{} missing after merge", legacy.version);
            let found = found.unwrap();
            assert_eq!(
                found.stream,
                ReleaseStream::Lts,
                "{} must be LTS",
                found.version
            );
            assert!(
                found.changeset.is_some(),
                "{} must carry a changeset for install",
                found.version
            );
        }
    }

    #[test]
    fn merge_dedupes_by_version_keeping_live_first() {
        // If the live feed ever re-adds a legacy version, the live entry
        // must win and we must not render a duplicate row. We detect the
        // winner via a sentinel release_notes_url that only the live
        // entry carries.
        let sentinel = "https://example.com/live-2022.3.62f1";
        let live = ReleaseEntry {
            version: "2022.3.62f1".to_string(),
            stream: ReleaseStream::Lts,
            release_date: Some("2025-05-07".to_string()),
            release_notes_url: sentinel.to_string(),
            changeset: Some("deadbeef0000".to_string()),
        };
        let merged = merge_legacy_lts(vec![live]);
        let matches: Vec<&ReleaseEntry> = merged
            .iter()
            .filter(|e| e.version == "2022.3.62f1")
            .collect();
        assert_eq!(
            matches.len(),
            1,
            "expected exactly one 2022.3.62f1 after merge, got {}",
            matches.len()
        );
        assert_eq!(
            matches[0].release_notes_url, sentinel,
            "live entry must supersede the curated legacy entry"
        );
    }

    #[test]
    fn stream_serializes_to_lowercase_string() {
        for stream in [
            ReleaseStream::Lts,
            ReleaseStream::Supported,
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
        assert_eq!(serde_json::to_string(&ReleaseStream::Lts).unwrap(), "\"lts\"");
        assert_eq!(
            serde_json::to_string(&ReleaseStream::Supported).unwrap(),
            "\"supported\""
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
    fn from_unity_str_maps_all_known_streams() {
        assert_eq!(ReleaseStream::from_unity_str("LTS"), ReleaseStream::Lts);
        assert_eq!(
            ReleaseStream::from_unity_str("SUPPORTED"),
            ReleaseStream::Supported
        );
        assert_eq!(ReleaseStream::from_unity_str("TECH"), ReleaseStream::Tech);
        assert_eq!(ReleaseStream::from_unity_str("BETA"), ReleaseStream::Beta);
        assert_eq!(
            ReleaseStream::from_unity_str("ALPHA"),
            ReleaseStream::Alpha
        );
    }

    #[test]
    fn from_unity_str_unknown_falls_back_to_tech() {
        // A future Unity stream string we have not seen must not drop
        // the row; fall back to a conservative stable-ish default.
        assert_eq!(ReleaseStream::from_unity_str("EXPERIMENTAL"), ReleaseStream::Tech);
    }

    #[test]
    fn extract_changeset_from_deep_link() {
        assert_eq!(
            extract_changeset(Some("unityhub://6000.4.12f1/3ca267ce8005")),
            Some("3ca267ce8005".to_string())
        );
    }

    #[test]
    fn extract_changeset_handles_missing_or_malformed() {
        assert_eq!(extract_changeset(None), None);
        assert_eq!(extract_changeset(Some("")), None);
        assert_eq!(extract_changeset(Some("not-a-link")), None);
        // Missing changeset segment.
        assert_eq!(extract_changeset(Some("unityhub://6000.0.0f1/")), None);
    }

    #[test]
    fn normalize_release_date_truncates_timestamp() {
        assert_eq!(
            normalize_release_date(&Some("2026-06-17T15:09:23.805Z".to_string())),
            Some("2026-06-17".to_string())
        );
        // Already-date-only passes through.
        assert_eq!(
            normalize_release_date(&Some("2026-06-17".to_string())),
            Some("2026-06-17".to_string())
        );
        // Non-ISO values pass through unchanged (no data loss).
        assert_eq!(
            normalize_release_date(&Some("tomorrow".to_string())),
            Some("tomorrow".to_string())
        );
        assert_eq!(normalize_release_date(&None), None);
    }

    #[test]
    fn parse_archive_payload_extracts_entries_from_fixture() {
        let html = fixture_html();
        let entries =
            parse_archive_payload(&html).expect("fixture must yield a non-empty entry list");
        assert!(!entries.is_empty(), "fixture should produce entries");
        // The fixture spans all five Unity streams.
        let mut streams = std::collections::HashSet::new();
        for e in &entries {
            streams.insert(e.stream.clone());
        }
        for expected in [
            ReleaseStream::Lts,
            ReleaseStream::Supported,
            ReleaseStream::Tech,
            ReleaseStream::Beta,
            ReleaseStream::Alpha,
        ] {
            assert!(
                streams.contains(&expected),
                "fixture should include stream {:?}",
                expected
            );
        }
    }

    #[test]
    fn parse_archive_payload_extracts_changeset_and_normalizes_date() {
        let html = fixture_html();
        let entries = parse_archive_payload(&html).unwrap();
        // The fixture's first LTS entry is 6000.3.18f1 with deep link
        // unityhub://6000.3.18f1/5ebeb53e4c07.
        let lts = entries
            .iter()
            .find(|e| e.version == "6000.3.18f1")
            .expect("fixture must include 6000.3.18f1");
        assert_eq!(lts.stream, ReleaseStream::Lts);
        assert_eq!(lts.changeset.as_deref(), Some("5ebeb53e4c07"));
        // Date normalized to YYYY-MM-DD.
        assert_eq!(lts.release_date.as_deref(), Some("2026-06-17"));
        assert!(lts.release_notes_url.ends_with("6000.3.18f1"));
    }

    #[test]
    fn parse_archive_payload_classifies_6000_0_as_tech() {
        // Regression guard: the live feed classifies 6000.0.x as TECH,
        // not LTS. The fixture includes 6000.0.22f1.
        let html = fixture_html();
        let entries = parse_archive_payload(&html).unwrap();
        let tech = entries
            .iter()
            .find(|e| e.version == "6000.0.22f1")
            .expect("fixture must include 6000.0.22f1");
        assert_eq!(tech.stream, ReleaseStream::Tech);
    }

    #[test]
    fn parse_archive_payload_returns_none_when_payload_absent() {
        // A page that does not embed the catalog must not crash.
        assert!(parse_archive_payload("<html><body>nothing here</body></html>").is_none());
    }

    #[test]
    fn parse_archive_payload_sorts_newest_first() {
        let html = fixture_html();
        let entries = parse_archive_payload(&html).unwrap();
        for pair in entries.windows(2) {
            let a = pair[0].release_date.as_deref().unwrap_or("");
            let b = pair[1].release_date.as_deref().unwrap_or("");
            assert!(a >= b, "parsed entries out of order: {} should be >= {}", a, b);
        }
    }

    #[test]
    fn releases_file_roundtrip_preserves_entries() {
        let original = ReleasesFile {
            version: 1,
            fetched_at: Some("2026-06-11T19:00:00+00:00".to_string()),
            entries: snapshot_entries(),
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

    #[test]
    fn releases_file_loads_without_supported_field_for_legacy_cache() {
        // A cache written before the Supported variant existed only ever
        // serialized lts/tech/beta/alpha. Deserialization must still
        // succeed (the enum is open over the lowercase set).
        let legacy = r#"{
            "version": 1,
            "fetchedAt": "2026-01-01T00:00:00Z",
            "entries": [
                {"version":"6000.0.32f1","stream":"lts","releaseNotesUrl":"https://unity.com/releases/editor/whats-new/6000.0.32f1"}
            ]
        }"#;
        let file: ReleasesFile = serde_json::from_str(legacy).unwrap();
        assert_eq!(file.entries.len(), 1);
        // We do not rewrite history: a legacy lts entry round-trips as
        // lts. New fetches will classify it correctly.
        assert_eq!(file.entries[0].stream, ReleaseStream::Lts);
    }
}
