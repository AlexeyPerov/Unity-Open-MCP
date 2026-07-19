import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. The agent's pathfinder only runs at runtime —
// in Edit Mode the destination is queued but the agent will not move.
export const navigationAgentSetDestination = makeTool(
  "unity_open_mcp_navigation_agent_set_destination",
  "Set a NavMeshAgent's destination as a world-space 'x,y,z'. Requires Play " +
    "Mode — the agent's pathfinder only runs at runtime. In Edit Mode the " +
    "destination is queued but the agent will not move. Returns pathPending + " +
    "pathStatus (Valid / Partial / Invalid) + isPlaying. Mutating: runs the " +
    "full gate path; paths_hint is the host scene path. Requires the " +
    "navigation extension pack.",
  {
    required: ["destination", "paths_hint"],
        properties: {
          instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
          path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
          name: { type: "string", description: "Host GameObject name (first match)." },
          destination: {
            type: "string",
            description: "World-space destination as 'x,y,z'.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the host." },
          gate: { ...GATE_PROP },
        },
  },
);
