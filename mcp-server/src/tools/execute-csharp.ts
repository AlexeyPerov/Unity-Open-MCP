import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const executeCsharp: Tool = {
  name: "unity_agent_execute_csharp",
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
    },
    additionalProperties: false,
  },
};
