//! Per-project bridge port resolution — mirror of the bridge (C#) and MCP
//! server (TypeScript) port formulas so all three sides agree without any
//! shared config.
//!
//! Two Unity projects running bridges simultaneously cannot share a fixed
//! port. The port is derived deterministically from the project path:
//! `20000 + (sha256(path) % 10000)`, so the bridge listener, the MCP server
//! client, and the Hub wizard that writes the client config all converge on
//! the same number. An explicit override (the wizard's optional port field)
//! always wins — it's the escape hatch for users who pin a port and for CI
//! flows that allocate ports externally.
//!
//! # The formula (byte-identical across three languages)
//!
//! 1. Normalize the path: backslashes → forward slashes, trim a trailing
//!    slash, **no lowercasing** (macOS/Linux paths are case-sensitive).
//! 2. Lowercase hex SHA-256 of the normalized path.
//! 3. Port = `PORT_RANGE_START + (first-16-hex-chars as big-endian u64) %
//!    PORT_RANGE_SIZE`.
//!
//! The first 8 bytes (16 hex chars) are used so the modulo stays inside a
//! 64-bit unsigned integer and agrees exactly across Rust (`u64`), C#
//! (`UInt64`), and TypeScript (`BigInt`). A full 256-bit modulo would
//! diverge across language big-ints.
//!
//! Canonical implementations this MUST stay in sync with:
//! - Bridge: `packages/bridge/Editor/Bridge/InstancePortResolver.cs`
//! - MCP server: `mcp-server/src/instance-discovery.ts`
//! - Hub (this file): `hub/src-tauri/src/config/bridge_port.rs`
//!
//! Cross-side consistency is pinned by unit tests on all three sides
//! (`InstancePortResolverTests.cs` / `instance-discovery.test.ts` / the
//! `tests` module below) — they share fixtures including the demo path.

use sha2::{Digest, Sha256};

/// Start of the per-project port range (inclusive). Matches the bridge and
/// the MCP server.
pub const PORT_RANGE_START: u32 = 20_000;
/// Size of the per-project port range. Valid ports are
/// `[PORT_RANGE_START, PORT_RANGE_START + PORT_RANGE_SIZE)`.
pub const PORT_RANGE_SIZE: u32 = 10_000;

/// Path normalization applied BEFORE hashing. Mirrors
/// `InstancePortResolver.NormalizePath` (C#) and `normalizePath` (TS):
/// backslashes → forward slashes, trailing slash trimmed, no lowercasing.
pub fn normalize_path(project_path: &str) -> String {
    if project_path.is_empty() {
        return String::new();
    }
    let mut norm = project_path.replace('\\', "/");
    while norm.len() > 1 && norm.ends_with('/') {
        norm.pop();
    }
    norm
}

/// Lowercase hex SHA-256 of the normalized project path. Used as the
/// instance-lock file name and as the `projectHash` field written into the
/// lock JSON so the MCP server can verify it matched the expected project.
pub fn project_hash(project_path: &str) -> String {
    let normalized = normalize_path(project_path);
    let digest = Sha256::digest(normalized.as_bytes());
    // 32 bytes → 64 lowercase hex chars.
    let mut out = String::with_capacity(digest.len() * 2);
    for byte in digest.iter() {
        out.push_str(&format!("{:02x}", byte));
    }
    out
}

/// Deterministic port for a project path:
/// `PORT_RANGE_START + (sha256(path) % PORT_RANGE_SIZE)`, using the first
/// 8 bytes (16 hex chars) of the hash as a big-endian `u64` so the modulo
/// matches the C# `UInt64` and TS `BigInt` computations exactly.
pub fn compute_port(project_path: &str) -> u16 {
    let hash = project_hash(project_path);
    // First 16 hex chars = first 8 bytes as big-endian u64.
    let prefix = &hash[..16];
    let value = u64::from_str_radix(prefix, 16)
        .expect("first 16 hex chars of a sha256 digest always parse as u64");
    let port = PORT_RANGE_START + (value % PORT_RANGE_SIZE as u64) as u32;
    // Range is [20000, 29999] — always fits in a u16 (max 65535).
    port.try_into().expect("computed port fits in u16")
}

/// `true` for a valid TCP port (1..=65535). Matches
/// `InstancePortResolver.IsValidPort`.
pub fn is_valid_port(port: u16) -> bool {
    port >= 1
}

/// Resolve the bridge port with override precedence:
///
/// 1. `override_port` when present and valid (1..=65535) — the escape hatch
///    for users who pin a port or CI flows that allocate one externally.
/// 2. deterministic hash of the project path (default).
///
/// Pass `None` for `override_port` when the caller found no override (blank
/// wizard field, missing env var, unparseable input).
pub fn resolve_port(project_path: &str, override_port: Option<u16>) -> u16 {
    match override_port.filter(|&p| is_valid_port(p)) {
        Some(p) => p,
        None => compute_port(project_path),
    }
}

/// Parse a free-text port string (the wizard's optional port field) into an
/// optional override port. Blank / whitespace / non-numeric / out-of-range
/// input → `None` (meaning "derive from the project path"). A non-empty
/// valid port → `Some(port)`.
pub fn parse_override(raw: &str) -> Option<u16> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }
    let n: u32 = trimmed.parse().ok()?;
    if n >= 1 && n <= u16::MAX as u32 {
        Some(n as u16)
    } else {
        None
    }
}

/// Tauri command: resolve the bridge port for a project, honoring an
/// optional explicit override. The wizard Step 4 calls this to display the
/// effective port (so a blank field shows the derived value) and Step 5
/// uses the result to launch Unity + poll `/ping`. Keeps the hash formula
/// in one place (Rust) and gives the UI a single source of truth — no TS
/// copy of the formula.
///
/// Runs on the blocking pool for consistency with the rest of the wizard
/// command set (sync handlers occupy the WebView main thread). The SHA-256
/// computation is fast and CPU-bound, so this never hangs in practice —
/// the async wrapper is about keeping the whole wizard surface off the
/// main thread, not about a known slow path here.
#[tauri::command]
pub async fn resolve_bridge_port(
    project_path: String,
    override_port: Option<u16>,
) -> u16 {
    tauri::async_runtime::spawn_blocking(move || resolve_port(&project_path, override_port))
        .await
        .unwrap_or_else(|_| {
            // JoinError (pool panic) — effectively unreachable. Fall back
            // to the range start so the wizard's port field renders a sane
            // placeholder; the real value is recomputed on any launch.
            PORT_RANGE_START as u16
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    // Cross-side fixtures. These MUST match the bridge
    // (InstancePortResolverTests.cs) and MCP server
    // (instance-discovery.test.ts) so all three languages agree.

    #[test]
    fn demo_project_path_resolves_to_27916() {
        // Pinned by the live instance lock da30c04e….json and lsof on the
        // demo machine. sha256("/Users/.../demo")[:16] = da30c04ead4dc1fc,
        // 0xda30c04ead4dc1fc % 10000 = 7916, + 20000 = 27916.
        let path = "/Users/alexeyperov/Projects/Unity-AI-Hub/demo";
        assert_eq!(project_hash(path), "da30c04ead4dc1fcd4892433d4c0b7497708dab764339c4fabc2470a356d6980");
        assert_eq!(compute_port(path), 27916);
    }

    #[test]
    fn compute_port_stays_in_range() {
        let cases = [
            "/Users/x/proj",
            "C:\\Users\\x\\proj",
            "/home/dev/game",
            "/tmp/p",
        ];
        for path in cases {
            let port = compute_port(path) as u32;
            assert!(
                (PORT_RANGE_START..PORT_RANGE_START + PORT_RANGE_SIZE).contains(&port),
                "port {port} for {path:?} out of range"
            );
        }
    }

    #[test]
    fn normalize_matches_bridge_semantics() {
        // No lowercasing, backslash → slash, trailing slash trimmed.
        assert_eq!(normalize_path("/Foo/Bar/"), "/Foo/Bar");
        assert_eq!(normalize_path("C:\\Users\\x\\proj"), "C:/Users/x/proj");
        assert_eq!(normalize_path("/same"), "/same"); // case preserved
        assert_eq!(normalize_path(""), "");
        // A single slash is preserved (not trimmed to empty).
        assert_eq!(normalize_path("/"), "/");
    }

    #[test]
    fn trailing_slash_hashes_same_as_no_trailing() {
        // Normalization is applied before hashing so the same project
        // resolves to the same port regardless of trailing separator.
        assert_eq!(project_hash("/a/b/"), project_hash("/a/b"));
        assert_eq!(compute_port("/a/b/"), compute_port("/a/b"));
    }

    #[test]
    fn override_wins_over_hash() {
        let path = "/Users/alexeyperov/Projects/Unity-AI-Hub/demo";
        assert_eq!(resolve_port(path, None), 27916);
        assert_eq!(resolve_port(path, Some(19199)), 19199);
        // Port 0 is invalid → falls back to the hash.
        assert_eq!(resolve_port(path, Some(0)), 27916);
    }

    #[test]
    fn parse_override_handles_blank_and_garbage() {
        assert_eq!(parse_override(""), None);
        assert_eq!(parse_override("   "), None);
        assert_eq!(parse_override("abc"), None);
        assert_eq!(parse_override("0"), None); // 0 is not a valid port
        assert_eq!(parse_override("70000"), None); // out of u16 range
        assert_eq!(parse_override("27916"), Some(27916));
        assert_eq!(parse_override("  19199  "), Some(19199));
    }

    #[test]
    fn hashes_are_lowercase_hex_sha256_length() {
        let h = project_hash("/anything");
        assert_eq!(h.len(), 64);
        assert!(h.chars().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
    }
}
