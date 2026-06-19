//! Keep-a-Changelog version-section prepending. Ports the Go
//! `prependChangelogVersion` helper: when a package's version bumps,
//! a new `## [<version>] - <label>` section is inserted at the top of
//! `CHANGELOG.md` (below the title) with the standard
//! Added / Fixed / Changed / Removed stubs.

use std::fs;
use std::path::Path;

/// Prepends a new version section to `changelog_path`. If the file
/// does not exist, it is created with a minimal header + the new
/// section. The new section uses the Keep-a-Changelog layout:
///
/// ```markdown
/// ## [1.2.0] - 2026-06-19
///
/// ### Added
///
/// ### Fixed
///
/// ### Changed
///
/// ### Removed
/// ```
///
/// The section is inserted after the first `# ` title line (and any
/// blank line immediately following it) so the document title stays
/// on top.
pub fn prepend_version(
    changelog_path: &Path,
    version: &str,
    label: &str,
) -> Result<(), String> {
    let section = format!(
        "\n## [{version}] - {label}\n\n### Added\n\n### Fixed\n\n### Changed\n\n### Removed\n",
        version = version,
        label = label,
    );

    let existing = fs::read_to_string(changelog_path).ok();
    let new_content = match existing {
        None => {
            // No changelog yet — seed with a title + the new section.
            format!("# Changelog\n{section}")
        }
        Some(content) => {
            // Insert after the first `# ` title line. If there is no
            // title, prepend the section at the very top.
            let mut lines = content.lines();
            let title = lines.next();
            match title {
                Some(t) if t.starts_with("# ") => {
                    let rest = &content[t.len()..];
                    // Skip exactly one leading newline if present so we
                    // do not accumulate blank lines between sections.
                    let rest = rest.strip_prefix('\n').unwrap_or(rest);
                    format!("{}\n{}\n{}", t, section.trim_end_matches('\n'), rest)
                }
                _ => {
                    // No recognizable title — prepend the section.
                    format!("{}\n{}", section.trim_end_matches('\n'), content)
                }
            }
        }
    };

    if let Some(parent) = changelog_path.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    fs::write(changelog_path, new_content).map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn creates_changelog_when_missing() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("CHANGELOG.md");
        prepend_version(&path, "1.2.0", "2026-06-19").unwrap();
        let content = fs::read_to_string(&path).unwrap();
        assert!(content.starts_with("# Changelog"));
        assert!(content.contains("## [1.2.0] - 2026-06-19"));
        assert!(content.contains("### Added"));
        assert!(content.contains("### Removed"));
    }

    #[test]
    fn inserts_section_after_title() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("CHANGELOG.md");
        fs::write(&path, "# Changelog\n\n## [1.1.0] - old\n\nstuff\n").unwrap();
        prepend_version(&path, "1.2.0", "2026-06-19").unwrap();
        let content = fs::read_to_string(&path).unwrap();
        // Title stays first.
        assert!(content.starts_with("# Changelog\n"));
        // New section appears before the old one.
        let new_pos = content.find("## [1.2.0]").unwrap();
        let old_pos = content.find("## [1.1.0]").unwrap();
        assert!(new_pos < old_pos);
        // Old content is preserved.
        assert!(content.contains("stuff"));
    }
}
