#!/usr/bin/env bash
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

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
workspace_root="$(cd "${script_dir}/__WORKSPACE_FROM_SCRIPT__" && pwd)"
subpath="${UNITY_SUBPATH-__UNITY_SUBPATH__}"

if [ -n "${subpath}" ]; then
  project_path="${workspace_root}/${subpath}"
else
  project_path="${workspace_root}"
fi

for marker in __UNITY_ROOT_MARKERS__; do
  if [ ! -d "${project_path}/${marker}" ]; then
    echo "unity-open-mcp: ${project_path} is not a Unity project root (missing ${marker}/)." >&2
    echo "unity-open-mcp: set UNITY_SUBPATH to the Unity folder relative to ${workspace_root}." >&2
    exit 1
  fi
done

export UNITY_PROJECT_PATH="${project_path}"
exec npx -y "unity-open-mcp@__UNITY_OPEN_MCP_VERSION__" "$@"
