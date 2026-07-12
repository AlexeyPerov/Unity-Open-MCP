import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 2 / M30 Plan 4 — read-only component get. Gate-free, profile-bounded.
// Returns the serialized field/property list of one component.
export const componentGet: Tool = {
  name: "unity_open_mcp_component_get",
  description:
    "Read the serialized fields and (optionally) public properties of a single Component on a " +
    "GameObject. Read-only (gate-free). Defaults to profile=\"compact\" (top-level serialized " +
    "fields only; public properties omitted unless include_properties is true). Returns each " +
    "field's path (SerializedProperty path), type, and current value so an agent can plan a " +
    "component_modify without trial-and-error. Use property_path to drill into one subtree; use " +
    "profile=\"balanced\" / \"full\" or page_size + cursor to expand without unbounded dumps. " +
    "Address the host by instance_id > path > name; identify the component by instance_id " +
    "(specific instance) or type_name (full name preferred, class-name fallback).",
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
      profile: {
        type: "string",
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "Output verbosity. compact (default) = top-level SerializedObject fields only; " +
          "balanced = + leaf public properties; full = + nested serialized children up to max_depth.",
      },
      page_size: {
        type: ["string", "integer"],
        minimum: 1,
        description:
          "When set, page the combined fields+properties stream. Response carries pagination " +
          "with next_cursor (component_get:<offset>). Omit to receive the whole profile-shaped " +
          "payload up to max_fields.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation token from a previous pagination.next_cursor. Mismatched cursors " +
          "restart from the first page.",
      },
      property_path: {
        type: "string",
        description:
          "Drill into one SerializedProperty subtree by path (e.g. m_LocalPosition, m_Color.r). " +
          "Omit to read the component root per profile.",
      },
      max_depth: {
        type: ["string", "integer"],
        default: 3,
        minimum: 1,
        description: "Nested serialized depth cap when profile=\"full\" (default 3).",
      },
      max_fields: {
        type: ["string", "integer"],
        minimum: 1,
        description:
          "Max fields+properties collected. Fields hidden by this cap are reported in the top-level " +
          "`truncated` count (raise max_fields or switch to profile=full to see them); when page_size " +
          "is set, fields after the current page window are reported separately in pagination.truncated " +
          "(page on via next_cursor). Profile defaults: compact=40, balanced=100, full=200.",
      },
      include_properties: {
        type: "boolean",
        description:
          "Include non-serialized public properties (e.g. Rigidbody.velocity). Profile default: " +
          "false for compact, true for balanced/full. Set explicitly to override.",
      },
    },
    additionalProperties: false,
  },
};
