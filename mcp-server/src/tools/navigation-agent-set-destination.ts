import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. The agent's pathfinder only runs at runtime —
// in Edit Mode the destination is queued but the agent will not move.
export const navigationAgentSetDestination: Tool = {
  name: "unity_open_mcp_navigation_agent_set_destination",
  description:
    "Set a NavMeshAgent's destination as a world-space 'x,y,z'. Requires Play " +
    "Mode — the agent's pathfinder only runs at runtime. In Edit Mode the " +
    "destination is queued but the agent will not move. Returns pathPending + " +
    "pathStatus (Valid / Partial / Invalid) + isPlaying. Mutating: runs the " +
    "full gate path; paths_hint is the host scene path. Requires the " +
    "navigation extension pack.",
  inputSchema: {
    type: "object",
    required: ["destination", "paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      destination: {
        type: "string",
        description: "World-space destination as 'x,y,z'.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the scene path that contains the host.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
