// M22 Plan 1 / T22.1.5 — Cost hints for the heavy tools' output profiles.
//
// Agents that reason about prompt cost before choosing an output profile need
// a machine-readable signal of *roughly* how expensive each profile is per
// tool. This module is that signal: a per-tool, per-profile cost band plus a
// short recommended-tool-chain narrative.
//
// The bands are deliberately coarse (`small` | `medium` | `large`) and framed
// as approximate token ranges. They are not measurements — they are planning
// hints so an agent can pick `compact` first, expand on demand, and avoid
// pulling a 100 KB `full` dump into context when it only needed a count.
//
// This is a pure data module (no I/O, no cross-file runtime imports) so it
// loads cleanly under `node --experimental-strip-types` in tests.

import type { OutputProfile } from "../output-profile.js";

// ---------------------------------------------------------------------------
// Cost bands.
// ---------------------------------------------------------------------------

export type CostBand = "small" | "medium" | "large";

/**
 * Approximate token range for each cost band.
 *
 * The ranges are intentionally wide — they describe the *typical* payload a
 * profile produces for that tool on a representative asset, not a hard bound.
 * Real payloads can fall outside the range on very large or very small inputs;
 * `page_size` is the mechanism that enforces an actual cap.
 */
export const COST_BAND_TOKENS: Record<CostBand, { min: number; max: number }> = {
  small: { min: 0, max: 800 },
  medium: { min: 800, max: 4000 },
  large: { min: 4000, max: 0 }, // 0 max = unbounded (use page_size)
};

export interface ProfileCostHint {
  /** Cost band for this profile on this tool. */
  band: CostBand;
  /**
   * Human-readable approximate token range (e.g. "~0.3–0.8k tokens" or
   * "large — unbounded; set page_size"). Surfaced so an agent can display it
   * inline without mapping the band back to a range.
   */
  approxTokens: string;
}

export interface ToolCostHint {
  /**
   * Tool name these hints apply to. Matches a heavy tool that accepts
   * `profile` + `page_size`.
   */
  tool: string;
  /** What the `profile` axis controls on this tool (one-liner). */
  profileControls: string;
  /** What `page_size` pages on this tool (one-liner). */
  pageSizePages: string;
  /** Per-profile cost band. Every heavy tool ships all three profiles. */
  profiles: Record<OutputProfile, ProfileCostHint>;
}

/**
 * Format a cost band into a human-readable approximate-token string.
 */
export function describeBand(band: CostBand): string {
  switch (band) {
    case "small":
      return "~0.3–0.8k tokens";
    case "medium":
      return "~1–4k tokens";
    case "large":
      return "large — unbounded; set page_size to bound it";
  }
}

// ---------------------------------------------------------------------------
// Per-tool cost hints.
//
// The table mirrors the heavy-tool roster in `docs/api/mcp-tools.md`
// §"Output profiles + uniform paging". Keep the two in sync: when a tool is
// added to / removed from the profile-aware surface, update both this table
// and the doc in the same task.
// ---------------------------------------------------------------------------

export const TOOL_COST_HINTS: ToolCostHint[] = [
  {
    tool: "unity_open_mcp_read_asset",
    profileControls:
      "TREE folding (compact = CMP codes + omission counts; full = verbose tree).",
    pageSizePages: "TREE rows.",
    profiles: {
      compact: { band: "small", approxTokens: describeBand("small") },
      balanced: { band: "medium", approxTokens: describeBand("medium") },
      full: { band: "large", approxTokens: describeBand("large") },
    },
  },
  {
    tool: "unity_open_mcp_search_assets",
    profileControls:
      "per-file object cap (compact tight; balanced/full larger).",
    pageSizePages: "result-file matches.",
    profiles: {
      compact: { band: "small", approxTokens: describeBand("small") },
      balanced: { band: "medium", approxTokens: describeBand("medium") },
      full: { band: "large", approxTokens: describeBand("large") },
    },
  },
  {
    tool: "unity_open_mcp_scene_get_data",
    profileControls:
      "scene overview vs nested children vs transforms (depth / per-node verbosity).",
    pageSizePages: "flattened node stream.",
    profiles: {
      compact: { band: "small", approxTokens: describeBand("small") },
      balanced: { band: "medium", approxTokens: describeBand("medium") },
      full: { band: "large", approxTokens: describeBand("large") },
    },
  },
  {
    tool: "unity_open_mcp_find_references",
    profileControls:
      "counts/groupings (compact) vs per-asset list (balanced) vs field locations (full).",
    pageSizePages: "referencing-assets list.",
    profiles: {
      compact: { band: "small", approxTokens: describeBand("small") },
      balanced: { band: "medium", approxTokens: describeBand("medium") },
      full: { band: "large", approxTokens: describeBand("large") },
    },
  },
  {
    tool: "unity_open_mcp_validate_edit",
    profileControls: "counts by severity (compact) vs full issues list (balanced/full).",
    pageSizePages: "issues list.",
    profiles: {
      compact: { band: "small", approxTokens: describeBand("small") },
      balanced: { band: "medium", approxTokens: describeBand("medium") },
      full: { band: "large", approxTokens: describeBand("large") },
    },
  },
  {
    tool: "unity_open_mcp_scan_paths",
    profileControls: "counts by severity (compact) vs full issues list (balanced/full).",
    pageSizePages: "issues list.",
    profiles: {
      compact: { band: "small", approxTokens: describeBand("small") },
      balanced: { band: "medium", approxTokens: describeBand("medium") },
      full: { band: "large", approxTokens: describeBand("large") },
    },
  },
  {
    tool: "unity_open_mcp_component_get",
    profileControls:
      "top-level serialized fields (compact) vs + leaf public properties (balanced) vs nested serialized children (full).",
    pageSizePages: "combined fields+properties stream.",
    profiles: {
      compact: { band: "small", approxTokens: describeBand("small") },
      balanced: { band: "medium", approxTokens: describeBand("medium") },
      full: { band: "large", approxTokens: describeBand("large") },
    },
  },
];

/**
 * The default recommended page sizes per tool. These are starting points, not
 * hard caps — an agent can choose a different `page_size`. Surfaced as part of
 * the cost-hints block so an agent learns a sensible starting page size
 * alongside the cost band.
 */
export const RECOMMENDED_PAGE_SIZE: Record<string, number> = {
  unity_open_mcp_read_asset: 40,
  unity_open_mcp_search_assets: 25,
  unity_open_mcp_scene_get_data: 50,
  unity_open_mcp_find_references: 50,
  unity_open_mcp_validate_edit: 25,
  unity_open_mcp_scan_paths: 25,
  unity_open_mcp_component_get: 25,
};

// ---------------------------------------------------------------------------
// Recommended tool chains.
//
// Short narratives that frame the canonical way to accomplish a common task.
// These are agent-facing guidance, so they MUST stay clean of internal IDs
// (AGENTS.md §"No internal references"): no milestone numbers, no specs paths,
// no execution-plan task references.
// ---------------------------------------------------------------------------

export interface RecommendedToolChain {
  /** Short name for the chain (e.g. "asset-inspect"). */
  name: string;
  /** One-line description of the task this chain accomplishes. */
  task: string;
  /** Ordered tool names (or short prose steps). */
  steps: string[];
}

export const RECOMMENDED_TOOL_CHAINS: RecommendedToolChain[] = [
  {
    name: "discover",
    task: "Learn the available surface before assuming tool names or schemas.",
    steps: [
      "unity_open_mcp_capabilities",
      "unity_open_mcp_manage_tools(action=\"list_groups\")",
      "unity_open_mcp_ping",
    ],
  },
  {
    name: "asset-inspect",
    task: "Read an asset cheaply, then drill into the part you need.",
    steps: [
      "unity_open_mcp_read_asset (default compact) — get the folded overview + omission counts",
      "unity_open_mcp_read_asset(component=\"...\" or path=\"...\") — drill down without re-fetching",
      "only escalate to profile=\"full\" when the folded view is insufficient",
    ],
  },
  {
    name: "find-references",
    task: "Find what references an asset without flooding context.",
    steps: [
      "unity_open_mcp_find_references (default compact) — counts + folder groupings",
      "if a folder is interesting, page its assets with page_size + cursor",
      "escalate to profile=\"verbose\" only for the specific field locations you need",
    ],
  },
  {
    name: "mutate-then-verify",
    task: "Mutate scoped to paths_hint, then read the gate delta inline.",
    steps: [
      "mutate with gate=\"enforce\" + non-empty paths_hint",
      "read gate.delta + agentNextSteps from the response (no separate read_console needed)",
      "on gate failure, prefer unity_open_mcp_apply_fix with dry_run: true first",
    ],
  },
  {
    name: "verify-api-before-coding",
    task:
      "Verify Unity API details before writing C# — LLM training data frequently contains incorrect or outdated Unity APIs.",
    steps: [
      "unity_open_mcp_search_assets — find actual shaders / materials / scripts in the project",
      "unity_open_mcp_find_members — reflect the real type + member signatures",
      "unity_open_mcp_type_schema — drill into fields/properties on a type",
      "only then unity_open_mcp_execute_csharp / invoke_method with the verified API",
    ],
  },
  {
    name: "component-inspect",
    task: "Read a component cheaply, then expand only the slice you need.",
    steps: [
      "unity_open_mcp_component_get (default compact) — top-level serialized fields",
      "unity_open_mcp_component_get(property_path=\"...\") — drill into one subtree",
      "escalate to profile=\"balanced\" / \"full\" or page with page_size + cursor when more detail is needed",
    ],
  },
  {
    name: "batch-setup",
    task:
      "Bundle multi-step setup (create objects + materials + assignments) into one round trip.",
    steps: [
      "unity_open_mcp_batch_execute — one call, many typed tools (gameobject_create × N, material_create × M, ...)",
      "scope paths_hint to the union of every nested scope (scene paths, asset paths)",
      "prefer per-tool arrays (component_modify.fields[], gameobject_modify multi-surface) when a single tool already batches",
      "read gate.delta ONCE at the end — the whole sequence shares one gate cycle + one undo group",
    ],
  },
];

// ---------------------------------------------------------------------------
// Public aggregator — what gets attached to the capabilities response.
// ---------------------------------------------------------------------------

export interface CostHintsBlock {
  /**
   * Per-tool cost hints for the profile-aware heavy tools. Empty array is
   * never returned — the heavy surface is statically known.
   */
  tools: ToolCostHint[];
  /**
   * Default recommended starting page size per tool (agent may override).
   * Absent tools have no paging axis.
   */
  recommendedPageSize: Record<string, number>;
  /**
   * Canonical tool chains for common tasks. Agents can read these to learn
   * the budget-aware way to accomplish a goal before trial-and-error.
   */
  recommendedToolChains: RecommendedToolChain[];
  /**
   * One-line guidance restating the contract: compact is the default; expand
   * on demand; bound with page_size. Kept here so the block is self-describing
   * without an agent having to read the doc.
   */
  guidance: string;
}

export function buildCostHints(): CostHintsBlock {
  return {
    tools: TOOL_COST_HINTS,
    recommendedPageSize: RECOMMENDED_PAGE_SIZE,
    recommendedToolChains: RECOMMENDED_TOOL_CHAINS,
    guidance:
      "Start with the default compact profile on every heavy tool, then " +
      "expand (profile=\"balanced\" / \"full\") or drill down " +
      "(component / path / id flags) only for the slice you need. Set " +
      "page_size to bound any profile; follow pagination.next_cursor to " +
      "resume. compact is the documented default for all heavy tools.",
  };
}
