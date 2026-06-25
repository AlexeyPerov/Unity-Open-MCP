//! Path sandboxing for fs setup actions (phase-2 task 1).
//!
//! Every fs op the action executor performs must resolve to a path that
//! is **inside the selected project root**. This module is the single
//! chokepoint: [`resolve_within`] canonicalizes a candidate (resolving
//! `..` segments and symlinks best-effort) and rejects anything that
//! escapes the root.
//!
//! Threat model (local single-operator app):
//!  - Scenario JSON is authored/trusted by the operator, but a typo or a
//!    copied scenario must never let an action write outside the project
//!    (e.g. `../../../etc/`). We defend against `..` traversal and
//!    absolute paths that point elsewhere.
//!  - We do NOT defend against a malicious symlink already inside the
//!    project that escapes it; we canonicalize and then check the
//!    canonical path is under the canonical root, which catches the
//!    common case. A symlink created mid-run is out of scope for v1.
//!
//! The check is path-lexical + canonical where possible: we first
//! lexically join+normalize, and additionally canonicalize the parent
//! when it exists, so a non-existent target under the root still
//! validates (needed for `fs_copy` to a not-yet-created fixture dir).

use std::path::{Component, Path, PathBuf};

/// Resolve `candidate` (which may be relative to `root` or absolute under
/// it) to a canonical path, returning an error string if it is not
/// strictly inside `root`.
///
/// `candidate` may use `..` segments; these are normalized lexically
/// before the containment check. Absolute paths are accepted only when
/// they already live under `root`.
pub fn resolve_within(root: &Path, candidate: &str) -> Result<PathBuf, String> {
    let root_canon = canonicalize_root(root);
    let joined = if Path::new(candidate).is_absolute() {
        PathBuf::from(candidate)
    } else {
        root_canon.join(candidate)
    };
    let normalized = lexical_normalize(&joined);

    // For an existing path, canonicalize to follow symlinks; for a
    // not-yet-existing target (e.g. a fixture dir about to be created),
    // canonicalize the deepest existing ancestor and re-append the tail.
    let resolved = resolve_with_ancestor_canon(&normalized);
    if !is_within(&root_canon, &resolved) {
        return Err(format!(
            "Path \"{}\" resolves outside the project root ({}). fs actions are sandboxed to the project.",
            candidate,
            root_canon.display()
        ));
    }
    Ok(resolved)
}

/// Canonicalize the root, falling back to its lexical form when it
/// doesn't exist (tests build roots under tempdirs that always exist,
/// but be defensive).
fn canonicalize_root(root: &Path) -> PathBuf {
    std::fs::canonicalize(root).unwrap_or_else(|_| lexical_normalize(root))
}

/// Strip `.` and collapse `..` lexically (does not touch the filesystem).
fn lexical_normalize(path: &Path) -> PathBuf {
    let mut out: Vec<Component> = Vec::new();
    for comp in path.components() {
        match comp {
            Component::CurDir => {}
            Component::ParentDir => {
                // Pop only a normal component; keep prefix/root/`..`.
                match out.last() {
                    Some(Component::Normal(_)) => {
                        out.pop();
                    }
                    _ => out.push(comp),
                }
            }
            other => out.push(other),
        }
    }
    out.iter().collect()
}

/// Canonicalize `path`; if it doesn't exist, walk up to the nearest
/// existing ancestor, canonicalize that, then re-append the missing tail.
fn resolve_with_ancestor_canon(path: &Path) -> PathBuf {
    if path.exists() {
        return std::fs::canonicalize(path).unwrap_or_else(|_| path.to_path_buf());
    }
    // Collect the non-existing tail components.
    let mut existing = path.to_path_buf();
    let mut tail: Vec<String> = Vec::new();
    while !existing.exists() {
        let name = existing
            .file_name()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_default();
        if name.is_empty() {
            break;
        }
        tail.push(name);
        if !existing.pop() {
            break;
        }
    }
    let base = std::fs::canonicalize(&existing).unwrap_or(existing);
    let mut full = base;
    for name in tail.into_iter().rev() {
        full.push(name);
    }
    full
}

/// True when `child` equals or is a descendant of `root` (both must be
/// canonical/absolute for a trustworthy answer).
fn is_within(root: &Path, child: &Path) -> bool {
    if child == root {
        return true;
    }
    child.starts_with(root)
}

/// Convert an absolute path under `root` to a forward-slash project-relative
/// string for manifest storage (portable across OSes). Returns the original
/// string if it isn't under `root`.
pub fn to_relative(root: &Path, abs: &Path) -> String {
    match abs.strip_prefix(root) {
        Ok(rel) => rel.to_string_lossy().replace(std::path::MAIN_SEPARATOR, "/"),
        Err(_) => abs.to_string_lossy().to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn root() -> PathBuf {
        let dir = tempfile::tempdir().unwrap();
        // Canonicalize so the test root matches the form `resolve_within`
        // canonicalizes to internally (on macOS tempdirs live under a
        // `/var` → `/private/var` symlink; comparing a canonical resolved
        // path against the non-canonical tempdir path would spuriously
        // fail `starts_with`).
        std::fs::canonicalize(dir.path()).unwrap()
    }

    #[test]
    fn relative_path_under_root_resolves() {
        let root = root();
        let p = resolve_within(&root, "Assets/_ValidationSuite/x.prefab").unwrap();
        assert!(p.starts_with(&root));
        assert!(p.ends_with("Assets/_ValidationSuite/x.prefab"));
    }

    #[test]
    fn dotdot_traversal_is_rejected() {
        let root = root();
        let err = resolve_within(&root, "../../../etc/passwd").unwrap_err();
        assert!(err.contains("outside the project root"));
    }

    #[test]
    fn traversal_into_existing_target_under_root_is_ok() {
        let root = root();
        let sub = root.join("Assets/_VS");
        std::fs::create_dir_all(&sub).unwrap();
        let p = resolve_within(&root, "Assets/_VS/x.prefab").unwrap();
        assert!(p.starts_with(&root));
    }

    #[test]
    fn absolute_path_outside_root_is_rejected() {
        let root = root();
        let outside = if cfg!(windows) {
            "C:/Windows/System32/evil.dll"
        } else {
            "/etc/passwd"
        };
        let err = resolve_within(&root, outside).unwrap_err();
        assert!(err.contains("outside the project root"));
    }

    #[test]
    fn absolute_path_inside_root_is_accepted() {
        let root = root();
        let inside = root.join("Assets/a.prefab");
        let p = resolve_within(&root, &inside.to_string_lossy()).unwrap();
        assert!(p.starts_with(&root));
    }

    #[test]
    fn nested_dotdot_collapses_to_within_root() {
        let root = root();
        // a/../b -> b, still under root.
        let p = resolve_within(&root, "Assets/../Assets/x.prefab").unwrap();
        assert!(p.starts_with(&root));
        assert!(p.ends_with("Assets/x.prefab"));
    }

    #[test]
    fn to_relative_strips_root_with_forward_slashes() {
        let root = root();
        let abs = root.join("Assets/_VS/x.prefab");
        let rel = to_relative(&root, &abs);
        assert_eq!(rel, "Assets/_VS/x.prefab");
    }
}
