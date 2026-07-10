// Single source of truth for cross-cutting constants that used to be inlined
// across the mcp-server tree (and that must agree with the bridge C# and hub
// Rust trees). Values that already have a natural domain home stay there —
// the port formula lives in instance-discovery.ts, the retry tunables in
// retry-policy.ts. This module owns the values that had no home and were
// copy-pasted as bare literals.
//
// Cross-tree parity (port formula, verify markers, bridge timeout, npm name)
// is guarded by the parity test in constants.parity.test.ts, which asserts
// the values are identical to the on-disk definitions in the bridge and hub
// trees.

/**
 * Scratch directory shared by the bridge and the MCP server
 * (`~/.unity-open-mcp`). Mirrors the C# `BridgeConstants.SettingsDirName`
 * and the Rust consumers. The home-dir join lives in instance-discovery.ts
 * `statusDir()`; this constant is the bare directory name.
 */
export const STATUS_DIR_NAME = ".unity-open-mcp";

/**
 * Bridge port override env var. An explicit value (1–65535) wins over the
 * deterministic per-project hash. Read here (resolveEnv) and set into every
 * MCP client config the bridge/wizard generate.
 */
export const PORT_ENV_VAR = "UNITY_OPEN_MCP_BRIDGE_PORT";

/**
 * Unity project root env var. Required by the MCP server to locate the
 * project; set into every generated client config.
 */
export const PROJECT_PATH_ENV_VAR = "UNITY_PROJECT_PATH";

/**
 * Loopback address the bridge HTTP listener binds by default. Used to build
 * the base URL the client connects to. Mirrors the C#
 * `BridgeBindAddress.Loopback`.
 */
export const LOOPBACK_HOST = "127.0.0.1";

/**
 * Markers wrapping the JSON payload emitted by the headless batch / verify
 * entry points so this server can extract it from mixed stdout. MUST match
 * the C# `BridgeConstants.VerifyJsonBegin/End` byte-for-byte
 * (parity-tested).
 */
export const VERIFY_JSON_BEGIN = "---UNITY_OPEN_MCP_VERIFY_JSON_BEGIN---";
export const VERIFY_JSON_END = "---UNITY_OPEN_MCP_VERIFY_JSON_END---";

/**
 * The bridge's default per-tool wait before IT gives up and returns a
 * timeout envelope (packages/bridge/Editor/Bridge/BridgeRequestBody.cs
 * `DefaultTimeoutMs`). The MCP client fetch timeout must never preempt this
 * — if the client aborts first it re-POSTs while the bridge is still
 * processing the original work, manufacturing duplicate side-effects.
 * Kept as a literal here (not imported from the C# side) because the bridge
 * assembly isn't readable from the TS server; the cross-reference is the
 * contract — bump both together. Parity-tested.
 */
export const BRIDGE_DEFAULT_TIMEOUT_MS = 30_000;

/**
 * Minimum / maximum tool timeout the bridge clamps an explicit timeout_ms to
 * (packages/bridge/Editor/Bridge/BridgeRequestBody.cs `MinTimeoutMs` /
 * `MaxTimeoutMs`). Mirrored here for tool-schema defaults so the advertised
 * bounds agree with the clamp.
 */
export const BRIDGE_MIN_TIMEOUT_MS = 1_000;
export const BRIDGE_MAX_TIMEOUT_MS = 600_000;

/**
 * The npm package the MCP server is published as, pinned to `@latest` for
 * the `npx -y` invocation. The `@latest` suffix is behavior-affecting
 * (always resolves the newest published version). Mirrors the C#
 * `BridgeConstants.NpmPackageLatest`.
 */
export const NPM_PACKAGE_LATEST = "unity-open-mcp@latest";

/**
 * Build the bridge base URL for a given port. Centralizes the loopback +
 * scheme so every caller produces the same string.
 */
export function bridgeBaseUrl(port: number): string {
  return `http://${LOOPBACK_HOST}:${port}`;
}

/**
 * Known Unity LTS version prefixes. A `6000.x` / `2022.3` / etc. release is
 * classified "LTS" rather than "TECH" when its version starts with one of
 * these. The min supported by the packages is 2022.3 LTS. Used by
 * hub-control.ts `releaseStreamFromVersion`.
 */
export const UNITY_LTS_PREFIXES: readonly string[] = [
  "6000.0",
  "2022.3",
  "2021.3",
  "2020.3",
  "2019.4",
];

/**
 * Unity public download-archive page. Server-renders the full Unity release
 * catalog. The single source for both the mcp-server (hub-control.ts) and
 * (mirrored in Rust) the Hub launcher. Append the version to
 * {@link RELEASE_NOTES_URL_PREFIX} to build a per-release notes URL.
 */
export const ARCHIVE_URL = "https://unity.com/releases/editor/archive";

/**
 * Unity release-notes URL prefix. Append a version string to build a
 * per-release notes URL. Sourced here so the mcp-server and the Hub TS
 * frontend (UnityVersionsTab.svelte) share one definition.
 */
export const RELEASE_NOTES_URL_PREFIX =
  "https://unity.com/releases/editor/whats-new/";

/**
 * Canonical repository URL (human-facing). The actual GitHub repo is
 * `Unity-Open-MCP`. Mirrors the C# `BridgeConstants.RepoUrl` and the Rust
 * `REPO_URL`.
 */
export const REPO_URL = "https://github.com/AlexeyPerov/Unity-Open-MCP";
