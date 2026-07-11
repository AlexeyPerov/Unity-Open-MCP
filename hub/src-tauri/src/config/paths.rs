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

pub fn home_dir() -> Option<PathBuf> {
    dirs::home_dir()
}

pub fn unity_hub_data_dir() -> Option<PathBuf> {
    if cfg!(target_os = "macos") {
        home_dir().map(|h| h.join("Library").join("Application Support").join("UnityHub"))
    } else if cfg!(target_os = "windows") {
        dirs::data_dir().map(|d| d.join("UnityHub"))
    } else {
        home_dir().map(|h| h.join(".config").join("UnityHub"))
    }
}

pub fn unity_hub_templates_dir() -> Option<PathBuf> {
    let templates = unity_hub_data_dir()?.join("Templates");
    if templates.is_dir() {
        Some(templates)
    } else {
        None
    }
}

pub fn unity_hub_editor_default_dir() -> PathBuf {
    if cfg!(target_os = "macos") {
        PathBuf::from("/Applications/Unity/Hub/Editor")
    } else if cfg!(target_os = "windows") {
        PathBuf::from("C:\\Program Files\\Unity\\Hub\\Editor")
    } else {
        home_dir()
            .map(|h| h.join("Unity").join("Hub").join("Editor"))
            .unwrap_or_else(|| PathBuf::from("Unity/Hub/Editor"))
    }
}

pub fn unity_editor_logs_dir() -> PathBuf {
    if cfg!(target_os = "macos") {
        home_dir()
            .map(|h| h.join("Library").join("Logs").join("Unity"))
            .unwrap_or_else(|| PathBuf::from("~/Library/Logs/Unity"))
    } else if cfg!(target_os = "windows") {
        if let Some(local) = dirs::config_local_dir() {
            return local.join("Unity").join("Editor");
        }
        if let Some(appdata) = std::env::var_os("LOCALAPPDATA") {
            return PathBuf::from(appdata).join("Unity").join("Editor");
        }
        PathBuf::from("C:\\Users\\Public\\AppData\\Local\\Unity\\Editor")
    } else {
        if let Some(xdg) = std::env::var_os("XDG_CONFIG_HOME") {
            return PathBuf::from(xdg).join("unity3d");
        }
        home_dir()
            .map(|h| h.join(".config").join("unity3d"))
            .unwrap_or_else(|| PathBuf::from("~/.config/unity3d"))
    }
}

pub fn unity_crash_dumps_dir() -> PathBuf {
    if cfg!(target_os = "macos") {
        home_dir()
            .map(|h| h.join("Library").join("Logs").join("DiagnosticReports"))
            .unwrap_or_else(|| PathBuf::from("~/Library/Logs/DiagnosticReports"))
    } else if cfg!(target_os = "windows") {
        if let Some(local) = dirs::config_local_dir() {
            return local.join("CrashDumps");
        }
        if let Some(appdata) = std::env::var_os("LOCALAPPDATA") {
            return PathBuf::from(appdata).join("CrashDumps");
        }
        PathBuf::from("C:\\Users\\Public\\AppData\\Local\\CrashDumps")
    } else {
        unity_editor_logs_dir()
    }
}

pub fn unity_asset_store_parent_dir() -> Option<PathBuf> {
    if cfg!(target_os = "macos") {
        home_dir().map(|h| h.join("Library").join("Application Support").join("Unity"))
    } else if cfg!(target_os = "windows") {
        if let Some(local) = dirs::config_local_dir() {
            return Some(local.join("Unity"));
        }
        if let Some(appdata) = std::env::var_os("LOCALAPPDATA") {
            return Some(PathBuf::from(appdata).join("Unity"));
        }
        Some(PathBuf::from("C:\\Users\\Public\\AppData\\Local\\Unity"))
    } else {
        None
    }
}
