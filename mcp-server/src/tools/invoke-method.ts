import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const invokeMethod: Tool = {
  name: "unity_open_mcp_invoke_method",
  description: "Call a method via reflection.",
  inputSchema: {
    type: "object",
    required: ["type_name", "method_name"],
    properties: {
      type_name: {
        type: "string",
        description: "Fully qualified type name",
      },
      method_name: {
        type: "string",
      },
      args: {
        type: "array",
        description: "JSON-serializable arguments",
        items: {},
      },
      is_static: {
        type: "boolean",
        default: false,
      },
      assembly_name: {
        type: "string",
        description:
          "Optional assembly simple name if type is ambiguous",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
      timeout_ms: {
        type: "integer",
        default: 30000,
      },
      max_depth: {
        type: "integer",
        default: 4,
        minimum: 0,
        description:
          "Max recursion depth when serializing the returned object graph (default 4).",
      },
      max_items: {
        type: "integer",
        default: 100,
        minimum: 0,
        description:
          "Max items emitted per list/enumerable in the returned object graph (default 100). Truncated lists report a `truncated` count.",
      },
    },
    additionalProperties: false,
  },
};
