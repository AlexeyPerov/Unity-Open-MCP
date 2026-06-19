use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::Serialize;

use crate::config::paths;

#[derive(Serialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct DiagnosticsPaths {
    pub config_dir: String,
    pub settings_file: String,
    pub projects_file: String,
}

#[derive(Serialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct ExportDiagnosticsResult {
    pub path: String,
    pub settings_copied: bool,
    pub projects_copied: bool,
    pub log_included: bool,
}

#[derive(Serialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct ExportDiagnosticsError {
    pub kind: String,
    pub message: String,
}

#[tauri::command]
pub fn get_diagnostics_paths() -> DiagnosticsPaths {
    DiagnosticsPaths {
        config_dir: paths::config_dir().to_string_lossy().to_string(),
        settings_file: paths::settings_path().to_string_lossy().to_string(),
        projects_file: paths::projects_path().to_string_lossy().to_string(),
    }
}

#[tauri::command]
pub fn export_diagnostics(
    target_dir: String,
    log_tail: Option<String>,
) -> Result<ExportDiagnosticsResult, ExportDiagnosticsError> {
    export_diagnostics_inner(
        &target_dir,
        &paths::settings_path(),
        &paths::projects_path(),
        log_tail.as_deref(),
    )
    .map_err(|e| ExportDiagnosticsError {
        kind: e.kind,
        message: e.message,
    })
}

#[derive(Debug)]
struct ExportError {
    kind: String,
    message: String,
}

impl ExportError {
    fn new(kind: &str, message: impl Into<String>) -> Self {
        Self { kind: kind.to_string(), message: message.into() }
    }
}

impl std::fmt::Display for ExportError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {}", self.kind, self.message)
    }
}

fn copy_if_exists(source: &Path, dest_dir: &Path) -> Result<bool, ExportError> {
    if !source.exists() {
        return Ok(false);
    }
    let file_name = source.file_name().ok_or_else(|| {
        ExportError::new("invalidSource", format!("{} has no file name", source.display()))
    })?;
    let dest = dest_dir.join(file_name);
    fs::copy(source, &dest).map_err(|e| {
        ExportError::new("copyFailed", format!("copy {} -> {}: {}", source.display(), dest.display(), e))
    })?;
    Ok(true)
}

fn write_version_file(dest_dir: &Path) -> Result<(), ExportError> {
    let path = dest_dir.join("version.txt");
    let mut f = fs::File::create(&path).map_err(|e| {
        ExportError::new("writeFailed", format!("create {}: {}", path.display(), e))
    })?;
    let build_ts = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let body = format!(
        "app: {}\nversion: {}\nbuild_target: {}\nbuild_profile: {}\nexported_at_unix: {}\n",
        env!("CARGO_PKG_NAME"),
        env!("CARGO_PKG_VERSION"),
        std::env::consts::ARCH,
        if cfg!(debug_assertions) { "debug" } else { "release" },
        build_ts,
    );
    f.write_all(body.as_bytes()).map_err(|e| {
        ExportError::new("writeFailed", format!("write {}: {}", path.display(), e))
    })?;
    Ok(())
}

fn write_log_file(dest_dir: &Path, log_tail: &str) -> Result<(), ExportError> {
    let path = dest_dir.join("log.txt");
    let mut f = fs::File::create(&path).map_err(|e| {
        ExportError::new("writeFailed", format!("create {}: {}", path.display(), e))
    })?;
    f.write_all(log_tail.as_bytes()).map_err(|e| {
        ExportError::new("writeFailed", format!("write {}: {}", path.display(), e))
    })?;
    Ok(())
}

fn export_diagnostics_inner(
    target_dir: &str,
    settings_source: &Path,
    projects_source: &Path,
    log_tail: Option<&str>,
) -> Result<ExportDiagnosticsResult, ExportError> {
    if target_dir.trim().is_empty() {
        return Err(ExportError::new("invalidTarget", "target path is empty"));
    }
    let target = PathBuf::from(target_dir);
    if target.exists() {
        if !target.is_dir() {
            return Err(ExportError::new(
                "notADirectory",
                format!("{} exists and is not a directory", target.display()),
            ));
        }
        let has_contents = fs::read_dir(&target)
            .map_err(|e| ExportError::new("readFailed", format!("read {}: {}", target.display(), e)))?
            .next()
            .is_some();
        if has_contents {
            return Err(ExportError::new(
                "targetExists",
                format!("{} already exists and is not empty", target.display()),
            ));
        }
    } else {
        fs::create_dir_all(&target).map_err(|e| {
            ExportError::new("createFailed", format!("create {}: {}", target.display(), e))
        })?;
    }

    let settings_copied = copy_if_exists(settings_source, &target)?;
    let projects_copied = copy_if_exists(projects_source, &target)?;
    write_version_file(&target)?;
    let log_included = if let Some(tail) = log_tail {
        if !tail.is_empty() {
            write_log_file(&target, tail)?;
            true
        } else {
            false
        }
    } else {
        false
    };

    Ok(ExportDiagnosticsResult {
        path: target.to_string_lossy().to_string(),
        settings_copied,
        projects_copied,
        log_included,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
use crate::config::paths;
use crate::config::schemas::{ProjectEntry, ProjectKind, ProjectsFile, Settings};

    fn write_settings(dir: &Path) {
        let s = Settings::default();
        let json = serde_json::to_string_pretty(&s).unwrap();
        std::fs::write(dir.join("settings.json"), json).unwrap();
    }

    fn write_projects(dir: &Path) {
        let p = ProjectsFile {
            version: 1,
            projects: vec![ProjectEntry {
                id: "id1".to_string(),
                name: "Demo".to_string(),
                path: "/x".to_string(),
                unity_version: Some("6000.0.1f1".to_string()),
                last_opened_at: None,
                last_modified_at: None,
                launch_args: None,
                platform_intent: None,
                last_launch_pid: None,
                last_launch_at: None,
                frecency: 0,
                git_branch: None,
                source: "manual".to_string(),
                hidden: false,
                stale: false,
                env_vars: Default::default(),
                render_pipeline: None,
                default_build_target: None,
                kind: ProjectKind::Unity,
                package_manifest_path: None,
                migrate_source_folder: None,
                line_count_stats: None,
            }],
        };
        let json = serde_json::to_string_pretty(&p).unwrap();
        std::fs::write(dir.join("projects.json"), json).unwrap();
    }

    #[test]
    fn diagnostics_paths_uses_config_dir() {
        let p = get_diagnostics_paths();
        assert!(p.config_dir.ends_with("unity-hub-pro"));
        assert!(p.settings_file.ends_with("settings.json"));
        assert!(p.projects_file.ends_with("projects.json"));
    }

    #[test]
    fn export_creates_dir_and_copies_existing_files() {
        let config = tempfile::tempdir().unwrap();
        let bundle = tempfile::tempdir().unwrap();
        write_settings(config.path());
        write_projects(config.path());
        let settings = config.path().join("settings.json");
        let projects = config.path().join("projects.json");

        let target = bundle.path().join("support-bundle");
        let result = export_diagnostics_inner(
            target.to_str().unwrap(),
            &settings,
            &projects,
            Some("drawer line 1\ndrawer line 2\n"),
        )
        .unwrap();

        assert!(target.is_dir());
        assert!(target.join("settings.json").is_file());
        assert!(target.join("projects.json").is_file());
        assert!(target.join("version.txt").is_file());
        assert!(target.join("log.txt").is_file());
        assert_eq!(result.settings_copied, true);
        assert_eq!(result.projects_copied, true);
        assert_eq!(result.log_included, true);
        let log = std::fs::read_to_string(target.join("log.txt")).unwrap();
        assert!(log.contains("drawer line 1"));
        let version = std::fs::read_to_string(target.join("version.txt")).unwrap();
        assert!(version.contains("version:"));
        assert!(version.contains("app:"));
    }

    #[test]
    fn export_skips_missing_source_files() {
        let config = tempfile::tempdir().unwrap();
        let bundle = tempfile::tempdir().unwrap();
        let settings = config.path().join("settings.json");
        let projects = config.path().join("projects.json");
        let target = bundle.path().join("empty-source-bundle");
        let result = export_diagnostics_inner(
            target.to_str().unwrap(),
            &settings,
            &projects,
            None,
        )
        .unwrap();
        assert!(target.is_dir());
        assert!(target.join("version.txt").is_file());
        assert_eq!(result.settings_copied, false);
        assert_eq!(result.projects_copied, false);
        assert_eq!(result.log_included, false);
    }

    #[test]
    fn export_omits_log_when_tail_empty() {
        let config = tempfile::tempdir().unwrap();
        let bundle = tempfile::tempdir().unwrap();
        let settings = config.path().join("settings.json");
        let projects = config.path().join("projects.json");
        let target = bundle.path().join("no-log-bundle");
        let result = export_diagnostics_inner(
            target.to_str().unwrap(),
            &settings,
            &projects,
            Some(""),
        )
        .unwrap();
        assert_eq!(result.log_included, false);
        assert!(!target.join("log.txt").exists());
    }

    #[test]
    fn export_omits_log_when_tail_none() {
        let config = tempfile::tempdir().unwrap();
        let bundle = tempfile::tempdir().unwrap();
        let settings = config.path().join("settings.json");
        let projects = config.path().join("projects.json");
        let target = bundle.path().join("no-log-bundle-2");
        let result = export_diagnostics_inner(
            target.to_str().unwrap(),
            &settings,
            &projects,
            None,
        )
        .unwrap();
        assert_eq!(result.log_included, false);
        assert!(!target.join("log.txt").exists());
    }

    #[test]
    fn export_rejects_existing_non_empty_dir() {
        let config = tempfile::tempdir().unwrap();
        let bundle = tempfile::tempdir().unwrap();
        let settings = config.path().join("settings.json");
        let projects = config.path().join("projects.json");
        let target = bundle.path().join("occupied");
        std::fs::create_dir(&target).unwrap();
        std::fs::write(target.join("preexisting.txt"), "data").unwrap();
        let err = export_diagnostics_inner(
            target.to_str().unwrap(),
            &settings,
            &projects,
            None,
        )
        .unwrap_err();
        assert_eq!(err.kind, "targetExists");
    }

    #[test]
    fn export_rejects_existing_file_path() {
        let config = tempfile::tempdir().unwrap();
        let bundle = tempfile::tempdir().unwrap();
        let settings = config.path().join("settings.json");
        let projects = config.path().join("projects.json");
        let target = bundle.path().join("not-a-dir");
        std::fs::write(&target, "data").unwrap();
        let err = export_diagnostics_inner(
            target.to_str().unwrap(),
            &settings,
            &projects,
            None,
        )
        .unwrap_err();
        assert_eq!(err.kind, "notADirectory");
    }

    #[test]
    fn export_rejects_empty_target_path() {
        let config = tempfile::tempdir().unwrap();
        let settings = config.path().join("settings.json");
        let projects = config.path().join("projects.json");
        let err = export_diagnostics_inner(
            "",
            &settings,
            &projects,
            None,
        )
        .unwrap_err();
        assert_eq!(err.kind, "invalidTarget");
    }

    #[test]
    fn export_propagates_copy_failure() {
        let config = tempfile::tempdir().unwrap();
        let bundle = tempfile::tempdir().unwrap();
        write_settings(config.path());
        let settings = config.path().join("settings.json");
        let projects = config.path().join("projects.json");
        let target = bundle.path().join("copy-fail-bundle");
        std::fs::create_dir(&target).unwrap();
        std::fs::write(target.join("settings.json"), "blocker").unwrap();
        let err = export_diagnostics_inner(
            target.to_str().unwrap(),
            &settings,
            &projects,
            None,
        )
        .unwrap_err();
        assert_eq!(err.kind, "targetExists");
    }

    #[test]
    fn diagnostics_paths_match_paths_module() {
        let p = get_diagnostics_paths();
        assert_eq!(p.config_dir, paths::config_dir().to_string_lossy());
        assert_eq!(p.settings_file, paths::settings_path().to_string_lossy());
        assert_eq!(p.projects_file, paths::projects_path().to_string_lossy());
    }
}
