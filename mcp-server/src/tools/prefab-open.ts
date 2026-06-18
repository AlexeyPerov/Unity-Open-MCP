import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — typed prefab stage open. Mutating: runs the full gate path
// (the stage lifecycle touches editor state). Scope paths_hint to the prefab
// asset path.
export const prefabOpen: Tool = {
  name: "unity_open_mcp_prefab_open",
  description:
    "Open the prefab edit stage for a prefab asset path. Modifications inside the stage propagate " +
    "to all instances. Pair with unity_open_mcp_prefab_close to exit the stage when done. Mutating: " +
    "runs the full gate path; `paths_hint` is the prefab asset path.",
  inputSchema: {
    type: "object",
    required: ["prefab_asset_path", "paths_hint"],
    properties: {
      prefab_asset_path: {
        type: "string",
        description: "Prefab asset path to open in the edit stage.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Prefab asset path (the gate's validation scope).",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
