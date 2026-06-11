import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const invokeMethod: Tool = {
  name: "unity_agent_invoke_method",
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
    },
    additionalProperties: false,
  },
};
