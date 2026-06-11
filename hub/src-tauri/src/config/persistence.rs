use std::fs;
use std::io::Write;
use std::path::Path;

use crate::config::paths;
use crate::config::schemas::{ProjectsFile, Settings};

fn backup_corrupt(path: &Path) {
    let backup = path.with_extension("json.corrupt");
    let _ = fs::rename(path, &backup);
    log::warn!(
        "Corrupt file backed up to {}",
        backup.display()
    );
}

fn atomic_write(path: &Path, data: &str) -> std::io::Result<()> {
    paths::ensure_config_dir()?;
    let tmp_path = path.with_extension("json.tmp");
    {
        let mut tmp = fs::File::create(&tmp_path)?;
        tmp.write_all(data.as_bytes())?;
        tmp.sync_all()?;
    }
    fs::rename(&tmp_path, path)?;
    Ok(())
}

pub fn load_settings() -> Settings {
    let path = paths::settings_path();
    if !path.exists() {
        let defaults = Settings::default();
        if let Err(e) = save_settings_inner(&defaults) {
            log::error!("Failed to create default settings: {}", e);
        }
        return defaults;
    }
    match fs::read_to_string(&path) {
        Ok(content) => match serde_json::from_str::<Settings>(&content) {
            Ok(s) => s,
            Err(e) => {
                log::warn!("Corrupt settings.json ({}), restoring defaults", e);
                backup_corrupt(&path);
                let defaults = Settings::default();
                let _ = save_settings_inner(&defaults);
                defaults
            }
        },
        Err(e) => {
            log::warn!("Cannot read settings.json ({}), restoring defaults", e);
            let defaults = Settings::default();
            let _ = save_settings_inner(&defaults);
            defaults
        }
    }
}

pub fn save_settings(settings: &Settings) -> std::io::Result<()> {
    save_settings_inner(settings)
}

fn save_settings_inner(settings: &Settings) -> std::io::Result<()> {
    let json = serde_json::to_string_pretty(settings)?;
    atomic_write(&paths::settings_path(), &json)
}

pub fn load_projects() -> ProjectsFile {
    let path = paths::projects_path();
    if !path.exists() {
        let defaults = ProjectsFile::default();
        if let Err(e) = save_projects_inner(&defaults) {
            log::error!("Failed to create default projects: {}", e);
        }
        return defaults;
    }
    match fs::read_to_string(&path) {
        Ok(content) => match serde_json::from_str::<ProjectsFile>(&content) {
            Ok(p) => p,
            Err(e) => {
                log::warn!("Corrupt projects.json ({}), restoring defaults", e);
                backup_corrupt(&path);
                let defaults = ProjectsFile::default();
                let _ = save_projects_inner(&defaults);
                defaults
            }
        },
        Err(e) => {
            log::warn!("Cannot read projects.json ({}), restoring defaults", e);
            let defaults = ProjectsFile::default();
            let _ = save_projects_inner(&defaults);
            defaults
        }
    }
}

pub fn save_projects(projects: &ProjectsFile) -> std::io::Result<()> {
    save_projects_inner(projects)
}

fn save_projects_inner(projects: &ProjectsFile) -> std::io::Result<()> {
    let json = serde_json::to_string_pretty(projects)?;
    atomic_write(&paths::projects_path(), &json)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::schemas::ProjectEntry;

    #[test]
    fn atomic_write_creates_file_with_content() {
        let dir = tempfile::tempdir().unwrap();
        let file_path = dir.path().join("test.json");
        atomic_write(&file_path, "hello").unwrap();
        assert_eq!(fs::read_to_string(&file_path).unwrap(), "hello");
    }

    #[test]
    fn atomic_write_overwrites_existing() {
        let dir = tempfile::tempdir().unwrap();
        let file_path = dir.path().join("test.json");
        atomic_write(&file_path, "first").unwrap();
        atomic_write(&file_path, "second").unwrap();
        assert_eq!(fs::read_to_string(&file_path).unwrap(), "second");
    }

    #[test]
    fn atomic_write_no_leftover_tmp_file() {
        let dir = tempfile::tempdir().unwrap();
        let file_path = dir.path().join("test.json");
        atomic_write(&file_path, "content").unwrap();
        assert!(!dir.path().join("test.json.tmp").exists());
    }

    #[test]
    fn backup_corrupt_renames_to_corrupt_extension() {
        let dir = tempfile::tempdir().unwrap();
        let file_path = dir.path().join("test.json");
        fs::write(&file_path, "corrupt data").unwrap();
        backup_corrupt(&file_path);
        assert!(!file_path.exists());
        let corrupt_path = dir.path().join("test.json.corrupt");
        assert!(corrupt_path.exists());
        assert_eq!(fs::read_to_string(&corrupt_path).unwrap(), "corrupt data");
    }

    #[test]
    fn save_and_load_settings_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        let original = Settings::default();
        let json = serde_json::to_string_pretty(&original).unwrap();
        atomic_write(&path, &json).unwrap();
        let content = fs::read_to_string(&path).unwrap();
        let loaded: Settings = serde_json::from_str(&content).unwrap();
        assert_eq!(loaded.version, original.version);
        assert_eq!(loaded.launch.mode, original.launch.mode);
    }

    #[test]
    fn save_and_load_projects_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("projects.json");
        let original = ProjectsFile {
            version: 1,
            projects: vec![ProjectEntry {
                id: "abc".to_string(),
                name: "Proj".to_string(),
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
            }],
        };
        let json = serde_json::to_string_pretty(&original).unwrap();
        atomic_write(&path, &json).unwrap();
        let content = fs::read_to_string(&path).unwrap();
        let loaded: ProjectsFile = serde_json::from_str(&content).unwrap();
        assert_eq!(loaded.projects.len(), 1);
        assert_eq!(loaded.projects[0].id, "abc");
    }

    #[test]
    fn corrupt_file_recoverable_to_defaults() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        fs::write(&path, "{{{{broken").unwrap();
        backup_corrupt(&path);
        let defaults = Settings::default();
        let json = serde_json::to_string_pretty(&defaults).unwrap();
        atomic_write(&path, &json).unwrap();
        let content = fs::read_to_string(&path).unwrap();
        let recovered: Settings = serde_json::from_str(&content).unwrap();
        assert_eq!(recovered.version, 1);
        assert!(path.with_extension("json.corrupt").exists());
    }
}
