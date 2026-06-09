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
