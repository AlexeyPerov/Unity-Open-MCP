import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. Requires the
// navigation extension pack. Mutating: runs the full gate path.
export const navigationLinkAdd: Tool = {
  name: "unity_open_mcp_navigation_link_add",
  description:
    "Add a NavMeshLink component to a GameObject. A link connects two NavMesh " +
    "positions (start/end) — use it for jumps, drops, gaps, or any traversal " +
    "the surface bake cannot infer. Idempotent. Mutating: runs the full gate " +
    "path; paths_hint is the host scene path. Requires the navigation " +
    "extension pack.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      start_pos: {
        type: "string",
        default: "0,0,0",
        description: "Link start position (local space) as 'x,y,z'.",
      },
      end_pos: {
        type: "string",
        default: "0,0,0",
        description: "Link end position (local space) as 'x,y,z'.",
      },
      width: {
        type: "number",
        default: 0,
        description: "Link width (0 = point-to-point).",
      },
      cost_modifier: {
        type: "integer",
        default: -1,
        description: "Traversal cost modifier (-1 = default).",
      },
      bidirectional: {
        type: "boolean",
        default: true,
        description: "Traversable in both directions.",
      },
      auto_update: {
        type: "boolean",
        default: false,
        description: "Re-bake the link when positions change.",
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
