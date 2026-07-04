import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 2 — typed component add. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the scene that contains the target.
export const componentAdd: Tool = {
  name: "unity_open_mcp_component_add",
  description:
    "Add one or more Components to a GameObject in the active scene by type name. Undo-recorded. " +
    "Mutating: runs the full gate path; `paths_hint` is the scene path that contains the host. " +
    "Each type is resolved by full name (preferred) or class-name fallback; per-type errors " +
    "(unknown type, not a Component, disallows multiples) are accumulated in the response so a " +
    "single bad entry does not abort the batch. Use unity_open_mcp_component_list_all to discover " +
    "attachable types first.",
  inputSchema: {
    type: "object",
    required: ["component_types", "paths_hint"],
    properties: {
      component_types: {
        type: "array",
        items: { type: "string" },
        description:
          "Component type names to add. Each entry may be a fully-qualified type name (preferred) " +
          "or a bare class name. Example: [\"UnityEngine.Rigidbody\", \"UnityEngine.BoxCollider\"].",
      },
      instance_id: {
        type: ["string", "integer"],
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
