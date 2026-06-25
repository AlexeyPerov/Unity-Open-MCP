//! Filesystem setup actions (phase-2 tasks 2–4).
//!
//! Implements `fs_copy`, `fs_patch`, and `fs_delete` with project-root
//! sandboxing (delegated to [`crate::sandbox`]) and companion-artifact
//! tracking (Unity `.meta`). Each op returns an [`ActionResult`] whose
//! `entries` feed the step manifest; reset consumes those entries to
//! revert deterministically (phase-2 → revert strategy: snapshot for
//! patches, delete for created artifacts).
//!
//! Patching mirrors the core TS transform (`packages/core/src/patch.ts`)
//! line-for-line so the executor and the unit tests agree on exact
//! semantics. The patch-op vocabulary is pinned and validated at load
//! time, so this code only implements the four known ops.

use std::fs;
use std::path::{Path, PathBuf};

use crate::sandbox;
use crate::schemas::{
    ActionResult, ActionLogLine, ActionLogLevel, CompanionRule, ManifestEntry,
    ManifestEntryKind, McpResult,
};

/// Resolve a project-relative path string to an absolute, sandboxed path.
fn resolve(root: &Path, rel: &str) -> Result<PathBuf, String> {
    sandbox::resolve_within(root, rel)
}

/// Convert an absolute (under-root) path to a project-relative forward-slash string.
fn rel_of(root: &Path, abs: &Path) -> String {
    sandbox::to_relative(root, abs)
}

/// Companion path for a primary, per the profile rules (e.g. `.meta`).
/// Returns the companion's project-relative path when the companion
/// *source* exists (for copies) or when explicit tracking is requested.
fn companion_rel(primary_rel: &str, rules: &[CompanionRule]) -> Option<String> {
    // Match the primary extension against a rule's `primary` glob tail.
    let lower = primary_rel.to_lowercase();
    for rule in rules {
        let prim = rule.primary.trim_start_matches('*');
        if lower.ends_with(&prim.to_lowercase()) {
            // The companion is the primary path with the primary ext
            // replaced by the companion suffix (e.g. a.prefab → a.prefab.meta).
            return Some(format!("{}{}", primary_rel, rule.companion.trim_start_matches('*').trim_start_matches(prim)));
        }
    }
    None
}

/// Build the companion relative path robustly: take the primary, and if
/// a rule matches, append the companion suffix. We derive it from the
/// rule's `companion` glob (e.g. `*.prefab.meta`) by stripping the
/// primary ext prefix from the companion glob.
fn companion_rel_from(primary_rel: &str, rules: &[CompanionRule]) -> Option<String> {
    let lower = primary_rel.to_lowercase();
    for rule in rules {
        let prim_ext = rule.primary.trim_start_matches('*'); // ".prefab"
        if lower.ends_with(&prim_ext.to_lowercase()) {
            // companion like "*.prefab.meta" → append ".meta" after the
            // full primary path (primary already ends with the ext).
            let comp_ext = rule.companion.trim_start_matches('*'); // ".prefab.meta"
            let extra = comp_ext.strip_prefix(prim_ext).unwrap_or(comp_ext); // ".meta"
            return Some(format!("{primary_rel}{extra}"));
        }
    }
    None
}

// ── fs_copy ──────────────────────────────────────────────────────────────────

/// Copy a file or directory tree from `from_rel` to `to_rel` (both
/// project-relative). Auto-tracks the companion (`.meta`) when the source
/// companion exists. Creates parent dirs of the destination.
pub fn fs_copy(
    root: &Path,
    from_rel: &str,
    to_rel: &str,
    companions: &[CompanionRule],
) -> Result<ActionResult, String> {
    let from = resolve(root, from_rel)?;
    let to = resolve(root, to_rel)?;
    if !from.exists() {
        return Ok(ActionResult::err(
            format!("fs_copy skipped: source missing ({from_rel})"),
            format!("Source not found: {from_rel}"),
        ));
    }
    if let Some(parent) = to.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("create dirs: {e}"))?;
    }
    let mut entries = Vec::new();
    let copied = if from.is_dir() {
        copy_dir(&from, &to).map_err(|e| format!("copy dir: {e}"))?
    } else {
        fs::copy(&from, &to).map_err(|e| format!("copy file: {e}"))?;
        1
    };
    entries.push(ManifestEntry {
        kind: ManifestEntryKind::Created,
        path: rel_of(root, &to),
        companion_path: None,
        snapshot: None,
    });

    // Companion copy: only when the source companion exists.
    if let Some(comp_rel) = companion_rel_from(from_rel, companions) {
        let comp_from = resolve(root, &comp_rel)?;
        if comp_from.exists() {
            let comp_to_rel = companion_rel_from(to_rel, companions).unwrap_or_else(|| format!("{to_rel}.meta"));
            let comp_to = resolve(root, &comp_to_rel)?;
            if let Some(parent) = comp_to.parent() {
                fs::create_dir_all(parent).map_err(|e| format!("create companion dirs: {e}"))?;
            }
            fs::copy(&comp_from, &comp_to).map_err(|e| format!("copy companion: {e}"))?;
            // Attach the companion to the primary entry.
            entries[0].companion_path = Some(rel_of(root, &comp_to));
        }
    }

    Ok(ActionResult {
        ok: true,
        summary: format!("copied {copied} file(s) → {to_rel}"),
        logs: vec![ActionLogLine {
            level: ActionLogLevel::Info,
            message: format!("fs_copy {from_rel} → {to_rel}"),
            snippet: None,
        }],
        entries,
        mcp: None,
    })
}

/// Recursively copy a directory tree, returning the file count.
fn copy_dir(src: &Path, dst: &Path) -> std::io::Result<usize> {
    fs::create_dir_all(dst)?;
    let mut count = 0;
    for entry in fs::read_dir(src)? {
        let entry = entry?;
        let from = entry.path();
        let to = dst.join(entry.file_name());
        if from.is_dir() {
            count += copy_dir(&from, &to)?;
        } else {
            fs::copy(&from, &to)?;
            count += 1;
        }
    }
    Ok(count)
}

// ── fs_patch ─────────────────────────────────────────────────────────────────

/// Patches understood by `fs_patch` (pinned). Mirrors the core TS vocab.
#[derive(Clone, Debug)]
pub enum PatchOp {
    ReplaceLineContains { r#match: String, replace: String },
    InsertAfterLineContains { r#match: String, insert: String },
    InsertBeforeLineContains { r#match: String, insert: String },
    TrimTrailingWhitespace,
}

/// Parse a raw JSON patch entry into a typed op, validating the op name.
/// Returns an error string for an unknown op (defensive; the loader
/// already rejects unknown ops at load time).
pub fn parse_patch(entry: &serde_json::Value) -> Result<PatchOp, String> {
    let op = entry
        .get("op")
        .and_then(|v| v.as_str())
        .ok_or("patch missing \"op\"")?;
    let match_str = || -> Result<String, String> {
        entry
            .get("match")
            .and_then(|v| v.as_str())
            .map(|s| s.to_string())
            .ok_or_else(|| format!("op {op} needs \"match\""))
    };
    match op {
        "replace_line_contains" => Ok(PatchOp::ReplaceLineContains {
            r#match: match_str()?,
            replace: entry
                .get("replace")
                .and_then(|v| v.as_str())
                .ok_or("replace_line_contains needs \"replace\"")?
                .to_string(),
        }),
        "insert_after_line_contains" => Ok(PatchOp::InsertAfterLineContains {
            r#match: match_str()?,
            insert: json_string(entry.get("insert"), "insert")?,
        }),
        "insert_before_line_contains" => Ok(PatchOp::InsertBeforeLineContains {
            r#match: match_str()?,
            insert: json_string(entry.get("insert"), "insert")?,
        }),
        "trim_trailing_whitespace" => Ok(PatchOp::TrimTrailingWhitespace),
        other => Err(format!("Unknown patch op \"{other}\"")),
    }
}

fn json_string(v: Option<&serde_json::Value>, field: &str) -> Result<String, String> {
    v.and_then(|v| v.as_str())
        .map(|s| s.to_string())
        .ok_or_else(|| format!("patch needs \"{field}\""))
}

/// Apply `fs_patch` to `path_rel`. Snapshots the pre-patch file contents
/// into the manifest entry so reset can restore it verbatim (phase-2 →
/// revert strategy: snapshot). When `snapshot_override` is `Some`, the
/// file is written back to that exact content (reset path) and no patches
/// are applied.
pub fn fs_patch(
    root: &Path,
    path_rel: &str,
    patches: &[serde_json::Value],
    snapshot_override: Option<&str>,
) -> Result<ActionResult, String> {
    let path = resolve(root, path_rel)?;
    if !path.is_file() {
        return Ok(ActionResult::err(
            format!("fs_patch skipped: target missing ({path_rel})"),
            format!("Patch target not found: {path_rel}"),
        ));
    }
    let original = fs::read_to_string(&path).map_err(|e| format!("read patch target: {e}"))?;

    let (new_content, snapshot, summary) = match snapshot_override {
        Some(content) => (content.to_string(), None, format!("restored snapshot: {path_rel}")),
        None => {
            let mut typed: Vec<PatchOp> = Vec::new();
            for raw in patches {
                typed.push(parse_patch(raw)?);
            }
            let patched = apply_patches(&original, &typed)
                .map_err(|e| format!("apply patches: {e}"))?;
            (patched, Some(original), format!("patched {path_rel} ({} op(s))", typed.len()))
        }
    };

    // Write via temp+rename for crash-safety (mirrors persistence pattern).
    crate::persistence::atomic_write(&path, &new_content).map_err(|e| format!("write patched file: {e}"))?;

    let entry = ManifestEntry {
        kind: ManifestEntryKind::Modified,
        path: rel_of(root, &path),
        companion_path: None,
        snapshot,
    };

    Ok(ActionResult {
        ok: true,
        summary,
        logs: vec![ActionLogLine {
            level: ActionLogLevel::Info,
            message: format!("fs_patch {path_rel}"),
            snippet: None,
        }],
        entries: vec![entry],
        mcp: None,
    })
}

/// Pure patch transform — mirrors `packages/core/src/patch.ts`.
fn apply_patches(text: &str, patches: &[PatchOp]) -> Result<String, String> {
    let mut lines = split_lines(text);
    for patch in patches {
        match patch {
            PatchOp::ReplaceLineContains { r#match, replace } => {
                let idx = find_line(&lines, r#match);
                if let Some(idx) = idx {
                    let had_nl = lines[idx].ends_with('\n');
                    lines[idx] = if had_nl { format!("{replace}\n") } else { replace.clone() };
                } else {
                    return Err(format!("replace_line_contains: no line matched \"{match}\"."));
                }
            }
            PatchOp::InsertAfterLineContains { r#match, insert } => {
                let idx = find_line(&lines, r#match);
                if let Some(idx) = idx {
                    let insert_lines = split_lines(&ensure_trailing_nl(insert));
                    // splice after idx
                    let mut at = idx + 1;
                    for l in insert_lines {
                        lines.insert(at, l);
                        at += 1;
                    }
                } else {
                    return Err(format!("insert_after_line_contains: no line matched \"{match}\"."));
                }
            }
            PatchOp::InsertBeforeLineContains { r#match, insert } => {
                let idx = find_line(&lines, r#match);
                if let Some(idx) = idx {
                    let insert_lines = split_lines(&ensure_trailing_nl(insert));
                    let mut at = idx;
                    for l in insert_lines {
                        lines.insert(at, l);
                        at += 1;
                    }
                } else {
                    return Err(format!("insert_before_line_contains: no line matched \"{match}\"."));
                }
            }
            PatchOp::TrimTrailingWhitespace => {
                lines = lines
                    .iter()
                    .map(|l| {
                        let nl = if l.ends_with('\n') { "\n" } else { "" };
                        let content = l.strip_suffix('\n').unwrap_or(l);
                        let trimmed = content.trim_end_matches([' ', '\t']);
                        format!("{trimmed}{nl}")
                    })
                    .collect();
            }
        }
    }
    Ok(lines.join(""))
}

/// Split into lines keeping the trailing `\n` on each (mirrors TS splitLines).
fn split_lines(text: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut start = 0;
    let bytes = text.as_bytes();
    for (i, &b) in bytes.iter().enumerate() {
        if b == b'\n' {
            out.push(text[start..=i].to_string());
            start = i + 1;
        }
    }
    if start < text.len() {
        out.push(text[start..].to_string());
    }
    out
}

fn ensure_trailing_nl(s: &str) -> String {
    if s.ends_with('\n') { s.to_string() } else { format!("{s}\n") }
}

/// First index whose line content (sans newline) contains `needle`.
fn find_line(lines: &[String], needle: &str) -> Option<usize> {
    lines.iter().position(|l| {
        let content = l.strip_suffix('\n').unwrap_or(l);
        content.contains(needle)
    })
}

// ── fs_delete ────────────────────────────────────────────────────────────────

/// Delete a list of project-relative paths. Files and directories are
/// both handled. Missing paths are tolerated (reset is best-effort).
/// Used by reset on manifest-listed paths only — never deletes outside
/// the manifest (no heuristic deletes; idea.md → Reset contract).
pub fn fs_delete(root: &Path, paths_rel: &[String]) -> Result<ActionResult, String> {
    let mut deleted = 0usize;
    let mut logs = Vec::new();
    for rel in paths_rel {
        let abs = resolve(root, rel)?;
        if abs.is_dir() {
            fs::remove_dir_all(&abs).map_err(|e| format!("delete dir {rel}: {e}"))?;
            deleted += 1;
            logs.push(ActionLogLine {
                level: ActionLogLevel::Info,
                message: format!("deleted dir {rel}"),
                snippet: None,
            });
        } else if abs.is_file() {
            fs::remove_file(&abs).map_err(|e| format!("delete file {rel}: {e}"))?;
            deleted += 1;
            logs.push(ActionLogLine {
                level: ActionLogLevel::Info,
                message: format!("deleted file {rel}"),
                snippet: None,
            });
        }
        // Missing path: ignore (best-effort reset).
    }
    Ok(ActionResult {
        ok: true,
        summary: format!("deleted {deleted} path(s)"),
        logs,
        entries: Vec::new(),
        mcp: None,
    })
}

/// Restore a modified file from its snapshot (reset path). Public so the
/// runner can call it directly from a manifest entry without going
/// through `fs_patch`'s patch logic.
pub fn restore_snapshot(root: &Path, path_rel: &str, snapshot: &str) -> Result<(), String> {
    let path = resolve(root, path_rel)?;
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("restore create dirs: {e}"))?;
    }
    crate::persistence::atomic_write(&path, snapshot).map_err(|e| format!("restore write: {e}"))
}

// Unused but kept for completeness/tests of the companion helper.
#[allow(dead_code)]
pub fn companion_for(primary_rel: &str, companions: &[CompanionRule]) -> Option<String> {
    companion_rel(primary_rel, companions)
}

/// Build a synthetic ok result (used by the MCP runner + manual helpers).
pub fn synthetic_ok(summary: &str, logs: Vec<ActionLogLine>, mcp: Option<McpResult>) -> ActionResult {
    ActionResult {
        ok: true,
        summary: summary.to_string(),
        logs,
        entries: Vec::new(),
        mcp,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn rule(ext: &str, comp_ext: &str) -> CompanionRule {
        CompanionRule {
            primary: format!("*{ext}"),
            companion: format!("*{comp_ext}"),
        }
    }

    fn write(root: &Path, rel: &str, content: &str) {
        let p = root.join(rel);
        if let Some(parent) = p.parent() {
            fs::create_dir_all(parent).unwrap();
        }
        fs::write(p, content).unwrap();
    }

    /// Canonicalized tempdir root, matching how `select_project` scopes
    /// the project root (fs ops resolve canonical paths internally).
    fn mkroot() -> PathBuf {
        let dir = tempfile::tempdir().unwrap();
        std::fs::canonicalize(dir.path()).unwrap()
    }

    #[test]
    fn fs_copy_file_records_created_entry() {
        let root = mkroot();
        write(&root, "Assets/Src.prefab", "data");
        let res = fs_copy(&root, "Assets/Src.prefab", "Assets/_VS/Dst.prefab", &[rule(".prefab", ".prefab.meta")]).unwrap();
        assert!(res.ok);
        assert_eq!(res.entries.len(), 1);
        assert_eq!(res.entries[0].kind, ManifestEntryKind::Created);
        assert!(root.join("Assets/_VS/Dst.prefab").is_file());
    }

    #[test]
    fn fs_copy_tracks_companion_when_source_exists() {
        let root = mkroot();
        write(&root, "Assets/Src.prefab", "data");
        write(&root, "Assets/Src.prefab.meta", "guid");
        let res = fs_copy(&root, "Assets/Src.prefab", "Assets/_VS/Dst.prefab", &[rule(".prefab", ".prefab.meta")]).unwrap();
        assert_eq!(res.entries[0].companion_path.as_deref(), Some("Assets/_VS/Dst.prefab.meta"));
        assert!(root.join("Assets/_VS/Dst.prefab.meta").is_file());
    }

    #[test]
    fn fs_copy_skips_companion_when_source_absent() {
        let root = mkroot();
        write(&root, "Assets/Src.prefab", "data");
        let res = fs_copy(&root, "Assets/Src.prefab", "Assets/_VS/Dst.prefab", &[rule(".prefab", ".prefab.meta")]).unwrap();
        assert!(res.entries[0].companion_path.is_none());
        assert!(!root.join("Assets/_VS/Dst.prefab.meta").exists());
    }

    #[test]
    fn fs_copy_rejects_traversal_outside_root() {
        let root = mkroot();
        write(&root, "Assets/Src.prefab", "data");
        let err = fs_copy(&root, "Assets/Src.prefab", "../../outside.prefab", &[rule(".prefab", ".prefab.meta")]).unwrap_err();
        assert!(err.contains("outside the project root"));
    }

    #[test]
    fn fs_copy_directory_tree() {
        let root = mkroot();
        write(&root, "Assets/Src/a.prefab", "a");
        write(&root, "Assets/Src/b.prefab", "b");
        let res = fs_copy(&root, "Assets/Src", "Assets/_VS/Dst", &[]).unwrap();
        assert!(res.ok);
        assert!(root.join("Assets/_VS/Dst/a.prefab").is_file());
        assert!(root.join("Assets/_VS/Dst/b.prefab").is_file());
    }

    #[test]
    fn fs_patch_snapshots_and_applies_replace() {
        let root = mkroot();
        write(&root, "Assets/_VS/x.prefab", "  m_Name: Player\n  m_Script: 1\n");
        let patches = vec![json!({ "op": "replace_line_contains", "match": "m_Name:", "replace": "  m_Name: PlayerPatched" })];
        let res = fs_patch(&root, "Assets/_VS/x.prefab", &patches, None).unwrap();
        assert!(res.ok);
        assert_eq!(res.entries[0].kind, ManifestEntryKind::Modified);
        assert_eq!(res.entries[0].snapshot.as_deref(), Some("  m_Name: Player\n  m_Script: 1\n"));
        assert_eq!(fs::read_to_string(root.join("Assets/_VS/x.prefab")).unwrap(), "  m_Name: PlayerPatched\n  m_Script: 1\n");
    }

    #[test]
    fn fs_patch_insert_after_and_before() {
        let root = mkroot();
        write(&root, "Assets/_VS/x.prefab", "%YAML 1.1\n---\n");
        let after = vec![json!({ "op": "insert_after_line_contains", "match": "%YAML", "insert": "# after" })];
        fs_patch(&root, "Assets/_VS/x.prefab", &after, None).unwrap();
        assert_eq!(fs::read_to_string(root.join("Assets/_VS/x.prefab")).unwrap(), "%YAML 1.1\n# after\n---\n");

        write(&root, "Assets/_VS/x.prefab", "%YAML 1.1\n---\n");
        let before = vec![json!({ "op": "insert_before_line_contains", "match": "---", "insert": "# before" })];
        fs_patch(&root, "Assets/_VS/x.prefab", &before, None).unwrap();
        assert_eq!(fs::read_to_string(root.join("Assets/_VS/x.prefab")).unwrap(), "%YAML 1.1\n# before\n---\n");
    }

    #[test]
    fn fs_patch_trim_trailing_whitespace() {
        let root = mkroot();
        write(&root, "Assets/_VS/x.prefab", "a   \nb\t\n");
        let patches = vec![json!({ "op": "trim_trailing_whitespace" })];
        fs_patch(&root, "Assets/_VS/x.prefab", &patches, None).unwrap();
        assert_eq!(fs::read_to_string(root.join("Assets/_VS/x.prefab")).unwrap(), "a\nb\n");
    }

    #[test]
    fn fs_patch_restore_overwrites_from_snapshot() {
        let root = mkroot();
        write(&root, "Assets/_VS/x.prefab", "patched\n");
        fs_patch(&root, "Assets/_VS/x.prefab", &[], Some("original\n")).unwrap();
        assert_eq!(fs::read_to_string(root.join("Assets/_VS/x.prefab")).unwrap(), "original\n");
    }

    #[test]
    fn fs_patch_missing_match_is_a_clean_error() {
        let root = mkroot();
        write(&root, "Assets/_VS/x.prefab", "a\n");
        let patches = vec![json!({ "op": "replace_line_contains", "match": "zzz", "replace": "b" })];
        let err = fs_patch(&root, "Assets/_VS/x.prefab", &patches, None).unwrap_err();
        assert!(err.contains("no line matched"));
        // File untouched on failure.
        assert_eq!(fs::read_to_string(root.join("Assets/_VS/x.prefab")).unwrap(), "a\n");
    }

    #[test]
    fn fs_delete_removes_files_and_dirs() {
        let root = mkroot();
        write(&root, "Assets/_VS/a.prefab", "a");
        write(&root, "Assets/_VS/sub/b.prefab", "b");
        let res = fs_delete(&root, &["Assets/_VS/a.prefab".into(), "Assets/_VS/sub".into()]).unwrap();
        assert!(res.ok);
        assert!(!root.join("Assets/_VS/a.prefab").exists());
        assert!(!root.join("Assets/_VS/sub").exists());
    }

    #[test]
    fn fs_delete_tolerates_missing_paths() {
        let root = mkroot();
        let res = fs_delete(&root, &["Assets/_VS/missing.prefab".into()]).unwrap();
        assert!(res.ok);
    }

    #[test]
    fn apply_patches_matches_ts_semantics_for_multiline_insert() {
        let text = "head\n".to_string();
        let out = apply_patches(
            &text,
            &[PatchOp::InsertAfterLineContains {
                r#match: "head".into(),
                insert: "x\ny".into(),
            }],
        )
        .unwrap();
        assert_eq!(out, "head\nx\ny\n");
    }

    /// End-to-end phase-2 flow: copy a prefab + `.meta`, patch the copy,
    /// record a manifest, then reset restores the patched file and deletes
    /// the created artifacts. Mirrors the sample-happy-path scenario.
    #[test]
    fn copy_patch_reset_roundtrip_restores_and_cleans_up() {
        use crate::manifest_store;
        use crate::schemas::StepManifest;

        let root = mkroot();
        let companions = vec![rule(".prefab", ".prefab.meta")];
        let state_root = "UserSettings/ValidationSuite/";

        // 1. fs_copy fixture prefab + companion into the fixture root.
        write(&root, "Assets/Prefabs/Player.prefab", "  m_Name: Player\n  m_Script: 1\n");
        write(&root, "Assets/Prefabs/Player.prefab.meta", "guid: abc\n");
        let copy = fs_copy(
            &root,
            "Assets/Prefabs/Player.prefab",
            "Assets/_ValidationSuite/sample/Player.prefab",
            &companions,
        )
        .unwrap();
        assert!(copy.ok);
        // 2. fs_patch the copy.
        let patches = vec![json!({ "op": "replace_line_contains", "match": "m_Name:", "replace": "  m_Name: PlayerPatched" })];
        let patch = fs_patch(&root, "Assets/_ValidationSuite/sample/Player.prefab", &patches, None).unwrap();
        assert_eq!(
            fs::read_to_string(root.join("Assets/_ValidationSuite/sample/Player.prefab")).unwrap(),
            "  m_Name: PlayerPatched\n  m_Script: 1\n",
        );
        // 3. Record a combined manifest (created copy + modified patch).
        let mut entries = copy.entries.clone();
        entries.extend(patch.entries.iter().cloned());
        let id = manifest_store::make_id("sample", "setup", 1);
        manifest_store::save(
            &root,
            state_root,
            &id,
            &StepManifest {
                scenario_id: "sample".into(),
                step_id: "setup".into(),
                entries: entries.clone(),
            },
        )
        .unwrap();
        let loaded = manifest_store::load(&root, state_root, &id).unwrap().unwrap();
        assert_eq!(loaded.entries.len(), entries.len());

        // 4. Reset: reverse-order revert. Modified → restore snapshot; created → delete.
        let mut snapshot_restored = false;
        for entry in loaded.entries.iter().rev() {
            match entry.kind {
                ManifestEntryKind::Modified => {
                    let snap = entry.snapshot.clone().unwrap_or_default();
                    restore_snapshot(&root, &entry.path, &snap).unwrap();
                    // Right after restore, the patched file holds its
                    // original content (before the created-delete runs).
                    assert_eq!(
                        fs::read_to_string(root.join(&entry.path)).unwrap(),
                        "  m_Name: Player\n  m_Script: 1\n",
                    );
                    snapshot_restored = true;
                }
                ManifestEntryKind::Created => {
                    let paths = entry
                        .companion_path
                        .iter()
                        .cloned()
                        .chain(std::iter::once(entry.path.clone()))
                        .collect::<Vec<_>>();
                    fs_delete(&root, &paths).unwrap();
                }
                ManifestEntryKind::Deleted => {}
            }
        }
        manifest_store::delete(&root, state_root, &id).unwrap();

        // Snapshot restore ran; the created copy + companion are gone.
        assert!(snapshot_restored, "modified entry was reverted from its snapshot");
        assert!(!root.join("Assets/_ValidationSuite/sample/Player.prefab").exists());
        assert!(!root.join("Assets/_ValidationSuite/sample/Player.prefab.meta").exists());
        assert!(manifest_store::load(&root, state_root, &id).unwrap().is_none());
    }
}
