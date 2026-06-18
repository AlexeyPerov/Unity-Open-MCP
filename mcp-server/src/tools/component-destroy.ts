import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 2 — typed component destroy. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the scene that contains the host.
export const componentDestroy: Tool = {
  name: "unity_open_mcp_component_destroy",
  description:
    "Remove one or more Components from a GameObject in the active scene by type name. " +
    "Undo-recorded. Mutating: runs the full gate path; `paths_hint` is the scene path that " +
    "contains the host. Each type is resolved by full name or class-name fallback; per-type " +
    "errors (unknown type, not present) are accumulated in the response. Use component_list_all " +
    "to discover attachable types and component_get to inspect a component before removing it.",
  inputSchema: {
    type: "object",
    required: ["component_types", "paths_hint"],
    properties: {
      component_types: {
        type: "array",
        items: { type: "string" },
        description:
          "Component type names to remove (full name preferred, class-name fallback). When a type " +
          "allows multiples, only the first match is removed per call.",
      },
      instance_id: {
        type: "integer",
        default: 0,
        description: "Host GameObject instance ID. Highest priority resolver.",
      },
      path: {
        type: "string",
        description: "Host hierarchy path \"Root/Child\".",
      },
      name: {
        type: "string",
        description: "Host GameObject name (first match). Lowest priority resolver.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the scene path that contains the host.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
