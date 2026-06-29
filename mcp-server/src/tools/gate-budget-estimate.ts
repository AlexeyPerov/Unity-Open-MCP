import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 8 — gate intelligence. gate_budget_estimate forecasts the cost of
// validating a planned mutation scope before paying for it. It composes
// VerifyGateAdapter.ResolveRuleIds + VerifyCacheService / VerifyRunner to
// return an estimatedDurationMs + estimatedIssueBudget plus a confidence band
// and the basis the estimate was derived from.
//
// Two modes:
//   - "cache"  (default): inspect the most recent VerifyCacheService snapshot.
//                Cheap and deterministic, but coarse (the cache is a single
//                global snapshot, not keyed by scope).
//   - "sample": run a cheap Checkpoint-mode scan over the resolved scope and
//                time it. More accurate, pays one scan.
//
// Heuristic only — checkpoint-mode is lighter than full validation, so the
// duration estimate is a lower bound. Run validate_edit to measure actuals.
// Composed from existing foundations; no equivalent in the broader tool
// landscape.
export const gateBudgetEstimate: Tool = {
  name: "unity_open_mcp_gate_budget_estimate",
  description:
    "Forecast validation duration + issue budget for a planned mutation scope before mutating. Returns " +
    "estimatedDurationMs (a lower bound on the real gate path), estimatedIssueBudget (an upper bound on " +
    "issues the gate might surface), the resolved rule set, and the basis + confidence of the estimate. " +
    "Read-only and gate-free. Use mode 'cache' (default) for a cheap, coarse signal or 'sample' for a " +
    "grounded-but-paid measurement. The estimate is heuristic — run validate_edit for actuals.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      paths_hint: {
        type: "array",
        items: { type: "string" },
        minItems: 1,
        description: "Scope to forecast — the same vocabulary the gate uses.",
      },
      categories: {
        type: "array",
        items: { type: "string" },
        description: "Optional explicit verify rule IDs. Auto-selected from paths when omitted.",
      },
      include_rules: {
        type: "array",
        items: { type: "string" },
        description: "Allow-list applied to the resolved rule set.",
      },
      exclude_rules: {
        type: "array",
        items: { type: "string" },
        description: "Deny-list. Always wins over categories and include_rules.",
      },
      mode: {
        enum: ["cache", "sample"],
        default: "cache",
        description:
          "'cache' (default) inspects the latest VerifyCacheService snapshot (cheap, coarse). 'sample' " +
          "runs a cheap checkpoint-mode scan over the scope and times it (grounded, paid).",
      },
    },
    additionalProperties: false,
  },
};
