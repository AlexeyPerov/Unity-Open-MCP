import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.4 — Input System extension tool. Requires the input
// system extension pack. Mutating: runs the full gate path.
export const inputsystemActionmapAdd: Tool = {
  name: "unity_open_mcp_inputsystem_actionmap_add",
  description:
    "Add a new InputActionMap to an existing .inputactions asset. A map groups " +
    "related actions (e.g. 'Player', 'UI'). Fails if a map of that name already " +
    "exists. Mutating: runs the full gate path; paths_hint is the .inputactions " +
    "asset path. Requires the input system extension pack installed in the project.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "map_name", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "'Assets/'-rooted path to the existing '.inputactions' asset.",
      },
      map_name: {
        type: "string",
        description: "Unique name for the new ActionMap within the asset.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the .inputactions asset path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
