import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 9 — typed scripting-define read. Read-only: the scripting define
// symbols for the active build target group, as a raw ';' string AND a parsed
// list. Gate-free direct-response tool.
export const buildGetDefines: Tool = {
  name: "unity_open_mcp_build_get_defines",
  description:
    "Read-only: scripting define symbols (PlayerSettings.GetScriptingDefineSymbols) for the " +
    "active build target group. Returns the group, the NamedBuildTarget, the raw ';' -joined " +
    "string (\"defines\"), and a parsed \"list\". Use this before build_set_defines to see the " +
    "current set. Gate-free.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
