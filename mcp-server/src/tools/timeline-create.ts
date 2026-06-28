import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 6 / T20.6.2 — Timeline create. Compile-gated in the bridge
// (UNITY_OPEN_MCP_EXT_TIMELINE on com.unity.timeline). Mutating: produces a
// .playable asset — paths_hint includes the asset path.
export const timelineCreate: Tool = {
  name: "unity_open_mcp_timeline_create",
  description:
    "Create a new empty TimelineAsset (.playable) at the given asset_path. The " +
    "asset_path is required ('Assets/.../Cutscene.playable'); the parent folder " +
    "must already exist. Mutating: runs the full gate path; paths_hint includes " +
    "the new asset path. Requires the com.unity.timeline package installed.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "Destination asset path. Must end with '.playable' and the parent folder must exist.",
      },
      frame_rate: {
        type: "string",
        description: "Optional frame rate (e.g. '30', '60'). Pass as a string.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — must include the new .playable asset path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
