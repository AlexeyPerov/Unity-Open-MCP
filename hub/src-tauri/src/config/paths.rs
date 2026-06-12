use std::path::PathBuf;

const CONFIG_DIR_NAME: &str = "unity-hub-pro";

pub fn config_dir() -> PathBuf {
    let base = dirs::config_dir().unwrap_or_else(|| PathBuf::from("."));
    base.join(CONFIG_DIR_NAME)
}

pub fn settings_path() -> PathBuf {
    config_dir().join("settings.json")
}

pub fn projects_path() -> PathBuf {
    config_dir().join("projects.json")
}

pub fn ensure_config_dir() -> std::io::Result<()> {
    let dir = config_dir();
    if !dir.exists() {
        std::fs::create_dir_all(&dir)?;
    }
    Ok(())
}
