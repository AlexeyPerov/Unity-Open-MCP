//! Manifest blob persistence (phase-2 deliverable: manifest recording).
//!
//! Each mutating setup step records the artifacts it created/modified as
//! a [`StepManifest`] blob under `UserSettings/ValidationSuite/manifests/`.
//! The state file only keeps the blob id (per-step `manifestRefs`); the
//! full entries live here so reset can revert deterministically.
//!
//! Blob ids are short, sortable, suffix-free tokens derived from the
//! scenario/step + a counter; they are stable only within a project's
//! manifest dir (a reset deletes the blob, so ids are not reused).

use std::fs;
use std::path::{Path, PathBuf};

use crate::persistence::atomic_write;
use crate::schemas::StepManifest;

/// Where manifest blobs live for a project: `<root>/UserSettings/ValidationSuite/manifests/`.
/// The base dir (`UserSettings/ValidationSuite/`) comes from the profile
/// `stateRoot`; we append `manifests/` to keep state file + blobs together.
pub fn manifests_dir(project_root: &Path, state_root: &str) -> PathBuf {
    project_root.join(state_root).join("manifests")
}

/// Path to a single manifest blob, keyed by id.
fn blob_path(project_root: &Path, state_root: &str, id: &str) -> PathBuf {
    manifests_dir(project_root, state_root).join(format!("{id}.json"))
}

/// A stable id for a manifest blob. Scenario + step make it human-readable
/// in the dir listing; the counter disambiguates repeated runs of the
/// same step.
pub fn make_id(scenario_id: &str, step_id: &str, counter: u64) -> String {
    // Sanitize scenario/step ids to path-safe tokens.
    let s = sanitize(scenario_id);
    let t = sanitize(step_id);
    format!("{s}-{t}-{counter}")
}

fn sanitize(s: &str) -> String {
    s.chars()
        .map(|c| if c.is_ascii_alphanumeric() || c == '-' { c } else { '-' })
        .collect()
}

/// Persist a manifest blob and return its id. The id encodes the
/// scenario/step + the caller-chosen counter (the runner passes the
/// number of manifests already stored for that step + 1).
pub fn save(
    project_root: &Path,
    state_root: &str,
    id: &str,
    manifest: &StepManifest,
) -> std::io::Result<()> {
    let path = blob_path(project_root, state_root, id);
    let json = serde_json::to_string_pretty(manifest)
        .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
    atomic_write(&path, &json)
}

/// Load a manifest blob by id. Returns `None` when the blob is absent
/// (reset is best-effort and warns rather than crashes on a missing blob).
pub fn load(
    project_root: &Path,
    state_root: &str,
    id: &str,
) -> Result<Option<StepManifest>, String> {
    let path = blob_path(project_root, state_root, id);
    if !path.exists() {
        return Ok(None);
    }
    let content = fs::read_to_string(&path)
        .map_err(|e| format!("Cannot read manifest {id}: {e}"))?;
    serde_json::from_str::<StepManifest>(&content)
        .map_err(|e| format!("Manifest {id} is malformed: {e}"))
        .map(Some)
}

/// Delete a manifest blob (after reset consumes it). Missing is not an
/// error.
pub fn delete(project_root: &Path, state_root: &str, id: &str) -> std::io::Result<()> {
    let path = blob_path(project_root, state_root, id);
    if path.exists() {
        fs::remove_file(&path)?;
    }
    Ok(())
}

/// Count how many manifest blobs already exist for a scenario/step, so
/// the runner can pass a disambiguating counter to [`make_id`]/[`save`].
pub fn count_for(project_root: &Path, state_root: &str, scenario_id: &str, step_id: &str) -> u64 {
    let dir = manifests_dir(project_root, state_root);
    let prefix = format!("{}-{}-", sanitize(scenario_id), sanitize(step_id));
    let mut max = 0u64;
    if let Ok(entries) = fs::read_dir(&dir) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            if let Some(rest) = name.strip_prefix(&prefix) {
                if let Some(num_str) = rest.strip_suffix(".json") {
                    if let Ok(n) = num_str.parse::<u64>() {
                        max = max.max(n);
                    }
                }
            }
        }
    }
    max
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schemas::{ManifestEntry, ManifestEntryKind};

    fn manifest(scenario: &str, step: &str) -> StepManifest {
        StepManifest {
            scenario_id: scenario.to_string(),
            step_id: step.to_string(),
            entries: vec![ManifestEntry {
                kind: ManifestEntryKind::Created,
                path: "Assets/_VS/x.prefab".to_string(),
                companion_path: Some("Assets/_VS/x.prefab.meta".to_string()),
                snapshot: None,
            }],
        }
    }

    #[test]
    fn save_load_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path();
        let state_root = "UserSettings/ValidationSuite/";
        let id = make_id("m9-x", "setup", 1);
        save(root, state_root, &id, &manifest("m9-x", "setup")).unwrap();
        let loaded = load(root, state_root, &id).unwrap().unwrap();
        assert_eq!(loaded.scenario_id, "m9-x");
        assert_eq!(loaded.entries.len(), 1);
        assert_eq!(loaded.entries[0].companion_path.as_deref(), Some("Assets/_VS/x.prefab.meta"));
    }

    #[test]
    fn load_returns_none_when_absent() {
        let dir = tempfile::tempdir().unwrap();
        let res = load(dir.path(), "UserSettings/ValidationSuite/", "nope").unwrap();
        assert!(res.is_none());
    }

    #[test]
    fn delete_removes_blob_and_is_idempotent() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path();
        let state_root = "UserSettings/ValidationSuite/";
        let id = make_id("m9-x", "setup", 1);
        save(root, state_root, &id, &manifest("m9-x", "setup")).unwrap();
        delete(root, state_root, &id).unwrap();
        delete(root, state_root, &id).unwrap(); // idempotent
        assert!(load(root, state_root, &id).unwrap().is_none());
    }

    #[test]
    fn count_for_disambiguates_repeated_runs() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path();
        let state_root = "UserSettings/ValidationSuite/";
        assert_eq!(count_for(root, state_root, "m9-x", "setup"), 0);
        let id1 = make_id("m9-x", "setup", count_for(root, state_root, "m9-x", "setup") + 1);
        save(root, state_root, &id1, &manifest("m9-x", "setup")).unwrap();
        assert_eq!(count_for(root, state_root, "m9-x", "setup"), 1);
        let id2 = make_id("m9-x", "setup", count_for(root, state_root, "m9-x", "setup") + 1);
        save(root, state_root, &id2, &manifest("m9-x", "setup")).unwrap();
        assert_eq!(count_for(root, state_root, "m9-x", "setup"), 2);
    }

    #[test]
    fn make_id_sanitizes_unsafe_chars() {
        let id = make_id("m9/x y", "setup:fixture", 1);
        assert!(id.starts_with("m9-x-y-setup-fixture-1"));
        // No path separators.
        assert!(!id.contains('/') && !id.contains('\\') && !id.contains(':'));
    }
}
