//! Filesystem path resolution.
//!
//! Two concerns live here:
//!  1. The app **config dir** — where the operator's last-project
//!     pointer (`app-config.json`) is stored (phase-1 task 3). Mirrors
//!     the Hub's `paths.rs` (`dirs::config_dir` + a named subdir).
//!  2. **Bundled resources** — engine profiles and scenarios shipped
//!     with the app. In dev they sit next to the crate; in a bundled
//!     app they live under the resource dir resolved by Tauri.

use std::path::PathBuf;

/// Subdir of the OS config dir used by the suite app. Kept distinct
/// from the Hub's `unity-hub-pro` dir so the two apps never share state.
const CONFIG_DIR_NAME: &str = "validation-suite";

/// The OS config dir for this app (e.g.
/// `~/.config/validation-suite` / `~/Library/Application
/// Support/validation-suite`). Falls back to a relative `.` if the OS
/// has no concept of a config dir.
pub fn config_dir() -> PathBuf {
    let base = dirs::config_dir().unwrap_or_else(|| PathBuf::from("."));
    base.join(CONFIG_DIR_NAME)
}

/// Path to the persisted last-project pointer.
pub fn app_config_path() -> PathBuf {
    config_dir().join("app-config.json")
}

/// Create the config dir if it does not yet exist.
pub fn ensure_config_dir() -> std::io::Result<()> {
    let dir = config_dir();
    if !dir.exists() {
        std::fs::create_dir_all(&dir)?;
    }
    Ok(())
}

/// Resolve the bundled `engine-profiles/` directory.
///
/// In a `tauri dev` / `cargo test` run the directory sits at the repo
/// root (`validation-suite/engine-profiles/`); when bundled, it is a
/// resource. We try the resource dir first, then fall back to the
/// source-relative path so dev + tests resolve without bundling.
pub fn engine_profiles_dir(resource_dir: Option<&PathBuf>) -> PathBuf {
    if let Some(rd) = resource_dir {
        let candidate = rd.join("engine-profiles");
        if candidate.is_dir() {
            return candidate;
        }
    }
    // Dev/test fallback: walk up from the current exe/CARGO_MANIFEST_DIR.
    manifest_root().join("engine-profiles")
}

/// Resolve the bundled `scenarios/<engineId>/` directory. Same
/// resolution strategy as [`engine_profiles_dir`].
pub fn scenarios_dir(engine_id: &str, resource_dir: Option<&PathBuf>) -> PathBuf {
    if let Some(rd) = resource_dir {
        let candidate = rd.join("scenarios").join(engine_id);
        if candidate.is_dir() {
            return candidate;
        }
    }
    manifest_root().join("scenarios").join(engine_id)
}

/// The app root (the `validation-suite/` dir). Resolved as the parent
/// of `CARGO_MANIFEST_DIR` (which points at `src-tauri/`), so the
/// bundled `engine-profiles/` and `scenarios/` siblings resolve
/// correctly in dev + tests regardless of the binary's working dir.
fn manifest_root() -> PathBuf {
    let crate_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    // src-tauri/ → validation-suite/
    crate_dir
        .parent()
        .map(|p| p.to_path_buf())
        .unwrap_or(crate_dir)
}
