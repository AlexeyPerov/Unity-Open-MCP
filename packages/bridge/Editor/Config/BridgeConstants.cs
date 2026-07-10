using System;

namespace UnityOpenMcpBridge.Config
{
    /// <summary>
    /// Single source of truth for cross-cutting string/int constants that used
    /// to be inlined across the bridge tree (and that must agree with the
    /// mcp-server TypeScript and hub Rust trees). Values that already have a
    /// natural domain home stay there — the port formula lives in
    /// <see cref="InstancePortResolver"/>, the timeout bounds in
    /// <see cref="BridgeRequestBody"/>, and the bind addresses in
    /// <see cref="BridgeBindAddress"/>. This module owns the values that had no
    /// home and were copy-pasted as bare literals.
    ///
    /// Cross-tree parity (port formula, verify markers, bridge timeout, npm
    /// name) is guarded by parity tests on each side — see
    /// BridgeConstantsTests.cs.
    /// </summary>
    public static class BridgeConstants
    {
        // --- scratch / settings directory ---------------------------------

        /// <summary>
        /// Per-project settings directory created at the Unity project root
        /// (<c>settings.json</c>, <c>audit/</c>) and the home scratch
        /// directory (<c>~/.unity-open-mcp</c>) shared with the MCP server
        /// (instance locks, test scratch, screenshots). Mirrors the TypeScript
        /// <c>STATUS_DIR_NAME</c> and the Rust consumers.
        /// </summary>
        public const string SettingsDirName = ".unity-open-mcp";

        // --- npm package --------------------------------------------------

        /// <summary>
        /// The npm package the MCP server is published as, pinned to
        /// <c>@latest</c> for the <c>npx -y</c> invocation the bridge surfaces
        /// in its Status / Configure panels. The <c>@latest</c> suffix is
        /// behavior-affecting (always resolves the newest published version),
        /// so it is part of the constant, not a caller choice.
        /// </summary>
        public const string NpmPackageLatest = "unity-open-mcp@latest";

        // --- environment variable names -----------------------------------

        /// <summary>
        /// Bridge port override env var. An explicit value (1–65535) wins over
        /// the deterministic per-project hash. Read by the bridge
        /// (<see cref="BridgeHttpServer"/>) and set into every MCP client
        /// config the bridge/wizard generate.
        /// </summary>
        public const string PortEnvVar = "UNITY_OPEN_MCP_BRIDGE_PORT";

        /// <summary>
        /// Bridge port override CLI arg prefix (Unity launch argument form).
        /// The bridge reads this as a fallback when the env var is absent.
        /// </summary>
        public const string PortArgPrefix = "-UNITY_OPEN_MCP_BRIDGE_PORT=";

        /// <summary>
        /// Unity project root env var. Required by the MCP server to locate
        /// the project; set into every generated client config.
        /// </summary>
        public const string ProjectPathEnvVar = "UNITY_PROJECT_PATH";

        // --- verify batch output markers ----------------------------------

        /// <summary>
        /// Markers wrapping the JSON payload emitted by the headless batch /
        /// verify entry points so the MCP server can extract it from mixed
        /// stdout. MUST match the TypeScript
        /// <c>OUTPUT_BEGIN</c>/<c>OUTPUT_END</c> and the Rust/verify entry
        /// byte-for-byte (parity-tested).
        /// </summary>
        public const string VerifyJsonBegin = "---UNITY_OPEN_MCP_VERIFY_JSON_BEGIN---";
        public const string VerifyJsonEnd = "---UNITY_OPEN_MCP_VERIFY_JSON_END---";

        // --- repo URL -----------------------------------------------------

        /// <summary>
        /// Canonical repository URL (human-facing). The actual GitHub repo is
        /// <c>Unity-Open-MCP</c>; the <c>unity-open-mcp.git</c> spelling used
        /// for UPM/git operations resolves to the same repo via GitHub
        /// redirect. All in-window links and docs should use this constant.
        /// </summary>
        public const string RepoUrl = "https://github.com/AlexeyPerov/Unity-Open-MCP";
    }
}
