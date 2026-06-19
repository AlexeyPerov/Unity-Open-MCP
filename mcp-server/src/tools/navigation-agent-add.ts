import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. Mutating: runs the full gate path.
export const navigationAgentAdd: Tool = {
  name: "unity_open_mcp_navigation_agent_add",
  description:
    "Add a NavMeshAgent component to a GameObject and configure its radius / " +
    "height / speed / angular speed / acceleration / stopping distance. The " +
    "agent does nothing until navigation_agent_set_destination is called (and " +
    "the scene is in Play Mode). Idempotent. Mutating: runs the full gate " +
    "path; paths_hint is the host scene path. Requires the navigation " +
    "extension pack.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      radius: { type: "number", default: 0.5, description: "Agent radius (world units)." },
      height: { type: "number", default: 2, description: "Agent height (world units)." },
      speed: { type: "number", default: 3.5, description: "Max movement speed." },
      angular_speed: { type: "number", default: 120, description: "Max turn speed (deg/sec)." },
      acceleration: { type: "number", default: 8, description: "Max acceleration." },
      stopping_distance: {
        type: "number",
        default: 0.1,
        description: "Stop this far from the destination.",
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
