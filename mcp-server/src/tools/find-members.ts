import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 6 — find_members enhanced in place with richer overload/signature
// metadata. The previous flat signature string now carries structured fields
// (returnType, parameters[], isStatic, isGeneric, genericParameters[] for
// methods; propertyType, canRead, canWrite, isStatic for properties; the
// member kind, declaring type, and assembly) so an agent can pick a specific
// overload and call it via invoke_method without trial-and-error. The legacy
// flat `signature` field stays for compatibility.
export const findMembers: Tool = {
  name: "unity_open_mcp_find_members",
  description:
    "Discover types, methods, and properties for agent planning (reduces blind execute calls). " +
    "Token-bounded: `max_results` caps the returned list, `truncated` always reports how many " +
    "additional matches were dropped. Each member carries a flat `signature` string AND " +
    "structured fields (returnType, parameters[], isStatic, isGeneric, genericParameters[] for " +
    "methods; propertyType, canRead, canWrite for properties) so you can pick a specific " +
    "overload and plan an invoke_method call. Pass `include_signatures: false` to get back " +
    "just names (lighter payload). When `query` matches a method with overloads, every " +
    "overload is listed separately.",
  inputSchema: {
    type: "object",
    properties: {
      query: {
        type: "string",
        description: "Substring filter on type or member name",
      },
      kind: {
        enum: ["type", "method", "property", "all"],
        default: "all",
      },
      assembly_filter: {
        type: "string",
      },
      include_unity_editor: {
        type: "boolean",
        default: true,
      },
      include_project: {
        type: "boolean",
        default: true,
      },
      include_signatures: {
        type: "boolean",
        default: true,
        description:
          "Include the flat `signature` string and structured parameter/generic metadata on " +
          "each member (default). Set false to get back just names for a lighter payload.",
      },
      max_results: {
        type: "integer",
        default: 50,
        maximum: 200,
        description:
          "Maximum number of members to return. Additional matches are counted in `truncated`.",
      },
    },
    additionalProperties: false,
  },
};
