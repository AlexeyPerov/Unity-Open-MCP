using System;
using System.Linq;

namespace UnityOpenMcpBridge.Config
{
    /// <summary>
    /// Renders the committed launch wrapper for clients whose config can name
    /// neither a workspace variable nor a spawn-directory flag (Codex, ZCode).
    /// The script resolves the Unity project from its own location, exports
    /// <c>UNITY_PROJECT_PATH</c>, and execs the pinned package, so the config
    /// that runs it carries no machine path.
    ///
    /// <see cref="Template"/> is a byte copy of
    /// <c>mcp-server/templates/mcp-wrapper.sh</c> (the <c>setup --wrapper</c>
    /// source); a dev-checkout test pins the two together, and the
    /// placeholder substitution mirrors <c>renderWrapperScript</c> in
    /// <c>mcp-server/src/setup/portable-config.ts</c>.
    /// </summary>
    internal static class McpWrapperScript
    {
        /// <summary>Directories that must exist directly under a Unity project root.</summary>
        internal const string UnityRootMarkers = "Assets Packages ProjectSettings";

        internal const string Template = @"#!/usr/bin/env bash
# unity-open-mcp MCP wrapper.
#
# Resolves UNITY_PROJECT_PATH from THIS SCRIPT'S location, so it works no
# matter what working directory the AI client spawns the MCP server in. Use it
# for clients that expand neither ${workspaceFolder} nor environment variables
# in their MCP config (for example Codex and ZCode).
#
# Committed to the repository; contains no machine-specific paths.
# Regenerate with: unity-open-mcp setup --wrapper …
#
# Override the Unity subfolder for a one-off run:
#   UNITY_SUBPATH=OtherClient bash <this script>
set -euo pipefail

script_dir=""$(cd ""$(dirname ""${BASH_SOURCE[0]}"")"" && pwd)""
workspace_root=""$(cd ""${script_dir}/__WORKSPACE_FROM_SCRIPT__"" && pwd)""
subpath=""${UNITY_SUBPATH-__UNITY_SUBPATH__}""

if [ -n ""${subpath}"" ]; then
  project_path=""${workspace_root}/${subpath}""
else
  project_path=""${workspace_root}""
fi

for marker in __UNITY_ROOT_MARKERS__; do
  if [ ! -d ""${project_path}/${marker}"" ]; then
    echo ""unity-open-mcp: ${project_path} is not a Unity project root (missing ${marker}/)."" >&2
    echo ""unity-open-mcp: set UNITY_SUBPATH to the Unity folder relative to ${workspace_root}."" >&2
    exit 1
  fi
done

export UNITY_PROJECT_PATH=""${project_path}""
exec npx -y ""unity-open-mcp@__UNITY_OPEN_MCP_VERSION__"" ""$@""
";

        /// <summary>
        /// Script body for a wrapper at <paramref name="wrapperRelativePath"/>
        /// (relative to the workspace root) launching
        /// <c>unity-open-mcp@<paramref name="version"/></c> on the Unity project
        /// at <paramref name="unitySubpath"/> ("" when the workspace is the
        /// Unity project).
        /// </summary>
        internal static string Render(string version, string wrapperRelativePath, string unitySubpath)
        {
            var depth = wrapperRelativePath.Split('/').Length - 1;
            var upward = depth > 0 ? string.Join("/", Enumerable.Repeat("..", depth)) : ".";
            // The template is a verbatim literal, so it carries whatever line
            // endings this source file was checked out with. A CRLF checkout
            // (Windows, core.autocrlf) must not leak into a bash script: bash
            // rejects `set -euo pipefail\r`, and once committed the script
            // would break on every machine.
            return Template
                .Replace("\r\n", "\n")
                .Replace("__WORKSPACE_FROM_SCRIPT__", upward)
                .Replace("__UNITY_SUBPATH__", (unitySubpath ?? "").Replace('\\', '/'))
                .Replace("__UNITY_ROOT_MARKERS__", UnityRootMarkers)
                .Replace("__UNITY_OPEN_MCP_VERSION__", version);
        }

        /// <summary>Version part of <see cref="BridgeConstants.NpmPackage"/>
        /// (<c>unity-open-mcp@1.3.0</c> → <c>1.3.0</c>).</summary>
        internal static string PinnedVersion()
        {
            var pin = BridgeConstants.NpmPackage;
            var at = pin.LastIndexOf('@');
            return at > 0 ? pin.Substring(at + 1) : pin;
        }
    }
}
