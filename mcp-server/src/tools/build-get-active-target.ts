import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 9 — typed active build-target read. Read-only: returns the active
// BuildTarget + its BuildTargetGroup. Gate-free direct-response tool.
export const buildGetActiveTarget: Tool = {
  name: "unity_open_mcp_build_get_active_target",
  description:
    "Read-only: the currently active BuildTarget and its BuildTargetGroup " +
    "(EditorUserBuildSettings.activeBuildTarget / selectedBuildTargetGroup). " +
    "Lightweight companion to build_get_targets when you only need the active " +
    "target. Gate-free.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
