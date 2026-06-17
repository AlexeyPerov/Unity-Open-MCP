import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const compileCheck: Tool = {
  name: "unity_open_mcp_compile_check",
  description:
    "Headless compile check: spawn a batch Unity (no live bridge needed) and return " +
    "structured C# compiler errors (CSxxxx code, file, line, message) collected across " +
    "all assemblies. Use this when the live bridge is offline (a compile error put the " +
    "Editor in a bad state) to self-diagnose whether a project compiles. " +
    "Batch-only — requires UNITY_PATH + UNITY_PROJECT_PATH.",
  inputSchema: {
    type: "object",
    properties: {
      timeout_ms: {
        type: "integer",
        default: 300000,
        minimum: 30000,
        maximum: 600000,
        description:
          "Maximum time to wait for compilation to settle (ms). " +
          "Clamped to [30000, 600000].",
      },
    },
    additionalProperties: false,
  },
};
