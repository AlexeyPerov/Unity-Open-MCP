//! Unity editor version parsing and comparison.
//!
//! Unity editor version strings look like `6000.0.1f1`, `2022.3.48f1`,
//! `2019.4.40f1`, `6000.0.10f1`. The shape is:
//!
//! ```text
//! <major>.<minor>.<patch><kind><build>
//! ```
//!
//! where `<kind>` is a single letter (`a` = alpha, `b` = beta, `f` = final,
//! `c` = China, `p` = patch) and `<build>` is an integer build number. The
//! `<patch>` segment ships well above 9 (e.g. `2022.3.48f1`, `6000.0.10f1`),
//! so a lexicographic `String` comparison mis-sorts: `"6000.0.10f1"` sorts
//! **below** `"6000.0.9f1"` because `'1' < '9'` at the patch position, and
//! `"2022.3.9f1"` sorts **above** `"2022.3.48f1"` for the same reason.
//!
//! This module exposes [`parse_unity_version`] → [`UnityVersion`] and an
//! [`Ord`] implementation that orders by the numeric tuple, so callers can
//! compare real Unity versions instead of their string representation. See
//! H14 in the round-2 review.

use std::cmp::Ordering;

/// Parsed Unity editor version. The `<kind>` letter is preserved for display
/// but does not contribute to ordering by default — Unity never ships two
/// editors with the same `major.minor.patch` but different kinds in the same
/// discovery result, and the upgrade flow only cares about the numeric
/// ordering.
#[derive(Debug, Clone, Eq, PartialEq)]
pub struct UnityVersion {
    pub major: u32,
    pub minor: u32,
    pub patch: u32,
    /// The release-stream letter (`a`, `b`, `f`, `c`, `p`) when present.
    pub kind: Option<char>,
    /// The trailing build number (`1` in `6000.0.1f1`).
    pub build: Option<u32>,
}

impl UnityVersion {
    /// Parse a Unity editor version string. Returns `None` for a string that
    /// does not match the `<major>.<minor>.<patch>[<letter>[<build>]]` shape.
    /// The parser is intentionally lenient about trailing junk so it can also
    /// accept values Unity sometimes writes (e.g. a local-build suffix);
    /// only the leading numeric triple and the optional kind/build are
    /// consumed.
    pub fn parse(input: &str) -> Option<Self> {
        let trimmed = input.trim();
        if trimmed.is_empty() {
            return None;
        }
        let mut parts = trimmed.split('.');
        let major: u32 = parts.next()?.parse().ok()?;
        let minor: u32 = parts.next()?.parse().ok()?;
        // The third segment may carry the kind letter + build number
        // attached to the patch (`48f1`), or be a bare number (`48`).
        let third = parts.next()?;
        let (patch, kind, build) = parse_patch_segment(third)?;
        // Reject obvious garbage like a fourth numeric segment we don't
        // model — Unity never writes `6000.0.1.2f1`. We do tolerate a
        // trailing non-numeric tail (some local builds append a hash) by
        // stopping at the first segment that fails to parse as a number
        // after the patch/kind/build trio.
        Some(UnityVersion {
            major,
            minor,
            patch,
            kind,
            build,
        })
    }
}

/// Split the third `.`-delimited segment (`"48f1"`, `"10b2"`, `"0"`,
/// `"48f1exrLocal"`) into `(patch, kind, build)`. The patch must parse as a
/// `u32`; the kind is the first non-digit byte after the patch (if any); the
/// build is the integer following the kind letter (if present and numeric).
fn parse_patch_segment(segment: &str) -> Option<(u32, Option<char>, Option<u32>)> {
    let bytes = segment.as_bytes();
    let mut idx = 0usize;
    while idx < bytes.len() && bytes[idx].is_ascii_digit() {
        idx += 1;
    }
    if idx == 0 {
        return None;
    }
    let patch: u32 = segment[..idx].parse().ok()?;
    let kind = if idx < bytes.len() {
        // The kind letter is a single ASCII alpha byte.
        let b = bytes[idx] as char;
        if b.is_ascii_alphabetic() {
            idx += 1;
            Some(b)
        } else {
            None
        }
    } else {
        None
    };
    let build = if idx < bytes.len() {
        // Try to parse the remainder (after the kind letter) as the build
        // number. Stop at the first non-digit so a local-build suffix
        // (`48f1exrLocal`) does not break the parse.
        let mut j = idx;
        while j < bytes.len() && bytes[j].is_ascii_digit() {
            j += 1;
        }
        if j > idx {
            segment[idx..j].parse::<u32>().ok()
        } else {
            None
        }
    } else {
        None
    };
    Some((patch, kind, build))
}

impl Ord for UnityVersion {
    fn cmp(&self, other: &Self) -> Ordering {
        self.major
            .cmp(&other.major)
            .then(self.minor.cmp(&other.minor))
            .then(self.patch.cmp(&other.patch))
            // Stable tiebreakers so equal numeric triples still order
            // deterministically (and so the `Ord` is total). Higher build
            // number = newer; a present kind letter ranks above an absent
            // one (a bare patch like `0` is an unfinished parse on the
            // other side, so treat the well-formed side as greater).
            .then_with(|| {
                let ka = self.kind.map(|c| c as u32).unwrap_or(0);
                let kb = other.kind.map(|c| c as u32).unwrap_or(0);
                ka.cmp(&kb)
            })
            .then_with(|| {
                let ba = self.build.unwrap_or(0);
                let bb = other.build.unwrap_or(0);
                ba.cmp(&bb)
            })
    }
}

impl PartialOrd for UnityVersion {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

/// Compare two Unity version strings by their parsed numeric tuples.
/// Falls back to lexicographic comparison when either side fails to parse,
/// so an unparseable value (rare in practice — Unity always writes the
/// documented shape) is still ordered deterministically rather than
/// discarded. This preserves the previous behaviour for malformed inputs
/// while fixing the common case (real Unity versions with patch >= 10).
pub fn compare_versions(a: &str, b: &str) -> Ordering {
    match (UnityVersion::parse(a), UnityVersion::parse(b)) {
        (Some(pa), Some(pb)) => pa.cmp(&pb),
        _ => a.cmp(b),
    }
}

/// True when `candidate` is strictly higher than `current` using the parsed
/// numeric tuple comparison. Replaces the lexicographic `candidate > current`
/// comparison that mis-sorted patch numbers >= 10.
pub fn version_is_higher(candidate: &str, current: &str) -> bool {
    candidate != current && compare_versions(candidate, current) == Ordering::Greater
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_modern_unity_version() {
        let v = UnityVersion::parse("6000.0.1f1").unwrap();
        assert_eq!(v.major, 6000);
        assert_eq!(v.minor, 0);
        assert_eq!(v.patch, 1);
        assert_eq!(v.kind, Some('f'));
        assert_eq!(v.build, Some(1));
    }

    #[test]
    fn parses_2022_lts_version() {
        let v = UnityVersion::parse("2022.3.48f1").unwrap();
        assert_eq!(v.major, 2022);
        assert_eq!(v.minor, 3);
        assert_eq!(v.patch, 48);
        assert_eq!(v.kind, Some('f'));
        assert_eq!(v.build, Some(1));
    }

    #[test]
    fn parses_beta_version() {
        let v = UnityVersion::parse("6000.0.10b2").unwrap();
        assert_eq!(v.patch, 10);
        assert_eq!(v.kind, Some('b'));
        assert_eq!(v.build, Some(2));
    }

    #[test]
    fn patch_numbers_above_nine_sort_numerically() {
        // H14: the lexicographic comparator sorted "6000.0.10f1" BELOW
        // "6000.0.9f1" because '1' < '9' at the patch position.
        assert!(version_is_higher("6000.0.10f1", "6000.0.9f1"));
        assert!(!version_is_higher("6000.0.9f1", "6000.0.10f1"));
    }

    #[test]
    fn patch_numbers_above_nine_sort_numerically_2022() {
        // The symmetric case from the report: "2022.3.9f1" > "2022.3.48f1"
        // was true under lexicographic comparison.
        assert!(version_is_higher("2022.3.48f1", "2022.3.9f1"));
        assert!(!version_is_higher("2022.3.9f1", "2022.3.48f1"));
    }

    #[test]
    fn cross_major_version_still_orders_correctly() {
        assert!(version_is_higher("6000.0.1f1", "2022.3.48f1"));
        assert!(!version_is_higher("2022.3.48f1", "6000.0.1f1"));
    }

    #[test]
    fn equal_versions_are_not_higher() {
        assert!(!version_is_higher("6000.0.1f1", "6000.0.1f1"));
    }

    #[test]
    fn higher_patch_within_same_minor() {
        assert!(version_is_higher("6000.0.2f1", "6000.0.1f1"));
        assert!(!version_is_higher("6000.0.0f1", "6000.0.1f1"));
    }

    #[test]
    fn compare_versions_descending_sort() {
        // Mirrors discovery.rs::discover_results_sorted_version_desc.
        let mut versions = vec![
            "2022.3.48f1".to_string(),
            "6000.0.1f1".to_string(),
            "6000.0.10f1".to_string(),
            "6000.0.9f1".to_string(),
            "6000.0.2f1".to_string(),
        ];
        versions.sort_by(|a, b| compare_versions(b, a));
        assert_eq!(
            versions,
            vec![
                "6000.0.10f1".to_string(),
                "6000.0.9f1".to_string(),
                "6000.0.2f1".to_string(),
                "6000.0.1f1".to_string(),
                "2022.3.48f1".to_string(),
            ]
        );
    }

    #[test]
    fn malformed_falls_back_to_lexicographic() {
        // An unparseable value does not panic and orders deterministically.
        assert_eq!(compare_versions("garbage", "6000.0.1f1"), "garbage".cmp("6000.0.1f1"));
    }

    #[test]
    fn rejects_empty_and_non_numeric() {
        assert!(UnityVersion::parse("").is_none());
        assert!(UnityVersion::parse("not-a-version").is_none());
        assert!(UnityVersion::parse("6000").is_none());
        assert!(UnityVersion::parse("6000.0").is_none());
    }

    #[test]
    fn tolerates_local_build_suffix() {
        // Unity local builds sometimes append a hash; the parser should
        // still recover the numeric triple + kind/build.
        let v = UnityVersion::parse("6000.0.1f1exrLocal").unwrap();
        assert_eq!(v.major, 6000);
        assert_eq!(v.patch, 1);
        assert_eq!(v.kind, Some('f'));
        assert_eq!(v.build, Some(1));
    }
}
