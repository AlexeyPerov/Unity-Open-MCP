import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M26 Plan 2 — Unity Hub control: list installed editors. Local-routed (the
// MCP server scans the Hub install roots itself; never hits the Unity bridge
// or spawns Unity). Reuses the same discovery approach as the Hub launcher
// (scannedHubRoots + Data/PlaybackEngines scan) implemented standalone in TS.
// Read-only, gate-free, no project-asset mutation (paths_hint is N/A).
export const hubListEditors: Tool = {
  name: "unity_open_mcp_hub_list_editors",
  description:
    "List all Unity Editor versions installed via Unity Hub on this machine, " +
    "including each install's executable path, the build-target platforms it " +
    "has modules for (scanned from Data/PlaybackEngines), and the release " +
    "stream inferred from the version suffix (LTS / TECH / Beta / Alpha). " +
    "Resolved by scanning the OS-default Unity Hub install roots " +
    "(+ UNITY_HUB env override) — no running Unity Editor or bridge " +
    "connection required. Read-only, gate-free. Cross-platform: the scan " +
    "roots are /Applications/Unity/Hub/Editor (macOS), " +
    "C:\\Program Files\\Unity\\Hub\\Editor (Windows), ~/Unity/Hub/Editor " +
    "(Linux). Use this before hub_install_editor to check what is already " +
    "available, and after an install (refresh) to confirm the new editor.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
