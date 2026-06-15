import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const executeCsharp: Tool = {
  name: "unity_open_mcp_execute_csharp",
  description:
    "Compile and run a C# snippet in the Editor (Roslyn). Primary escape hatch — covers most Editor APIs without typed tools.",
  inputSchema: {
    type: "object",
    required: ["code"],
    properties: {
      code: {
        type: "string",
        description: "C# source. Use return x; to produce output.",
      },
      usings: {
        type: "array",
        items: { type: "string" },
        description:
          "Extra using directives beyond defaults (UnityEngine, UnityEditor, etc.)",
      },
      object_ids: {
        type: "array",
        items: { type: "string" },
        description:
          "Instance IDs (or full handle JSON) of live UnityEngine.Objects " +
          "to inject into the snippet. Access them via Refs[index] or " +
          "Ref<T>(index) in the code body. Instance IDs come from the " +
          "'objectId' field of object handles returned by other tools. " +
          "Instance IDs change on domain reload.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Asset paths likely touched; drives scoped gate validation",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
      timeout_ms: {
        type: "integer",
        default: 30000,
        minimum: 1000,
        maximum: 300000,
      },
      max_depth: {
        type: "integer",
        default: 4,
        minimum: 0,
        description:
          "Max recursion depth when serializing the returned object graph (default 4). Composite nodes deeper than this are stringified to bound payload size.",
      },
      max_items: {
        type: "integer",
        default: 100,
        minimum: 0,
        description:
          "Max items emitted per list/enumerable in the returned object graph (default 100). Truncated lists report a `truncated` count of the elided items.",
      },
    },
    additionalProperties: false,
  },
};
