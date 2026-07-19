import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.4 — Input System extension tool. Requires the input
// system extension pack. Mutating: runs the full gate path.
export const inputsystemActionmapAdd = makeTool(
  "unity_open_mcp_inputsystem_actionmap_add",
  "Add a new InputActionMap to an existing .inputactions asset. A map groups " +
    "related actions (e.g. 'Player', 'UI'). Fails if a map of that name already " +
    "exists. Mutating: runs the full gate path; paths_hint is the .inputactions " +
    "asset path. Requires the input system extension pack installed in the project.",
  {
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
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the .inputactions asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
