//! Bundled scenario discovery.
//!
//! Walks `scenarios/<engineId>/**/*.json` and returns each file as raw
//! `{ source, content }`. The **frontend** core loader
//! (`packages/core`) performs all structural validation — the backend
//! only reads + parses JSON, so a single corrupt file is reported as a
//! parse error in the result rather than crashing the walk
//! (phase-1 validation: readable UI error without crashing).

use std::path::{Path, PathBuf};

use serde::Serialize;
use serde_json::Value;

use crate::paths;
use crate::schemas::ScenarioFile;

/// A discovered scenario file that could not be parsed as JSON. The
/// source path + reason are forwarded to the UI so the operator sees
/// which file is broken.
#[derive(Clone, Debug, Serialize)]
pub struct ScenarioReadError {
    pub source: String,
    pub message: String,
}

/// Result of discovering + parsing all scenario files for an engine.
#[derive(Clone, Debug, Serialize, Default)]
pub struct ScenarioReadResult {
    pub files: Vec<ScenarioFile>,
    pub errors: Vec<ScenarioReadError>,
}

/// Read every `.json` scenario file under the engine's scenario dir,
/// recursively. Unreadable/unparseable files are collected into
/// `errors` and skipped — they never abort the whole read.
pub fn read_scenarios(
    engine_id: &str,
    resource_dir: Option<&PathBuf>,
) -> ScenarioReadResult {
    let root = paths::scenarios_dir(engine_id, resource_dir);
    let mut out = ScenarioReadResult::default();
    if !root.is_dir() {
        return out;
    }
    let mut files = Vec::new();
    walk_json(&root, &root, &mut files);
    for path in files {
        let source = relative_source(&root, &path);
        let bytes = match std::fs::read(&path) {
            Ok(b) => b,
            Err(e) => {
                out.errors.push(ScenarioReadError {
                    source,
                    message: format!("Cannot read file: {e}"),
                });
                continue;
            }
        };
        let content: Value = match serde_json::from_slice(&bytes) {
            Ok(v) => v,
            Err(e) => {
                out.errors.push(ScenarioReadError {
                    source,
                    message: format!("Not valid JSON: {e}"),
                });
                continue;
            }
        };
        out.files.push(ScenarioFile { source, content });
    }
    out
}

/// Recursively collect `.json` file paths under `root`. Subdirectories
/// let scenarios be grouped by milestone (`scenarios/unity/m9/*.json`).
fn walk_json(root: &Path, dir: &Path, out: &mut Vec<PathBuf>) {
    let entries = match std::fs::read_dir(dir) {
        Ok(e) => e,
        Err(_) => return,
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            walk_json(root, &path, out);
        } else if path.extension().and_then(|e| e.to_str()) == Some("json") {
            out.push(path);
        }
    }
}

/// Relative path from the scenario root, using forward slashes for
/// stable cross-platform display (e.g. `m9/x.json`).
fn relative_source(root: &Path, path: &Path) -> String {
    match path.strip_prefix(root) {
        Ok(rel) => rel.to_string_lossy().replace(std::path::MAIN_SEPARATOR, "/"),
        Err(_) => path.to_string_lossy().to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn read_sample_scenarios_from_repo() {
        let res = read_scenarios("unity", None);
        // At least the sample scenarios ship in the repo.
        assert!(!res.files.is_empty(), "expected sample scenario files");
        // The intentional invalid file parses as JSON (it's valid JSON,
        // just semantically invalid) so it lands in `files`, not
        // `errors`; semantic validation is the frontend's job.
        assert!(res.errors.is_empty(), "no unreadable JSON expected");
    }

    #[test]
    fn missing_engine_returns_empty_without_error() {
        let res = read_scenarios("no-such-engine", None);
        assert!(res.files.is_empty());
        assert!(res.errors.is_empty());
    }
}
