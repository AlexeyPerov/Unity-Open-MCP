import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const runTests = makeTool(
  "unity_senses_run_tests",
  "Run Unity EditMode or PlayMode tests and return structured results. " +
    "EditMode runs in-process and results are available within seconds. " +
    "PlayMode survives domain reload via file handoff. " +
    "Filter by assembly, namespace, class, or method name. " +
    "Results are retrieved by the server polling a results file (NOT via " +
    "unity_senses_pull_events, which streams only console/editor-state). " +
    "Note: when the filter matches ZERO tests, Execute runs nothing and the " +
    "result carries status completed with summary.total:0 and a note — this " +
    "looks like 'no results' but is a no-match filter, not a failure. " +
    "Tip: to run a single test method deterministically, invoke the test " +
    "class/method directly via unity_open_mcp_invoke_method — NUnit assertion " +
    "failures come back verbatim in the error.",
  {
    properties: {
          play_mode: {
            type: "boolean",
            default: false,
            description: "Run PlayMode tests instead of EditMode. Requires domain reload.",
          },
          assembly_name: {
            type: "string",
            description: "Filter: run tests in this assembly only (e.g. 'MyAssembly'). Must name a real test assembly or the run matches nothing.",
          },
          test_namespace: {
            type: "string",
            description: "Filter: run tests under this namespace.",
          },
          test_class: {
            type: "string",
            description:
              "Filter: run tests in this class. Maps to NUnit groupNames (a partial/group " +
              "match), so a partial class name may match more or less than expected — " +
              "prefer the full class name.",
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
              "Client-side polling budget (seconds) the server waits for the results file. " +
              "Not a bridge parameter — the bridge run is async and writes a results file " +
              "the server polls. Raise for PlayMode (domain reload) or large suites.",
          },
          run_id: {
            type: "string",
            description:
              "Optional run id. Omit to let the bridge generate a safe one (<pid>-<unixMs>). " +
              "Must be 1..128 chars of [A-Za-z0-9._-] only.",
          },
        },
  },
);
