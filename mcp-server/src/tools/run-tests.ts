import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const runTests: Tool = {
  name: "unity_agent_run_tests",
  description:
    "Run Unity EditMode or PlayMode tests and return structured results. " +
    "EditMode runs in-process and results are available within seconds. " +
    "PlayMode survives domain reload via file handoff. " +
    "Filter by assembly, namespace, class, or method name.",
  inputSchema: {
    type: "object",
    properties: {
      play_mode: {
        type: "boolean",
        default: false,
        description: "Run PlayMode tests instead of EditMode. Requires domain reload.",
      },
      assembly_name: {
        type: "string",
        description: "Filter: run tests in this assembly only (e.g. 'MyAssembly').",
      },
      test_namespace: {
        type: "string",
        description: "Filter: run tests under this namespace.",
      },
      test_class: {
        type: "string",
        description: "Filter: run tests in this class (full or partial name).",
      },
      test_method: {
        type: "string",
        description: "Filter: run only this test method (fully qualified name).",
      },
      include_passes: {
        type: "boolean",
        default: true,
        description:
          "Include passing tests in the results array. Set false to return " +
          "only failures/inconclusive — the summary still reports the full " +
          "counts. Recommended for large suites to avoid truncation.",
      },
      timeout_ms: {
        type: "integer",
        default: 60000,
        minimum: 1000,
        maximum: 600000,
        description:
          "Maximum time to wait for results (PlayMode may take longer due to domain reload).",
      },
    },
    additionalProperties: false,
  },
};
