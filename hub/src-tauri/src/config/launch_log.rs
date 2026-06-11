use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::thread;

use chrono::Utc;
use serde::{Deserialize, Serialize};

use crate::config::paths;

/// Default rotation cap (5 MB). Exposed as a constant so unit tests can use
/// a smaller cap without exposing a runtime knob.
pub const DEFAULT_MAX_BYTES: u64 = 5 * 1024 * 1024;

/// Number of rotated files kept on disk. The current file is `launches.log`
/// and the previous one is `launches.log.1`. Older files are discarded to
/// honor the "keep last 2 files" policy from M1.5-2.
pub const ROTATED_FILES: u32 = 1;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "result", rename_all = "camelCase")]
pub enum LaunchOutcome {
    #[serde(rename_all = "camelCase")]
    Ok {
        pid: u32,
        unity_version: Option<String>,
        executable_path: String,
    },
    #[serde(rename_all = "camelCase")]
    Error { code: String, message: String },
    /// M1.5-14: emitted by the upgrade assistant. Carries the
    /// before/after versions and bundleVersion so the persistent log
    /// captures the change in the same shape as a launch. The `pid`
    /// / `installPath` fields on the parent record are null for this
    /// outcome (Unity was not launched, only the project's project
    /// metadata was rewritten).
    #[serde(rename_all = "camelCase")]
    Upgrade {
        from_version: String,
        to_version: String,
        previous_bundle_version: String,
        new_bundle_version: String,
        strategy: String,
    },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchRecord {
    pub timestamp: String,
    pub project_id: String,
    pub project_name: String,
    pub project_path: String,
    pub unity_version: Option<String>,
    pub install_path: Option<String>,
    pub pid: Option<u32>,
    pub launch_args: Vec<String>,
    pub build_target: Option<String>,
    pub outcome: LaunchOutcome,
    /// M1.5-18: the active Hub theme at the time of the launch
    /// (`"dark" | "light" | "system"`). The frontend resolves
    /// `system` to the concrete `dark` / `light` value before passing
    /// it in, so the on-disk record always carries a concrete
    /// palette. `None` for legacy writers (pre-M1.5-18) that did not
    /// capture the field — the deserializer fills it with
    /// `default_theme()` via `#[serde(default)]`.
    #[serde(default = "default_record_theme")]
    pub theme: Option<String>,
}

fn default_record_theme() -> Option<String> {
    Some("system".to_string())
}

/// Location of the persistent launch log on disk.
pub fn launch_log_path() -> PathBuf {
    paths::config_dir().join("logs").join("launches.log")
}

/// Append a record to the per-launch log file. Rotation: when the file
/// exceeds `max_bytes`, shift `launches.log -> launches.log.1` and start a
/// fresh file. The oldest rotation is dropped. All I/O happens synchronously
/// so the helper is safe to call from a background thread.
pub fn append_record(record: &LaunchRecord, max_bytes: u64) -> std::io::Result<()> {
    let log_path = launch_log_path();
    if let Some(parent) = log_path.parent() {
        fs::create_dir_all(parent)?;
    }
    rotate_if_needed(&log_path, max_bytes)?;
    let serialized = serde_json::to_string(record)
        .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
    let mut file = fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&log_path)?;
    file.write_all(serialized.as_bytes())?;
    file.write_all(b"\n")?;
    file.sync_all()?;
    Ok(())
}

fn rotate_if_needed(path: &Path, max_bytes: u64) -> std::io::Result<()> {
    if !path.exists() {
        return Ok(());
    }
    let meta = fs::metadata(path)?;
    if meta.len() < max_bytes {
        return Ok(());
    }
    for i in (1..=ROTATED_FILES).rev() {
        let from = rotated_path(path, i);
        if from.exists() {
            if i == ROTATED_FILES {
                fs::remove_file(&from)?;
            } else {
                let to = rotated_path(path, i + 1);
                fs::rename(&from, &to)?;
            }
        }
    }
    let first = rotated_path(path, 1);
    fs::rename(path, &first)?;
    Ok(())
}

fn rotated_path(base: &Path, index: u32) -> PathBuf {
    let file_name = base
        .file_name()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_else(|| "launches.log".to_string());
    let rotated = format!("{}.{}", file_name, index);
    match base.parent() {
        Some(parent) => parent.join(rotated),
        None => PathBuf::from(rotated),
    }
}

/// Read the last `line_count` lines from the persistent launch log. Returns
/// an empty string when the file is missing or has no readable content.
pub fn tail_lines(line_count: usize) -> String {
    let log_path = launch_log_path();
    let Ok(content) = fs::read_to_string(&log_path) else {
        return String::new();
    };
    if line_count == 0 || content.is_empty() {
        return String::new();
    }
    let lines: Vec<&str> = content.lines().collect();
    let start = lines.len().saturating_sub(line_count);
    lines[start..].join("\n")
}

/// Fire-and-forget background writer. Spawns a single OS thread that calls
/// `append_record` with the default cap. Errors are logged at warn level; the
/// caller (a launch command) does not block on disk I/O.
pub fn append_record_async(record: LaunchRecord) {
    let result = thread::Builder::new()
        .name("hub-launch-log".to_string())
        .spawn(move || {
            if let Err(e) = append_record(&record, DEFAULT_MAX_BYTES) {
                log::warn!(
                    "Failed to append launch log record for project {}: {}",
                    record.project_id,
                    e
                );
            }
        });
    if let Err(e) = result {
        log::warn!("Failed to spawn launch-log thread: {}", e);
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchLogTail {
    /// Absolute path to the underlying log file (for diagnostics panels).
    pub path: String,
    /// Tail of the log, one record per line, most recent last. Empty when
    /// the file is missing or unreadable.
    pub content: String,
    /// Number of lines requested.
    pub line_count: usize,
}

/// Tauri command wrapper for `tail_lines`. Returns the path + tail so the
/// UI can decide whether to surface the file in error messaging.
#[tauri::command]
pub fn get_launch_log_tail(line_count: usize) -> LaunchLogTail {
    let resolved_line_count = if line_count == 0 { 0 } else { line_count.min(2000) };
    let path = launch_log_path().to_string_lossy().to_string();
    let content = tail_lines(resolved_line_count);
    LaunchLogTail {
        path,
        content,
        line_count: resolved_line_count,
    }
}

/// Build a record from the launch inputs at the point of resolution. Used by
/// the launch command and by the failure path so both branches share the
/// same record shape.
#[allow(clippy::too_many_arguments)]
pub fn build_record(
    project_id: &str,
    project_name: &str,
    project_path: &str,
    unity_version: Option<&str>,
    install_path: Option<&str>,
    pid: Option<u32>,
    launch_args: &[String],
    build_target: Option<&str>,
    outcome: LaunchOutcome,
    // M1.5-18: the active Hub theme at the time of the launch
    // (`"dark" | "light"` — the frontend has already resolved
    // `system` to a concrete palette). Defaults to `"system"` for
    // tests and the upgrade-flow callers so the field is always
    // present on the wire.
    theme: Option<&str>,
) -> LaunchRecord {
    LaunchRecord {
        timestamp: Utc::now().to_rfc3339(),
        project_id: project_id.to_string(),
        project_name: project_name.to_string(),
        project_path: project_path.to_string(),
        unity_version: unity_version.map(str::to_string),
        install_path: install_path.map(str::to_string),
        pid,
        launch_args: launch_args.to_vec(),
        build_target: build_target.map(str::to_string),
        outcome,
        theme: theme.map(str::to_string).or_else(default_record_theme),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    /// Locks the on-disk test so parallel cargo threads do not clobber each
    /// other's `launches.log`. The lock is process-wide via `OnceLock`.
    static PATH_LOCK: Mutex<()> = Mutex::new(());

    /// Run `f` with a tempdir temporarily masquerading as the Hub config
    /// dir. We swap the resolved `config_dir` aside, create the same path
    /// inside the tempdir, run the test, then restore everything. Tests
    /// that only need `append_record`/`tail_lines` do not need to read the
    /// real config dir.
    fn with_tempdir<F: FnOnce(&Path)>(f: F) {
        let _guard = PATH_LOCK.lock().unwrap();
        let dir = tempfile::tempdir().unwrap();
        let original = paths::config_dir();
        let backup = original.with_extension("bak");
        let mut restored = false;
        if original.exists() {
            let _ = fs::rename(&original, &backup);
            restored = true;
        }
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| f(dir.path())));
        if original.exists() {
            let _ = fs::remove_dir_all(&original);
        }
        if restored {
            let _ = fs::rename(&backup, &original);
        }
        if let Err(e) = result {
            std::panic::resume_unwind(e);
        }
    }

    fn sample_record(outcome: LaunchOutcome) -> LaunchRecord {
        build_record(
            "pid",
            "Name",
            "/path",
            Some("6000.0.1f1"),
            Some("/install"),
            Some(42),
            &["-projectPath".to_string(), "/path".to_string()],
            Some("StandaloneWindows64"),
            outcome,
            Some("dark"),
        )
    }

    #[test]
    fn append_creates_file_and_logs_one_line() {
        with_tempdir(|_| {
            let r = sample_record(LaunchOutcome::Ok {
                pid: 42,
                unity_version: Some("6000.0.1f1".to_string()),
                executable_path: "/install".to_string(),
            });
            append_record(&r, DEFAULT_MAX_BYTES).unwrap();
            let content = fs::read_to_string(launch_log_path()).unwrap();
            assert_eq!(content.lines().count(), 1);
            assert!(content.contains("\"projectId\":\"pid\""));
            assert!(content.contains("\"result\":\"ok\""));
        });
    }

    #[test]
    fn append_appends_multiple_lines() {
        with_tempdir(|_| {
            for i in 0..3 {
                let r = build_record(
                    &format!("p{}", i),
                    "Name",
                    "/path",
                    None,
                    None,
                    Some(i),
                    &[],
                    None,
                    LaunchOutcome::Error {
                        code: "launchFailed".to_string(),
                        message: "nope".to_string(),
                    },
                    Some("light"),
                );
                append_record(&r, DEFAULT_MAX_BYTES).unwrap();
            }
            let content = fs::read_to_string(launch_log_path()).unwrap();
            assert_eq!(content.lines().count(), 3);
        });
    }

    #[test]
    fn rotation_shifts_to_dot_one_when_cap_exceeded() {
        with_tempdir(|_| {
            for i in 0..3 {
                let payload = "x".repeat(40);
                let r = build_record(
                    &format!("p{}", i),
                    "Name",
                    &payload,
                    None,
                    None,
                    Some(i),
                    &[],
                    None,
                    LaunchOutcome::Ok {
                        pid: i,
                        unity_version: None,
                        executable_path: "/i".to_string(),
                    },
                    Some("system"),
                );
                append_record(&r, 60).unwrap();
            }
            assert!(launch_log_path().exists());
            assert!(rotated_path(&launch_log_path(), 1).exists());
            let rotated = fs::read_to_string(rotated_path(&launch_log_path(), 1)).unwrap();
            assert!(!rotated.is_empty());
        });
    }

    #[test]
    fn rotation_drops_oldest_copy() {
        with_tempdir(|_| {
            for i in 0..6 {
                let payload = "y".repeat(40);
                let r = build_record(
                    &format!("p{}", i),
                    "Name",
                    &payload,
                    None,
                    None,
                    Some(i),
                    &[],
                    None,
                    LaunchOutcome::Ok {
                        pid: i,
                        unity_version: None,
                        executable_path: "/i".to_string(),
                    },
                    Some("dark"),
                );
                append_record(&r, 60).unwrap();
            }
            assert!(!rotated_path(&launch_log_path(), 2).exists());
        });
    }

    #[test]
    fn tail_lines_returns_last_n() {
        with_tempdir(|_| {
            for i in 0..5 {
                let r = build_record(
                    &format!("p{}", i),
                    "Name",
                    "/path",
                    None,
                    None,
                    Some(i),
                    &[],
                    None,
                    LaunchOutcome::Ok {
                        pid: i,
                        unity_version: None,
                        executable_path: "/i".to_string(),
                    },
                    Some("dark"),
                );
                append_record(&r, DEFAULT_MAX_BYTES).unwrap();
            }
            let tail = tail_lines(2);
            assert_eq!(tail.lines().count(), 2);
            assert!(tail.contains("p3"));
            assert!(tail.contains("p4"));
            assert!(!tail.contains("p0"));
        });
    }

    #[test]
    fn tail_lines_returns_empty_when_file_missing() {
        with_tempdir(|_| {
            assert_eq!(tail_lines(10), "");
        });
    }

    #[test]
    fn tail_lines_returns_empty_when_zero_requested() {
        with_tempdir(|_| {
            let r = sample_record(LaunchOutcome::Ok {
                pid: 1,
                unity_version: None,
                executable_path: "/i".to_string(),
            });
            append_record(&r, DEFAULT_MAX_BYTES).unwrap();
            assert_eq!(tail_lines(0), "");
        });
    }

    #[test]
    fn record_serializes_camel_case() {
        let r = sample_record(LaunchOutcome::Error {
            code: "launchFailed".to_string(),
            message: "nope".to_string(),
        });
        let json = serde_json::to_string(&r).unwrap();
        assert!(json.contains("\"projectId\""));
        assert!(json.contains("\"projectName\""));
        assert!(json.contains("\"projectPath\""));
        assert!(json.contains("\"unityVersion\""));
        assert!(json.contains("\"installPath\""));
        assert!(json.contains("\"launchArgs\""));
        assert!(json.contains("\"buildTarget\""));
        assert!(json.contains("\"result\":\"error\""));
        assert!(json.contains("\"theme\""));
    }

    #[test]
    fn outcome_ok_serializes_with_fields() {
        let outcome = LaunchOutcome::Ok {
            pid: 7,
            unity_version: Some("6000.0.1f1".to_string()),
            executable_path: "/exec".to_string(),
        };
        let json = serde_json::to_string(&outcome).unwrap();
        assert!(json.contains("\"result\":\"ok\""));
        assert!(json.contains("\"pid\":7"));
        assert!(json.contains("\"unityVersion\":\"6000.0.1f1\""));
        assert!(json.contains("\"executablePath\":\"/exec\""));
    }

    #[test]
    fn outcome_upgrade_serializes_with_fields() {
        // M1.5-14: the upgrade flow logs an `Upgrade` outcome to the
        // same per-launch log file. The shape is symmetric with `Ok` /
        // `Error` so a JSON consumer can branch on `result` and read
        // the same `projectId` / `projectName` / `projectPath` from
        // the parent record. `pid` / `installPath` are null because
        // Unity was not spawned.
        let outcome = LaunchOutcome::Upgrade {
            from_version: "2022.3.48f1".to_string(),
            to_version: "6000.0.1f1".to_string(),
            previous_bundle_version: "1.0.0".to_string(),
            new_bundle_version: "1.0.1".to_string(),
            strategy: "patch".to_string(),
        };
        let json = serde_json::to_string(&outcome).unwrap();
        assert!(json.contains("\"result\":\"upgrade\""));
        assert!(json.contains("\"fromVersion\":\"2022.3.48f1\""));
        assert!(json.contains("\"toVersion\":\"6000.0.1f1\""));
        assert!(json.contains("\"previousBundleVersion\":\"1.0.0\""));
        assert!(json.contains("\"newBundleVersion\":\"1.0.1\""));
        assert!(json.contains("\"strategy\":\"patch\""));
        // The upgrade outcome must not carry launch-only fields.
        assert!(!json.contains("\"pid\""));
        assert!(!json.contains("\"executablePath\""));
    }

    #[test]
    fn build_record_uses_current_utc_timestamp() {
        let r = build_record(
            "p",
            "Name",
            "/path",
            None,
            None,
            None,
            &[],
            None,
            LaunchOutcome::Error {
                code: "x".to_string(),
                message: "y".to_string(),
            },
            Some("dark"),
        );
        let parsed: chrono::DateTime<Utc> = r.timestamp.parse().expect("parse rfc3339");
        let now = Utc::now();
        let diff = (now - parsed).num_seconds().abs();
        assert!(diff < 5, "timestamp drift too large: {}s", diff);
    }

    #[test]
    fn record_includes_theme_field() {
        // M1.5-18: the per-launch log records the active theme so the
        // diagnostics export can correlate user-reported issues with
        // the palette the Hub was rendering in. The on-disk shape is
        // `theme: "dark" | "light" | "system"`; a None caller falls
        // back to `"system"`.
        let r = sample_record(LaunchOutcome::Ok {
            pid: 1,
            unity_version: Some("6000.0.1f1".to_string()),
            executable_path: "/exec".to_string(),
        });
        let json = serde_json::to_string(&r).unwrap();
        assert!(json.contains("\"theme\":\"dark\""));
    }

    #[test]
    fn record_theme_defaults_to_system_when_caller_omits() {
        // The launch log is append-only and the deserializer must
        // fill the `theme` field with `"system"` for legacy callers
        // that did not pass one in (the upgrade flow, the
        // failure-path test, etc.). We round-trip through JSON to
        // exercise both the serializer and the deserializer.
        let r = build_record(
            "p",
            "Name",
            "/path",
            None,
            None,
            None,
            &[],
            None,
            LaunchOutcome::Error {
                code: "x".to_string(),
                message: "y".to_string(),
            },
            None,
        );
        assert_eq!(r.theme.as_deref(), Some("system"));
        let json = serde_json::to_string(&r).unwrap();
        let restored: LaunchRecord = serde_json::from_str(&json).unwrap();
        assert_eq!(restored.theme.as_deref(), Some("system"));
    }

    #[test]
    fn record_deserializes_without_theme_for_legacy_json() {
        // Pre-M1.5-18 lines in the on-disk log do not carry `theme`.
        // The deserializer must default the field to `Some("system")`
        // so the analytics code that branches on the field never
        // sees `None` for an existing record.
        let legacy = r#"{"timestamp":"2026-06-11T19:00:00+00:00","projectId":"p","projectName":"Name","projectPath":"/p","launchArgs":[],"outcome":{"result":"ok","pid":1,"unityVersion":null,"executablePath":"/e"}}"#;
        let r: LaunchRecord = serde_json::from_str(legacy).unwrap();
        assert_eq!(r.theme.as_deref(), Some("system"));
    }

    #[test]
    fn command_clamps_large_line_count() {
        with_tempdir(|_| {
            let r = sample_record(LaunchOutcome::Ok {
                pid: 1,
                unity_version: None,
                executable_path: "/i".to_string(),
            });
            append_record(&r, DEFAULT_MAX_BYTES).unwrap();
            let tail = get_launch_log_tail(10_000);
            assert_eq!(tail.line_count, 2000);
            assert!(!tail.content.is_empty());
        });
    }

    #[test]
    fn command_returns_empty_when_file_missing() {
        with_tempdir(|_| {
            let tail = get_launch_log_tail(50);
            assert_eq!(tail.content, "");
            assert_eq!(tail.line_count, 50);
        });
    }
}
