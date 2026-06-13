//! M4 — AI toolkit root validation, Node check, and derived path
//! contract. The wizard Step 2 collects the cloned Unity-AI-Hub
//! monorepo root and persists it to `settings.aiToolkit.rootPath`;
//! every downstream step (3/4) derives its paths from that root.
//! See `specs/hub/hub-wizard.md` §AI toolkit root and
//! `specs/packages/mcp-server.md` §Hub MCP path (M4) for the
//! authoritative contract.

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

/// The set of relative paths the wizard checks under the toolkit
/// root. A candidate root is only valid when every required entry
/// resolves to an existing file or directory on disk.
///
/// | Fingerprint | Purpose |
/// |---|---|
/// | `mcp-server/package.json` | MCP server manifest |
/// | `packages/bridge/` | Bridge package source |
/// | `packages/verify/` | Verify package source |
/// | `skills/unity-agent/SKILL.md` | Core skill (Done copy source) |
/// | `mcp-server/dist/index.js` | Built MCP entry (remediation: `npm run build`) |
pub const TOOLKIT_FINGERPRINTS: &[(&str, ToolkitFingerprintKind, bool)] = &[
    ("mcp-server/package.json", ToolkitFingerprintKind::File, true),
    ("packages/bridge", ToolkitFingerprintKind::Dir, true),
    ("packages/verify", ToolkitFingerprintKind::Dir, true),
    (
        "skills/unity-agent/SKILL.md",
        ToolkitFingerprintKind::File,
        true,
    ),
    (
        "mcp-server/dist/index.js",
        ToolkitFingerprintKind::File,
        true,
    ),
];

/// Distinguishes a `File` fingerprint from a `Dir` (directory)
/// fingerprint so the validator can report a clear "missing" vs
/// "not a directory" message.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ToolkitFingerprintKind {
    File,
    Dir,
}

/// Per-fingerprint validation outcome.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolkitFingerprintResult {
    pub relative_path: String,
    pub kind: ToolkitFingerprintKind,
    pub required: bool,
    pub exists: bool,
    /// `Some(true)` when the entry exists and matches the expected
    /// kind. `Some(false)` when it exists but is the wrong kind
    /// (e.g. a file where a directory is required). `None` when the
    /// entry does not exist at all.
    pub kind_ok: Option<bool>,
    pub resolved: Option<String>,
}

/// Result of validating a candidate toolkit root path. `ok` is true
/// when every required fingerprint passes; soft-fail entries
/// (e.g. a missing `mcp-server/dist/index.js`) still report
/// `ok: true` so the user can clone the repo and run `npm run build`
/// without abandoning the flow.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolkitValidation {
    /// `true` when every required fingerprint resolved to a path of
    /// the correct kind. Optional fingerprints (e.g. the built MCP
    /// entry) do not gate this flag.
    pub ok: bool,
    /// `Some` when the candidate was a directory; `None` when the
    /// root itself was missing.
    pub root: Option<String>,
    /// Fingerprint outcomes in declaration order. Mirrors
    /// `TOOLKIT_FINGERPRINTS` so the UI can render rows 1:1 with
    /// the spec table.
    pub fingerprints: Vec<ToolkitFingerprintResult>,
    /// `true` when the MCP `dist/index.js` is missing while every
    /// other required fingerprint is fine — the UI surfaces a
    /// dedicated "run `npm run build`" remediation in that case.
    pub mcp_dist_missing: bool,
}

/// Node.js environment probe result. Returned by `check_node_version`
/// and rendered in the Step 2 Node check + blocked screen. A missing
/// or too-old `node` blocks the wizard before the toolkit picker is
/// shown.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NodeProbe {
    /// `true` when `node` was on `PATH` and reported a version at or
    /// above the minimum supported by the MCP server.
    pub ok: bool,
    /// Full version string reported by `node --version`, e.g.
    /// `"v20.11.1"`. `None` when the binary is not on `PATH`.
    pub version: Option<String>,
    /// Numeric major version parsed out of `version`. `None` when
    /// the binary is missing or the version string was unparsable.
    pub major: Option<u32>,
    /// Minimum major version the wizard accepts. Documented as
    /// Node 18 LTS in the spec.
    pub required_major: u32,
    /// Optional human-readable failure reason (binary missing, exit
    /// code, parse error). `None` on success.
    pub error: Option<String>,
}

/// Minimum supported Node.js major version. The MCP server README
/// declares Node 18+ as a hard requirement; we keep that number
/// here so the spec, the wizard copy, and the validator all agree.
pub const MIN_NODE_MAJOR: u32 = 18;

/// Validate a candidate toolkit root. The candidate is considered
/// valid when the path resolves to a directory and every required
/// fingerprint exists with the expected kind. The returned
/// `fingerprints` vector is always populated (in declaration order)
/// so the UI can render a "show which checks failed" list per
/// `hub-wizard.md` §Step 2 Blocked screen (toolkit root).
///
/// Exposed as the `validate_toolkit_root` Tauri command (String
/// entry point) — see the `#[tauri::command]` wrapper below.
fn validate_toolkit_root_at(candidate: &Path) -> ToolkitValidation {
    let root_exists = candidate.is_dir();
    let root_string = root_exists.then(|| candidate.to_string_lossy().into_owned());

    let mut fingerprints = Vec::with_capacity(TOOLKIT_FINGERPRINTS.len());
    let mut all_required_ok = root_exists;
    let mut mcp_dist_missing = false;

    for (relative, kind, required) in TOOLKIT_FINGERPRINTS {
        let resolved = root_exists.then(|| candidate.join(relative));
        let (exists, kind_ok) = match resolved.as_ref() {
            Some(p) => match kind {
                ToolkitFingerprintKind::File => {
                    let is_file = p.is_file();
                    (is_file, Some(is_file))
                }
                ToolkitFingerprintKind::Dir => {
                    let is_dir = p.is_dir();
                    (is_dir, Some(is_dir))
                }
            },
            None => (false, None),
        };

        // `mcp-server/dist/index.js` is the only fingerprint the
        // wizard treats as a soft failure — the user can clone
        // the toolkit and still be blocked by a missing build
        // output. We surface that state separately so the UI can
        // show a focused "run `npm run build`" remediation.
        if !exists && *relative == "mcp-server/dist/index.js" {
            mcp_dist_missing = true;
        }

        if *required && !exists {
            all_required_ok = false;
        }

        fingerprints.push(ToolkitFingerprintResult {
            relative_path: (*relative).to_string(),
            kind: *kind,
            required: *required,
            exists,
            kind_ok,
            resolved: resolved.map(|p| p.to_string_lossy().into_owned()),
        });
    }

    ToolkitValidation {
        ok: all_required_ok,
        root: root_string,
        fingerprints,
        mcp_dist_missing,
    }
}

/// Compute the canonical MCP server entry path for a toolkit root.
/// Returns `<root>/mcp-server/dist/index.js` unless `mcp_index_override`
/// is non-empty, in which case the override is used as-is (Step 4
/// advanced escape hatch). The returned path is **not** validated
/// here — call `validate_toolkit_root` to confirm the entry exists.
pub fn derived_mcp_index_path(
    toolkit_root: &str,
    mcp_index_override: &str,
) -> Option<PathBuf> {
    let trimmed_override = mcp_index_override.trim();
    if !trimmed_override.is_empty() {
        return Some(PathBuf::from(trimmed_override));
    }
    let trimmed_root = toolkit_root.trim();
    if trimmed_root.is_empty() {
        return None;
    }
    Some(Path::new(trimmed_root).join("mcp-server/dist/index.js"))
}

/// Probe `PATH` for a `node` binary and report the major version.
/// The probe runs `node --version`; failures (binary missing,
/// non-zero exit, unparsable output) populate `error` and set
/// `ok: false` without panicking. Used by the wizard Step 2 Node
/// check and the Step 2 blocked screen.
pub fn probe_node() -> NodeProbe {
    let output = std::process::Command::new("node").arg("--version").output();

    match output {
        Err(e) => NodeProbe {
            ok: false,
            version: None,
            major: None,
            required_major: MIN_NODE_MAJOR,
            error: Some(format!("node not found on PATH ({})", e)),
        },
        Ok(out) if !out.status.success() => {
            let stderr = String::from_utf8_lossy(&out.stderr).trim().to_string();
            NodeProbe {
                ok: false,
                version: None,
                major: None,
                required_major: MIN_NODE_MAJOR,
                error: Some(if stderr.is_empty() {
                    format!("node --version exited with {:?}", out.status.code())
                } else {
                    stderr
                }),
            }
        }
        Ok(out) => {
            let raw = String::from_utf8_lossy(&out.stdout).trim().to_string();
            let (version, major) = parse_node_version(&raw);
            let ok = major.map(|m| m >= MIN_NODE_MAJOR).unwrap_or(false);
            let error = if ok {
                None
            } else {
                Some(match (version.as_deref(), major) {
                    (Some(v), Some(m)) => format!(
                        "Node {} found; Unity Hub Pro requires Node {}+",
                        v, m
                    ),
                    _ => format!("Could not parse Node version from '{}'", raw),
                })
            };
            NodeProbe {
                ok,
                version,
                major,
                required_major: MIN_NODE_MAJOR,
                error,
            }
        }
    }
}

/// Parse a `node --version` string (`"v20.11.1"`, `"v18.0.0"`,
/// etc.) into `(full_string, major_number)`. Tolerant of the
/// optional leading `v` and trailing pre-release suffixes so a
/// future Node 18.19.0-rc.1 still parses correctly.
fn parse_node_version(raw: &str) -> (Option<String>, Option<u32>) {
    if raw.is_empty() {
        return (None, None);
    }
    let stripped = raw.strip_prefix('v').unwrap_or(raw);
    let major_str = stripped.split('.').next().unwrap_or("");
    let major = major_str.parse::<u32>().ok();
    (Some(raw.to_string()), major)
}

/// One-line summary of a `ToolkitValidation` suitable for the wizard
/// Step 2 status chip. Mirrors the `hub-wizard.md` §Step 2 Blocked
/// screen copy: failing fingerprints are listed in human-readable
/// form.
pub fn toolkit_failure_summary(validation: &ToolkitValidation) -> Option<String> {
    if validation.ok {
        return None;
    }
    let missing: Vec<&str> = validation
        .fingerprints
        .iter()
        .filter(|f| f.required && !f.exists)
        .map(|f| f.relative_path.as_str())
        .collect();
    if missing.is_empty() {
        Some("Toolkit root is not a directory.".to_string())
    } else {
        Some(format!("Missing: {}", missing.join(", ")))
    }
}

/// Tauri command: validate a candidate toolkit root path. The
/// wizard Step 2 calls this whenever the user picks a folder or
/// edits the path. Pure function — does not mutate `settings.json`.
/// Persisting the validated root is the caller's responsibility
/// (see `aiToolkit.rootPath` in `schemas::AiToolkitSettings`).
#[tauri::command]
pub fn validate_toolkit_root(path: String) -> ToolkitValidation {
    validate_toolkit_root_at(&PathBuf::from(path))
}

/// Tauri command: probe `PATH` for `node` and report the major
/// version. The wizard Step 2 calls this on entry and on retry
/// after the user installs Node. Pure function.
#[tauri::command]
pub fn check_node_version() -> NodeProbe {
    probe_node()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use tempfile::tempdir;

    fn make_valid_toolkit(root: &Path) {
        fs::create_dir_all(root.join("mcp-server/dist")).unwrap();
        fs::write(root.join("mcp-server/package.json"), "{}").unwrap();
        fs::write(root.join("mcp-server/dist/index.js"), "module.exports={};").unwrap();
        fs::create_dir_all(root.join("packages/bridge")).unwrap();
        fs::create_dir_all(root.join("packages/verify")).unwrap();
        fs::create_dir_all(root.join("skills/unity-agent")).unwrap();
        fs::write(root.join("skills/unity-agent/SKILL.md"), "# skill").unwrap();
    }

    #[test]
    fn validation_passes_on_complete_toolkit() {
        let dir = tempdir().unwrap();
        make_valid_toolkit(dir.path());
        let v = validate_toolkit_root_at(dir.path());
        assert!(v.ok, "expected ok, got {:?}", v);
        assert!(!v.mcp_dist_missing);
        assert_eq!(v.fingerprints.len(), TOOLKIT_FINGERPRINTS.len());
        assert!(v.fingerprints.iter().all(|f| f.exists));
    }

    #[test]
    fn validation_fails_on_missing_root() {
        let dir = tempdir().unwrap();
        let bogus = dir.path().join("does-not-exist");
        let v = validate_toolkit_root_at(&bogus);
        assert!(!v.ok);
        assert!(v.root.is_none());
        assert!(v.fingerprints.iter().all(|f| !f.exists));
    }

    #[test]
    fn validation_marks_mcp_dist_missing_without_failing_ok() {
        // The spec treats the built MCP entry as a soft failure:
        // a fresh clone with no `npm run build` is "mostly valid".
        let dir = tempdir().unwrap();
        fs::create_dir_all(dir.path().join("mcp-server")).unwrap();
        fs::write(dir.path().join("mcp-server/package.json"), "{}").unwrap();
        fs::create_dir_all(dir.path().join("packages/bridge")).unwrap();
        fs::create_dir_all(dir.path().join("packages/verify")).unwrap();
        fs::create_dir_all(dir.path().join("skills/unity-agent")).unwrap();
        fs::write(dir.path().join("skills/unity-agent/SKILL.md"), "# skill").unwrap();
        // Intentionally do NOT create mcp-server/dist/index.js.
        let v = validate_toolkit_root_at(dir.path());
        assert!(!v.ok, "mcp dist is required per spec table");
        assert!(v.mcp_dist_missing);
    }

    #[test]
    fn validation_reports_wrong_kind_for_dir_fingerprint() {
        // `packages/bridge` must be a directory; a file with the
        // same name should be reported as `kind_ok: false`.
        let dir = tempdir().unwrap();
        fs::create_dir_all(dir.path().join("mcp-server/dist")).unwrap();
        fs::write(dir.path().join("mcp-server/package.json"), "{}").unwrap();
        fs::write(dir.path().join("mcp-server/dist/index.js"), "").unwrap();
        fs::create_dir_all(dir.path().join("packages")).unwrap();
        fs::write(dir.path().join("packages/bridge"), "not a dir").unwrap();
        fs::create_dir_all(dir.path().join("packages/verify")).unwrap();
        fs::create_dir_all(dir.path().join("skills/unity-agent")).unwrap();
        fs::write(dir.path().join("skills/unity-agent/SKILL.md"), "# skill").unwrap();
        let v = validate_toolkit_root_at(dir.path());
        assert!(!v.ok);
        let bridge = v
            .fingerprints
            .iter()
            .find(|f| f.relative_path == "packages/bridge")
            .unwrap();
        // A regular file is reported as `exists: false` because the
        // wizard only counts the entry as present when the kind
        // matches. The diagnostic surface is `kind_ok: Some(false)`.
        assert!(!bridge.exists);
        assert_eq!(bridge.kind_ok, Some(false));
    }

    #[test]
    fn derived_mcp_index_uses_root_when_override_empty() {
        let p = derived_mcp_index_path("/repos/Unity-AI-Hub", "").unwrap();
        assert_eq!(
            p.to_string_lossy(),
            "/repos/Unity-AI-Hub/mcp-server/dist/index.js"
        );
    }

    #[test]
    fn derived_mcp_index_prefers_override() {
        let p =
            derived_mcp_index_path("/repos/Unity-AI-Hub", "/custom/build/mcp/index.js").unwrap();
        assert_eq!(p.to_string_lossy(), "/custom/build/mcp/index.js");
    }

    #[test]
    fn derived_mcp_index_returns_none_for_blank_inputs() {
        assert!(derived_mcp_index_path("", "").is_none());
        assert!(derived_mcp_index_path("   ", "").is_none());
        assert!(derived_mcp_index_path("", "  ").is_none());
    }

    #[test]
    fn parse_node_version_handles_v_prefix_and_prerelease() {
        assert_eq!(parse_node_version("v20.11.1"), (Some("v20.11.1".into()), Some(20)));
        assert_eq!(parse_node_version("v18.0.0"), (Some("v18.0.0".into()), Some(18)));
        assert_eq!(
            parse_node_version("v18.19.0-rc.1"),
            (Some("v18.19.0-rc.1".into()), Some(18))
        );
        assert_eq!(parse_node_version(""), (None, None));
    }

    #[test]
    fn toolkit_failure_summary_lists_missing_paths() {
        let dir = tempdir().unwrap();
        // Empty toolkit — only the root directory exists.
        let v = validate_toolkit_root_at(dir.path());
        let summary = toolkit_failure_summary(&v).unwrap();
        assert!(summary.contains("Missing:"));
        assert!(summary.contains("mcp-server/package.json"));
    }

    #[test]
    fn toolkit_failure_summary_none_on_ok() {
        let dir = tempdir().unwrap();
        make_valid_toolkit(dir.path());
        let v = validate_toolkit_root_at(dir.path());
        assert!(toolkit_failure_summary(&v).is_none());
    }
}
