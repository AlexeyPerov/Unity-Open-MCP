import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const compileCheck: Tool = {
  name: "unity_open_mcp_compile_check",
  description:
    "Headless compile check: spawn a batch Unity (no live bridge needed) and return " +
    "structured C# compiler errors (CSxxxx code, file, line, message) collected across " +
    "all assemblies. Use this when the live bridge is offline (a compile error put the " +
    "Editor in a bad state) to self-diagnose whether a project compiles. " +
    "Batch-only — uses the auto-discovered Unity (OS-default Hub install paths + " +
    "UNITY_HUB env override), or UNITY_PATH when set. UNITY_PROJECT_PATH is used when " +
    "set, else the instance lock's projectPath. Returns unity_not_discovered when no " +
    "Unity install can be found.",
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
