import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 8 — gate intelligence. mutation_explain turns a finished gate run
// (or an explicit checkpoint) into a human-readable narrative alongside a
// structured summary an agent can branch on. It composes:
//   - BridgeGateRunHistory.Latest  (the most recent gate run)
//   - BridgeGateRunHistory.Records  (when tool_name filters by mutating tool)
//   - CheckpointStore               (an explicit checkpoint_id)
//   - GatePolicy delta math         (already recorded on the run record)
//
// Two contracts:
//   - checkpoint_id provided → compare that checkpoint against CURRENT project
//     state (fresh delta). Authoritative "what happened to this scope?" answer.
//   - no checkpoint_id      → project the most recent gate run (optionally
//     filtered by tool_name) into a narrative using the delta captured at
//     mutation time.
//
// Read-only and gate-free. No Unity-MCP / UCP / UUMCP equivalent; composed
// from existing checkpoint/delta/run-history foundations.
export const mutationExplain: Tool = {
  name: "unity_open_mcp_mutation_explain",
  description:
    "Explain a finished mutation + gate delta in human-readable form. By default projects the most " +
    "recent gate run into a narrative + structured summary (outcome, new/resolved error & warning " +
    "counts, gate durations, agentNextSteps). Pass `checkpoint_id` to compare a known checkpoint " +
    "against current project state, or `tool_name` to target the latest run of a specific mutating " +
    "tool. Read-only and gate-free. When the gate was skipped or the run predates current editor " +
    "state, the delta may be empty — pass checkpoint_id for a scoped comparison.",
  inputSchema: {
    type: "object",
    properties: {
      checkpoint_id: {
        type: "string",
        description:
          "Optional. When set, the explanation compares this checkpoint against the CURRENT project " +
          "state (fresh delta). Useful for 'what changed since I took this checkpoint?'. Mutually " +
          "preferred over tool_name when both are set.",
      },
      tool_name: {
        type: "string",
        description:
          "Optional. When set (and no checkpoint_id), explain the latest recorded gate run whose " +
          "mutating tool name matches (e.g. 'unity_open_mcp_prefab_apply').",
      },
    },
    additionalProperties: false,
  },
};
