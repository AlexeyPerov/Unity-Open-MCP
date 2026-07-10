//! Single source of truth for cross-cutting constants that used to be inlined
//! across the hub Rust tree (and that must agree with the bridge C# and
//! mcp-server TypeScript trees). Values that already have a natural domain
//! home stay there — the port formula lives in `bridge_port.rs`, the release
//! archive URL in `releases.rs`, the ping timeouts in `launch_verify.rs`.
//! This module owns the values that had no home and were copy-pasted as bare
//! string literals.
//!
//! Cross-tree parity (port formula, verify markers, bridge timeout, npm name)
//! is guarded by the mcp-server parity test (`constants.parity.test.ts`),
//! which asserts the values are identical to the on-disk definitions in the
//! bridge and hub trees.

/// Bridge port override env var. An explicit value (1–65535) wins over the
/// deterministic per-project hash. Set into every MCP client config the
/// wizard generates and into the Unity launch args.
pub const PORT_ENV_VAR: &str = "UNITY_OPEN_MCP_BRIDGE_PORT";

/// Unity project root env var. Required by the MCP server to locate the
/// project; set into every generated client config.
pub const PROJECT_PATH_ENV_VAR: &str = "UNITY_PROJECT_PATH";

/// Unity Editor executable env var. Used by the mcp-server's batch-only tools
/// when no live bridge is available.
pub const UNITY_PATH_ENV_VAR: &str = "UNITY_PATH";

/// Loopback address the bridge HTTP listener binds by default. Mirrors the C#
/// `BridgeBindAddress.Loopback` and the TS `LOOPBACK_HOST`.
pub const LOOPBACK_HOST: &str = "127.0.0.1";

/// The npm package the MCP server is published as, pinned to `@latest` for
/// the `npx -y` invocation. The `@latest` suffix is behavior-affecting
/// (always resolves the newest published version). Mirrors the C#
/// `BridgeConstants.NpmPackageLatest` and the TS `NPM_PACKAGE_LATEST`.
pub const NPM_PACKAGE_LATEST: &str = "unity-open-mcp@latest";

/// Markers wrapping the JSON payload emitted by the headless batch / verify
/// entry points so the mcp-server can extract it from mixed stdout. MUST
/// match the C# `BridgeConstants.VerifyJsonBegin/End` and the TS
/// `VERIFY_JSON_BEGIN/END` byte-for-byte (parity-tested).
pub const VERIFY_JSON_BEGIN: &str = "---UNITY_OPEN_MCP_VERIFY_JSON_BEGIN---";
pub const VERIFY_JSON_END: &str = "---UNITY_OPEN_MCP_VERIFY_JSON_END---";

/// The bridge's default per-tool wait before IT gives up and returns a
/// timeout envelope (`packages/bridge/Editor/Bridge/BridgeRequestBody.cs`
/// `DefaultTimeoutMs`). Mirrored here for parity with the C# / TS sides.
/// Parity-tested.
pub const BRIDGE_DEFAULT_TIMEOUT_MS: u64 = 30_000;

/// Canonical repository URL (human-facing). The actual GitHub repo is
/// `Unity-Open-MCP`; the `unity-open-mcp.git` spelling used for UPM/git
/// operations resolves to the same repo via GitHub redirect. Mirrors the C#
/// `BridgeConstants.RepoUrl`.
pub const REPO_URL: &str = "https://github.com/AlexeyPerov/Unity-Open-MCP";

/// Unity release-notes URL prefix. Append a version string to build a
/// per-release notes URL (e.g. `…/whats-new/6000.3.18f1`). The Rust
/// `releases.rs` and the TS `UnityVersionsTab.svelte` both build release-notes
/// links from this single prefix so the two never drift.
pub const RELEASE_NOTES_URL_PREFIX: &str = "https://unity.com/releases/editor/whats-new/";
