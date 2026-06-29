import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 7 — typed profiler module toggle. Mutates local bookkeeping only
// (no Unity state, no asset writes); gate-free direct-response tool. Unity's
// runtime Profiler API does not expose per-module enable/disable, so this is
// purely a bookkeeping helper reflected back by profiler_get_status /
// profiler_list_modules; for actual module visibility use the Profiler window.
export const profilerEnableModule: Tool = {
  name: "unity_open_mcp_profiler_enable_module",
  description:
    "Toggle the local 'enabled' bookkeeping flag for one named profiler module. Bookkeeping " +
    "only — Unity's runtime API does not expose programmatic per-module toggling, so this does " +
    "not change the Profiler window; use the window for actual module visibility. The flag is " +
    "read back by profiler_get_status (activeModules) and profiler_list_modules. Mutates local " +
    "state only (no asset writes); gate-free.",
  inputSchema: {
    type: "object",
    required: ["module"],
    properties: {
      module: {
        type: "string",
        description:
          "Profiler module name (one of the names returned by profiler_list_modules, e.g. " +
          "\"CPU\", \"GPU\", \"Memory\").",
      },
      enabled: {
        type: "boolean",
        default: true,
        description: "True to mark the module enabled in local bookkeeping; false to disable.",
      },
    },
    additionalProperties: false,
  },
};
