import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 9 — typed build-target enumeration. Read-only: lists every
// BuildTarget that resolves to a known BuildTargetGroup (Unknown is skipped),
// flags the installed / active ones, and reports the active target + group.
// Gate-free direct-response tool.
export const buildGetTargets = makeTool(
  "unity_open_mcp_build_get_targets",
  "Read-only: enumerate available build targets. Each entry carries the BuildTarget name, its " +
    "BuildTargetGroup, whether the backend is installed locally, and whether it is the active " +
    "target. The active target + group are also surfaced at the top level. BuildTarget values " +
    "that resolve to BuildTargetGroup.Unknown are skipped. Use this before build_set_target to " +
    "discover a valid target name. Gate-free; token-bounded.",
  {
    properties: {},
  },
);
