// Capability-discovery builder.
//
// Aggregates the full capability surface (tools + verify rules + fixes) that
// `unity_open_mcp_capabilities` returns. Every registered tool ships as
// `implemented: true`; planned typed tools (the curated editor surface) are
// listed with `implemented: false` and guidance so agents get structured
// "not yet available" signals instead of discovering gaps by trial and error.
//
// Pure transformation module: dependencies (registered tools, batch allow-list,
// rule/fix catalogs) are passed in by the caller so this file has zero runtime
// cross-file imports and loads cleanly under `node --experimental-strip-types`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import type {
  RuleCapability,
  FixCapability,
  CapabilityStatus,
} from "./rule-catalog.js";

// ---------------------------------------------------------------------------
// Route metadata — mirrors tool-router.ts / compressible-router.ts decisions
// ---------------------------------------------------------------------------

const OFFLINE_TOOLS: ReadonlySet<string> = new Set(["unity_open_mcp_list_assets"]);

const OFFLINE_FIRST_TOOLS: ReadonlySet<string> = new Set([
  "unity_open_mcp_find_references",
  "unity_open_mcp_read_asset",
  "unity_open_mcp_search_assets",
]);

export type RoutePolicy = "live" | "offline" | "offline-first" | "compressible";

function routePolicyFor(toolName: string): RoutePolicy {
  if (OFFLINE_TOOLS.has(toolName)) return "offline";
  if (OFFLINE_FIRST_TOOLS.has(toolName)) return "offline-first";
  return "live";
}

// ---------------------------------------------------------------------------
// Tool categories — semantic grouping that does not leak milestone IDs
// ---------------------------------------------------------------------------

const TOOL_CATEGORY: Record<string, string> = {
  unity_open_mcp_ping: "core",
  unity_open_mcp_execute_csharp: "core",
  unity_open_mcp_invoke_method: "core",
  unity_open_mcp_execute_menu: "core",
  unity_open_mcp_find_members: "core",
  unity_open_mcp_editor_status: "core",
  unity_open_mcp_validate_edit: "gate-and-verify",
  unity_open_mcp_checkpoint_create: "gate-and-verify",
  unity_open_mcp_delta: "gate-and-verify",
  unity_open_mcp_find_references: "gate-and-verify",
  unity_open_mcp_scan_paths: "gate-and-verify",
  unity_open_mcp_apply_fix: "gate-and-verify",
  unity_open_mcp_scan_all: "gate-and-verify",
  unity_open_mcp_baseline_create: "gate-and-verify",
  unity_open_mcp_regression_check: "gate-and-verify",
  unity_open_mcp_reserialize: "asset-intelligence",
  unity_open_mcp_read_asset: "asset-intelligence",
  unity_open_mcp_search_assets: "asset-intelligence",
  unity_open_mcp_list_assets: "asset-intelligence",
  unity_open_mcp_capabilities: "capability-discovery",
  unity_agent_generate_skill: "capability-discovery",
  unity_agent_run_tests: "agent-senses",
  unity_agent_screenshot: "agent-senses",
  unity_agent_read_console: "agent-senses",
  unity_agent_profiler_capture: "agent-senses",
  unity_agent_profiler_memory: "agent-senses",
  unity_agent_profiler_rendering: "agent-senses",
  unity_agent_spatial_query: "agent-senses",
};

function categoryFor(toolName: string): string {
  return TOOL_CATEGORY[toolName] ?? "other";
}

// ---------------------------------------------------------------------------
// Planned typed tools — the curated editor surface (forward-looking)
// ---------------------------------------------------------------------------

export interface PlannedTool {
  name: string;
  category: string;
  description: string;
  guidance: string;
}

export const PLANNED_TOOLS: PlannedTool[] = [
  {
    name: "unity_open_mcp_assets_*",
    category: "typed-editor",
    description:
      "Typed asset CRUD, material, and prefab staging helpers.",
    guidance:
      "Planned typed surface. Use execute_csharp / invoke_method for asset CRUD today.",
  },
  {
    name: "unity_open_mcp_gameobject_*",
    category: "typed-editor",
    description: "Typed GameObject and component lifecycle operations.",
    guidance:
      "Planned typed surface. Use invoke_method or execute_csharp for hierarchy/component edits today.",
  },
  {
    name: "unity_open_mcp_scene_*",
    category: "typed-editor",
    description: "Typed scene lifecycle and data operations.",
    guidance:
      "Planned typed surface. Use execute_csharp for scene open/save/additive today.",
  },
  {
    name: "unity_open_mcp_package_*",
    category: "typed-editor",
    description:
      "Package manager list/add/remove/search.",
    guidance:
      "Planned typed surface. Read Packages/manifest.json directly or use execute_csharp with PackageManagerClient today.",
  },
  {
    name: "unity_open_mcp_console_clear",
    category: "typed-editor",
    description: "Clear the Unity console.",
    guidance:
      "Planned typed surface. Use execute_csharp with LogEntries.Clear() today.",
  },
  {
    name: "unity_open_mcp_editor_set_state",
    category: "typed-editor",
    description: "Set Editor play/pause state.",
    guidance:
      "Planned typed surface. Use execute_csharp with EditorApplication.isPlaying today.",
  },
  {
    name: "unity_open_mcp_selection_*",
    category: "typed-editor",
    description: "Get/set the active Editor selection.",
    guidance:
      "Planned typed surface. Use execute_csharp with Selection.activeObject today.",
  },
  {
    name: "unity_open_mcp_type_schema",
    category: "reflection",
    description:
      "Generate a JSON schema for any loadable C# type's public fields/properties.",
    guidance:
      "Planned reflection surface. Use find_members to inspect type members today.",
  },
  {
    name: "unity_open_mcp_script_*",
    category: "reflection",
    description: "Read/write/delete project script files.",
    guidance:
      "Planned reflection surface. Use execute_csharp with File IO today.",
  },
  {
    name: "unity_open_mcp_object_*",
    category: "reflection",
    description: "Get/modify serialized object data.",
    guidance:
      "Planned reflection surface. Use invoke_method or execute_csharp with SerializedObject today.",
  },
  {
    name: "unity_open_mcp_profiler_*",
    category: "diagnostics",
    description: "Profiler session/module/save/load/clear helpers.",
    guidance:
      "Planned diagnostics surface. Use unity_agent_profiler_capture for frame data today.",
  },
  {
    name: "unity_open_mcp_impact_preview",
    category: "gate-intelligence",
    description: "Preview the gate impact of a planned mutation.",
    guidance: "Planned gate-intelligence surface. Run validate_edit before mutating today.",
  },
  {
    name: "unity_open_mcp_gate_budget_estimate",
    category: "gate-intelligence",
    description: "Estimate gate cost/scope before a mutation.",
    guidance: "Planned gate-intelligence surface. Scope paths_hint tightly to limit gate cost today.",
  },
  {
    name: "unity_open_mcp_mutation_explain",
    category: "gate-intelligence",
    description: "Explain what a mutation did and why the gate flagged it.",
    guidance:
      "Planned gate-intelligence surface. Read gate.delta and agentNextSteps in mutation responses today.",
  },
];

// ---------------------------------------------------------------------------
// Capability descriptors
// ---------------------------------------------------------------------------

/**
 * Static routing narrative. Mirrors `docs/api/mcp-tools.md` §Route
 * policy + §Batch support + the `BATCH_TOOL_NAMES` / blocked-tool
 * lists in `batch-spawn.ts`. Kept concise; per-tool `batchCapable`
 * flags live on each {@link ToolCapability}.
 */
export const ROUTING_SUMMARY: RoutingSummary = {
  liveDefault: true,
  batchFallback: true,
  batchRequirements: ["UNITY_PATH", "UNITY_PROJECT_PATH"],
  // Mutating meta-tools that need an interactive Editor UI or live
  // compilation; intentionally rejected by the batch entry point.
  batchBlocked: [
    {
      tool: "unity_open_mcp_execute_csharp",
      reason: "Requires a live Editor compile context.",
    },
    {
      tool: "unity_open_mcp_invoke_method",
      reason: "Requires a live Editor reflection context.",
    },
    {
      tool: "unity_open_mcp_execute_menu",
      reason: "Menu execution needs the Editor UI; most menus fail in -batchmode.",
    },
  ],
  // Agent senses (screenshots, profiler, console, spatial, run_tests)
  // and gate mutations need a live Editor — they have no batch form.
  liveOnlyCategories: ["agent-senses"],
  perToolFlag: "batchCapable",
};

export interface ToolCapability {
  name: string;
  implemented: boolean;
  status: CapabilityStatus;
  category: string;
  description: string;
  routePolicy: RoutePolicy;
  batchCapable: boolean;
  /** Input schema mirrored from the Tool definition. */
  inputSchema: Tool["inputSchema"];
  /** Present only for planned tools. */
  guidance?: string;
}

export interface CapabilitiesResult {
  tools: ToolCapability[];
  rules: RuleCapability[];
  fixes: FixCapability[];
  counts: {
    toolsImplemented: number;
    toolsPlanned: number;
    rulesImplemented: number;
    rulesPlanned: number;
    fixesImplemented: number;
    fixesPlanned: number;
  };
  /**
   * One-shot routing narrative for agents. Lets a batch-narrative
   * agent learn how tool calls are routed without reading repo docs.
   * Concise on purpose — per-tool details live on each
   * {@link ToolCapability} entry, not here.
   */
  routing: RoutingSummary;
}

/**
 * Compact routing summary embedded in the capabilities response. This
 * is the agent-facing counterpart of `docs/api/mcp-tools.md` §Route
 * policy / §Batch support — it does NOT duplicate the full prose.
 */
export interface RoutingSummary {
  /** Most tools prefer the live bridge when it is connected. */
  liveDefault: boolean;
  /**
   * When the live bridge is unavailable, only `batchCapable` tools
   * fall back to a headless Unity spawn.
   */
  batchFallback: boolean;
  /** Env vars a headless batch spawn requires. */
  batchRequirements: string[];
  /** Mutating meta-tools that are intentionally blocked in batch. */
  batchBlocked: { tool: string; reason: string }[];
  /** Tool categories that are live-only (no batch fallback). */
  liveOnlyCategories: string[];
  /**
   * Pointer back to the per-tool `batchCapable` flag — agents should
   * read that flag on each tool, not scan this summary for the list.
   */
  perToolFlag: string;
}

export interface CapabilitiesFilter {
  /** Filter to a single surface (`tools` | `rules` | `fixes`). Omit for all. */
  kind?: "tools" | "rules" | "fixes";
  /** When false, omit planned/unimplemented capabilities. */
  includePlanned?: boolean;
}

// ---------------------------------------------------------------------------
// Dependencies — injected by the caller so this module stays import-free
// ---------------------------------------------------------------------------

export interface BuildCapabilitiesDeps {
  tools: Tool[];
  batchToolNames: ReadonlySet<string>;
  rules: RuleCapability[];
  fixes: FixCapability[];
}

export function buildCapabilities(
  deps: BuildCapabilitiesDeps,
  filter: CapabilitiesFilter = {},
): CapabilitiesResult {
  const includePlanned = filter.includePlanned !== false;

  const implementedTools: ToolCapability[] = deps.tools.map((tool) => ({
    name: tool.name,
    implemented: true,
    status: "implemented",
    category: categoryFor(tool.name),
    description: tool.description ?? "",
    routePolicy: routePolicyFor(tool.name),
    batchCapable: deps.batchToolNames.has(tool.name),
    inputSchema: tool.inputSchema,
  }));

  const plannedTools: ToolCapability[] = includePlanned
    ? PLANNED_TOOLS.map((p) => ({
        name: p.name,
        implemented: false,
        status: "planned",
        category: p.category,
        description: p.description,
        routePolicy: "live",
        batchCapable: false,
        inputSchema: { type: "object" as const, properties: {} },
        guidance: p.guidance,
      }))
    : [];

  const tools = [...implementedTools, ...plannedTools];

  const rules = includePlanned
    ? deps.rules
    : deps.rules.filter((r) => r.implemented);

  const fixes = includePlanned
    ? deps.fixes
    : deps.fixes.filter((f) => f.implemented);

  return {
    tools: filter.kind === "rules" || filter.kind === "fixes" ? [] : tools,
    rules: filter.kind === "tools" || filter.kind === "fixes" ? [] : rules,
    fixes: filter.kind === "tools" || filter.kind === "rules" ? [] : fixes,
    counts: {
      toolsImplemented: implementedTools.length,
      toolsPlanned: plannedTools.length,
      rulesImplemented: deps.rules.filter((r) => r.implemented).length,
      rulesPlanned: includePlanned
        ? deps.rules.filter((r) => !r.implemented).length
        : 0,
      fixesImplemented: deps.fixes.filter((f) => f.implemented).length,
      fixesPlanned: includePlanned
        ? deps.fixes.filter((f) => !f.implemented).length
        : 0,
    },
    // The routing summary is independent of the kind filter — agents
    // that ask for `kind: "rules"` still benefit from the routing
    // narrative. It is constant, so no per-call computation.
    routing: ROUTING_SUMMARY,
  };
}
