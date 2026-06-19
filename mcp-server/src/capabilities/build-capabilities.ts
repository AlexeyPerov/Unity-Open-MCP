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
  unity_open_mcp_compile_check: "diagnostics",
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
  // M16 Plan 1 — typed project & asset management tools.
  unity_open_mcp_assets_create_folder: "typed-editor",
  unity_open_mcp_assets_copy: "typed-editor",
  unity_open_mcp_assets_move: "typed-editor",
  unity_open_mcp_assets_delete: "typed-editor",
  unity_open_mcp_assets_refresh: "typed-editor",
  unity_open_mcp_material_create: "typed-editor",
  unity_open_mcp_material_get_properties: "typed-editor",
  unity_open_mcp_material_set_property: "typed-editor",
  unity_open_mcp_material_get_keywords: "typed-editor",
  unity_open_mcp_material_set_keyword: "typed-editor",
  unity_open_mcp_material_set_shader: "typed-editor",
  unity_open_mcp_shader_list_all: "typed-editor",
  unity_open_mcp_shader_get_data: "typed-editor",
  unity_open_mcp_prefab_instantiate: "typed-editor",
  unity_open_mcp_prefab_create: "typed-editor",
  unity_open_mcp_prefab_open: "typed-editor",
  unity_open_mcp_prefab_close: "typed-editor",
  unity_open_mcp_prefab_save: "typed-editor",
  unity_open_mcp_prefab_apply: "typed-editor",
  unity_open_mcp_prefab_revert: "typed-editor",
  unity_open_mcp_prefab_unpack: "typed-editor",
  unity_open_mcp_prefab_get_overrides: "typed-editor",
  unity_open_mcp_prefab_status: "typed-editor",
  // M16 Plan 2 — typed GameObject & component tools.
  unity_open_mcp_gameobject_create: "typed-editor",
  unity_open_mcp_gameobject_destroy: "typed-editor",
  unity_open_mcp_gameobject_duplicate: "typed-editor",
  unity_open_mcp_gameobject_find: "typed-editor",
  unity_open_mcp_gameobject_modify: "typed-editor",
  unity_open_mcp_gameobject_set_parent: "typed-editor",
  unity_open_mcp_component_add: "typed-editor",
  unity_open_mcp_component_destroy: "typed-editor",
  unity_open_mcp_component_get: "typed-editor",
  unity_open_mcp_component_modify: "typed-editor",
  unity_open_mcp_component_list_all: "typed-editor",
  // M16 Plan 3 — typed scene management tools.
  unity_open_mcp_scene_create: "typed-editor",
  unity_open_mcp_scene_open: "typed-editor",
  unity_open_mcp_scene_save: "typed-editor",
  unity_open_mcp_scene_unload: "typed-editor",
  unity_open_mcp_scene_set_active: "typed-editor",
  unity_open_mcp_scene_list_opened: "typed-editor",
  unity_open_mcp_scene_get_data: "typed-editor",
  unity_open_mcp_scene_get_dirty_summary: "typed-editor",
  unity_open_mcp_scene_focus: "typed-editor",
  // M16 Plan 4 — typed Package Manager tools.
  unity_open_mcp_package_list: "typed-editor",
  unity_open_mcp_package_search: "typed-editor",
  unity_open_mcp_package_add: "typed-editor",
  unity_open_mcp_package_remove: "typed-editor",
  unity_open_mcp_package_get_info: "typed-editor",
  unity_open_mcp_package_get_dependencies: "typed-editor",
  unity_open_mcp_package_check: "typed-editor",
  // M16 Plan 5 — typed console / editor state / selection / undo / tags / layers.
  unity_open_mcp_console_clear: "typed-editor",
  unity_open_mcp_console_log: "typed-editor",
  unity_open_mcp_editor_set_state: "typed-editor",
  unity_open_mcp_selection_get: "typed-editor",
  unity_open_mcp_selection_set: "typed-editor",
  unity_open_mcp_editor_undo: "typed-editor",
  unity_open_mcp_editor_redo: "typed-editor",
  unity_open_mcp_editor_get_tags: "typed-editor",
  unity_open_mcp_editor_get_layers: "typed-editor",
  unity_open_mcp_editor_add_tag: "typed-editor",
  unity_open_mcp_editor_add_layer: "typed-editor",
  // M16 Plan 6 — typed reflection / scripts / object data tools.
  // type_schema / script_read / object_get_data are read-only; script_write /
  // script_delete / object_modify are mutating (paths_hint scoped).
  unity_open_mcp_type_schema: "typed-editor",
  unity_open_mcp_script_read: "typed-editor",
  unity_open_mcp_script_write: "typed-editor",
  unity_open_mcp_script_delete: "typed-editor",
  unity_open_mcp_object_get_data: "typed-editor",
  unity_open_mcp_object_modify: "typed-editor",
  // M16 Plan 7 — typed profiler session / diagnostics tools. Most mutate
  // editor state (no asset writes) — gate-free direct-response tools.
  // profiler_save_data is the lone asset-writing mutator (paths_hint scoped
  // to the destination .json).
  unity_open_mcp_profiler_start: "diagnostics",
  unity_open_mcp_profiler_stop: "diagnostics",
  unity_open_mcp_profiler_get_status: "diagnostics",
  unity_open_mcp_profiler_get_config: "diagnostics",
  unity_open_mcp_profiler_set_config: "diagnostics",
  unity_open_mcp_profiler_list_modules: "diagnostics",
  unity_open_mcp_profiler_enable_module: "diagnostics",
  unity_open_mcp_profiler_clear_data: "diagnostics",
  unity_open_mcp_profiler_save_data: "diagnostics",
  unity_open_mcp_profiler_load_data: "diagnostics",
  unity_open_mcp_profiler_get_script_stats: "diagnostics",
  // M16 Plan 8 — typed gate intelligence tools. All read-only, gate-free
  // direct-response tools that compose checkpoint / validate / delta / verify
  // / run-history foundations. impact_preview + gate_budget_estimate are
  // pre-mutation; mutation_explain is post-mutation.
  unity_open_mcp_impact_preview: "gate-intelligence",
  unity_open_mcp_gate_budget_estimate: "gate-intelligence",
  unity_open_mcp_mutation_explain: "gate-intelligence",
  // M16 Plan 9 — typed build pipeline + project-settings tools. Reads are
  // gate-free; build_set_target / build_set_scenes / build_set_defines /
  // build_start / settings_set_* run the full gate path (build_start also
  // requires the deny bypass — BuildPipeline.BuildPlayer is on the deny list).
  unity_open_mcp_build_get_targets: "build-settings",
  unity_open_mcp_build_get_active_target: "build-settings",
  unity_open_mcp_build_set_target: "build-settings",
  unity_open_mcp_build_get_scenes: "build-settings",
  unity_open_mcp_build_set_scenes: "build-settings",
  unity_open_mcp_build_start: "build-settings",
  unity_open_mcp_build_get_defines: "build-settings",
  unity_open_mcp_build_set_defines: "build-settings",
  unity_open_mcp_settings_get_player: "build-settings",
  unity_open_mcp_settings_set_player: "build-settings",
  unity_open_mcp_settings_get_quality: "build-settings",
  unity_open_mcp_settings_set_quality: "build-settings",
  unity_open_mcp_settings_get_physics: "build-settings",
  unity_open_mcp_settings_set_physics: "build-settings",
  unity_open_mcp_settings_get_lighting: "build-settings",
  unity_open_mcp_settings_set_lighting: "build-settings",
  unity_open_mcp_capabilities: "capability-discovery",
  unity_open_mcp_list_rules: "capability-discovery",
  unity_open_mcp_generate_skill: "capability-discovery",
  unity_senses_run_tests: "agent-senses",
  unity_senses_screenshot: "agent-senses",
  unity_senses_read_console: "agent-senses",
  unity_senses_profiler_capture: "agent-senses",
  unity_senses_profiler_memory: "agent-senses",
  unity_senses_profiler_rendering: "agent-senses",
  unity_senses_spatial_query: "agent-senses",
};

function categoryFor(toolName: string): string {
  return TOOL_CATEGORY[toolName] ?? "other";
}

// ---------------------------------------------------------------------------
// Planned typed tools — the curated editor surface (forward-looking)
// ---------------------------------------------------------------------------
//
// Single source of truth for the planned typed-editor surface. Each entry is a
// concrete tool name (not a wildcard) so `unity_open_mcp_capabilities` can
// advertise the full planned surface with per-tool guidance, letting agents
// learn the shape of upcoming typed tools without discovering gaps by trial.
//
// This list is synchronized with the per-plan typed-tool tables in
// `specs/execution/M16/execution-plan-*.md`. When a plan implements a tool,
// move it out of here (it becomes a real Tool in `src/tools/`). When the
// planned surface changes, update both this array and the matching plan table.

export interface PlannedTool {
  name: string;
  category: string;
  description: string;
  guidance: string;
}

// Generic guidance reused across many planned typed tools.
const MUTATE_VIA_EXECUTE_CSHARP =
  "Planned typed surface. Use execute_csharp / invoke_method today.";

export const PLANNED_TOOLS: PlannedTool[] = [
  // --- Plan 1: Assets -----------------------------------------------------
  // Plan 1 typed tools are now implemented (see mcp-server/src/tools/).
  // The entries that used to live here have been removed; the remaining
  // Plan 1 references (none) live as real Tool definitions.

  // --- Plan 2: GameObject & Components ------------------------------------
  // Plan 2 typed tools are now implemented (see mcp-server/src/tools/).
  // The entries that used to live here have been removed.

  // --- Plan 3: Scene ------------------------------------------------------
  // Plan 3 typed tools are now implemented (see mcp-server/src/tools/).
  // The entries that used to live here have been removed.

  // --- Plan 4: Packages ---------------------------------------------------
  // Plan 4 typed tools are now implemented (see mcp-server/src/tools/).
  // The entries that used to live here have been removed.

  // --- Plan 5: Console + Editor state/selection/tags/layers/undo ----------
  // Plan 5 typed tools are now implemented (see mcp-server/src/tools/).
  // The entries that used to live here have been removed.

  // --- Plan 6: Reflection, scripts, objects -------------------------------
  // Plan 6 typed tools are now implemented (see mcp-server/src/tools/).
  // The entries that used to live here have been removed.

  // --- Plan 7: Profiler session ------------------------------------------
  // Plan 7 typed tools are now implemented (see mcp-server/src/tools/).
  // The entries that used to live here have been removed.

  // --- Plan 8: Gate intelligence -----------------------------------------
  // Plan 8 typed tools are now implemented (see mcp-server/src/tools/).
  // The entries that used to live here have been removed.

  // --- Plan 9: Build & Settings ------------------------------------------
  // Plan 9 typed tools are now implemented (see mcp-server/src/tools/).
  // The entries that used to live here have been removed; the remaining
  // Plan 9 references (none) live as real Tool definitions.
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
