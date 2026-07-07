import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M27 Plan 4 — live `batch_execute`. One HTTP round trip runs many typed tools
// sequentially inside the already-open Editor. NOT the headless batch fallback
// (`batchCapable: false`; not in BATCH_TOOL_NAMES). The bridge wraps the whole
// sequence in a single checkpoint → N steps → one validate/delta gate cycle
// (one undo group for the whole batch), so it is strictly safer than Coplay's
// non-transactional sequential invoke.
//
// Default cap is 25 commands (hard max 100), configurable in
// `.unity-open-mcp/settings.json` (`batchExecuteMaxCommands`).
export const batchExecute: Tool = {
  name: "unity_open_mcp_batch_execute",
  description:
    "Run many typed tools sequentially inside the already-open Editor in a single HTTP round trip — " +
    "cuts agent↔Unity latency and token cost for multi-object setup (e.g. spawn N cubes + create " +
    "materials + assign in one call). Each entry in `commands` carries a full tool id " +
    "(`unity_open_mcp_*` / `unity_senses_*`) plus its `params` object. Lives in the `core` group " +
    "(always visible). Live-only — NOT headless `batchCapable`; there is no batch spawn fallback " +
    "when the bridge is down.\n\n" +
    "Safety: the WHOLE batch shares ONE gate cycle (one checkpoint → all steps → one validate/" +
    "delta) and ONE undo group. `fail_fast: true` (the default) stops on the first step failure " +
    "and marks later entries `skipped`. With `fail_fast: false`, every step runs and per-step " +
    "errors are collected. Partial failure semantics: a successful step is NOT rolled back when a " +
    "later step fails (same as Coplay); the `gate.delta` still reports new issues introduced by " +
    "the partial run, and `agentNextSteps` points at fixes.\n\n" +
    "v1 limits: 25 commands default / 100 hard max (`batchExecuteMaxCommands`). `parallel: true` " +
    "is accepted but ignored with a note — Unity's API is main-thread; sequential execution only. " +
    "Nested meta-tools (`execute_csharp`, `invoke_method`, `execute_menu`) are blocked in v1 — " +
    "agents use batch for typed tools. `batch_execute` cannot be nested inside itself.",
  inputSchema: {
    type: "object",
    required: ["commands", "paths_hint"],
    properties: {
      commands: {
        type: "array",
        minItems: 1,
        items: {
          type: "object",
          required: ["tool", "params"],
          properties: {
            tool: {
              type: "string",
              description:
                "Full MCP tool id (`unity_open_mcp_gameobject_create`, etc.). Must be a " +
                "live-bridge typed tool; not `batch_execute`, `compile_check`, or a meta-tool.",
            },
            params: {
              type: "object",
              description:
                "The tool's input object (its normal `args`). Omit the outer `paths_hint` / " +
                "`gate` — the batch supplies those for the whole sequence.",
            },
          },
          additionalProperties: false,
        },
        description:
          "Ordered list of tool calls to run sequentially. Default cap 25, hard max 100 " +
          "(`batchExecuteMaxCommands`).",
      },
      fail_fast: {
        type: "boolean",
        default: true,
        description:
          "Stop on the first step failure and mark later entries `skipped` (default true). " +
          "Set false to run every step and collect per-step errors.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode applied to the WHOLE batch (one checkpoint → N steps → one validate/delta). " +
          "Default `enforce`.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope for the whole batch — the union of all paths the nested commands may " +
          "touch (scene paths, asset paths). Required when any nested command is mutating.",
      },
      parallel: {
        type: "boolean",
        default: false,
        description:
          "Accepted but ignored in v1 — Unity's API is main-thread, so execution is always " +
          "sequential. The flag is kept for source compatibility with Coplay agents.",
      },
    },
    additionalProperties: false,
  },
};
