import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M26 Plan 2 — Unity Hub control: available releases. Local-routed (fetches
// Unity's public download-archive page and parses its Next.js RSC payload;
// never hits the Unity bridge or spawns Unity). Falls back to a bundled
// snapshot (stale=true) on network failure, so the call never hard-errors.
// Read-only, gate-free.
export const hubAvailableReleases: Tool = {
  name: "unity_open_mcp_hub_available_releases",
  description:
    "List Unity Editor versions available for download, with their release " +
    "stream (LTS / Supported / TECH / Beta / Alpha), release date, release-" +
    "notes URL, and install changeset (when exposed). Sourced from Unity's " +
    "public download-archive page (unity.com/releases/editor/archive). The " +
    "result carries a `stale` flag — true when the live fetch failed and the " +
    "data is a bundled offline fallback (re-call to retry), false when it is " +
    "fresh. The changeset is required to install some archived versions via " +
    "the unityhub:// deep link; pass it to hub_install_editor when present. " +
    "Read-only, gate-free, no Unity Editor or bridge connection required.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
