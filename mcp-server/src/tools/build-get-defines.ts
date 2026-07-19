import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 9 — typed scripting-define read. Read-only: the scripting define
// symbols for the active build target group, as a raw ';' string AND a parsed
// list. Gate-free direct-response tool.
export const buildGetDefines = makeTool(
  "unity_open_mcp_build_get_defines",
  "Read-only: scripting define symbols (PlayerSettings.GetScriptingDefineSymbols) for the " +
    "active build target group. Returns the group, the NamedBuildTarget, the raw ';' -joined " +
    "string (\"defines\"), and a parsed \"list\". Use this before build_set_defines to see the " +
    "current set. Gate-free.",
  {
    properties: {},
  },
);
