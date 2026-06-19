import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 8 — gate intelligence. impact_preview is a read-only, gate-free
// pre-mutation projection of what the gate would look at for a given scope.
// It composes VerifyGateAdapter.ResolveRuleIds (which rules apply) with
// AssetDatabase (does each path exist, what asset kind) — it does NOT run a
// rule scan. That is what validate_edit is for. impact_preview answers "if I
// mutate this scope, what will the gate inspect, and how big is the surface?"
// so an agent can size risk before paying for a checkpoint.
//
// Heuristic only — confidence bounds are surfaced in the response so an agent
// treats the risk band as guidance, not ground truth. No Unity-MCP / UCP /
// UUMCP equivalent; composed from existing gate/verify foundations.
export const impactPreview: Tool = {
  name: "unity_open_mcp_impact_preview",
  description:
    "Project the gate's view of a planned mutation scope WITHOUT mutating. Resolves the auto-selected " +
    "verify rule set for the given `paths_hint`, classifies each path (exists / folder / asset kind / " +
    "rules-for-extension), and reports a coarse risk band (low / moderate / high) with an explicit " +
    "confidence level. Read-only and gate-free — it does not run a rule scan, only projects scope. " +
    "Use validate_edit to confirm actual issues before or after mutating.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      paths_hint: {
        type: "array",
        items: { type: "string" },
        minItems: 1,
        description:
          "Scope to project — the same vocabulary the gate uses. Asset paths, folder scopes " +
          "(Assets/-rooted), Packages/manifest.json, or ProjectSettings/*.asset.",
      },
      categories: {
        type: "array",
        items: { type: "string" },
        description:
          "Optional explicit verify rule IDs. Auto-selected from paths when omitted (same semantics " +
          "as validate_edit / scan_paths).",
      },
      include_rules: {
        type: "array",
        items: { type: "string" },
        description: "Allow-list applied to the resolved rule set (same semantics as validate_edit).",
      },
      exclude_rules: {
        type: "array",
        items: { type: "string" },
        description: "Deny-list. Always wins over categories and include_rules.",
      },
    },
    additionalProperties: false,
  },
};
