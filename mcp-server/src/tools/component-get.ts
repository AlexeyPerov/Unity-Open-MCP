import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 2 — read-only component get. Gate-free, token-bounded.
// Returns the serialized field/property list of one component.
export const componentGet: Tool = {
  name: "unity_open_mcp_component_get",
  description:
    "Read the serialized fields and (optionally) public properties of a single Component on a " +
    "GameObject. Read-only (gate-free). Token-bounded by max_fields. Returns each field's path " +
    "(SerializedProperty path), type, and current value — including nested arrays and child " +
    "objects — so an agent can plan a component_modify without trial-and-error. Address the host " +
    "by instance_id > path > name; identify the component by instance_id (specific instance) or " +
    "type_name (full name preferred, class-name fallback).",
  inputSchema: {
    type: "object",
    properties: {
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
      type_name: {
        type: "string",
        description:
          "Component type to read (full name preferred, class-name fallback). Use this OR " +
          "component_instance_id. Ignored when component_instance_id is set.",
      },
      component_instance_id: {
        type: ["string", "integer"],
        default: 0,
        description:
          "Specific Component instance ID (for types that allow multiples). Takes precedence " +
          "over type_name when set.",
      },
      max_fields: {
        type: ["string", "integer"],
        default: 100,
        minimum: 1,
        description: "Max fields+properties returned; remaining count is reported in 'truncated'.",
      },
      include_properties: {
        type: "boolean",
        default: true,
        description:
          "Include non-serialized public properties (e.g. transform.position, Rigidbody.velocity). " +
          "Default true. Set false to read only SerializedObject fields.",
      },
    },
    additionalProperties: false,
  },
};
