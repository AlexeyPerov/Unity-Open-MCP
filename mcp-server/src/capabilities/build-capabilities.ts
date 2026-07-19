// Capability-discovery builder.
//
// Aggregates the full capability surface (tools + verify rules + fixes +
// tool groups) that `unity_open_mcp_capabilities` returns. Every registered
// tool ships as `implemented: true`; planned typed tools (the curated editor
// surface) are listed with `implemented: false` and guidance so agents get
// structured "not yet available" signals instead of discovering gaps by
// trial and error.
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
import {
  TOOL_GROUPS,
  DEFAULT_ENABLED_GROUPS,
  groupFor,
  toolGroupAssignment,
  type ToolGroup,
} from "./tool-groups.js";
import {
  buildCostHints,
  type CostHintsBlock,
} from "./cost-hints.js";
import {
  lifecycleFor,
  buildLifecycle,
  type LifecycleClass,
  type LifecycleBlock,
} from "./lifecycle.js";
import { getFixIndex } from "./rule-catalog.js";

// ---------------------------------------------------------------------------
// M31-optimizations Plan 4 / T4.2 (M2) — module-level singletons for the
// constant capability structures. `buildCostHints()` and `buildLifecycle()`
// derive purely from module-level constants (their own catalogs) — the inline
// comment at the former call site even said "Constant, so no per-call
// computation" yet rebuilt them on every `buildCapabilities` call. Both are
// now computed once at first import. `getFixIndex()` is the lazy singleton
// for `FIX_CATALOG`'s issue-code → fix-id index (see rule-catalog.ts).
// ---------------------------------------------------------------------------
const COST_HINTS: CostHintsBlock = buildCostHints();
const LIFECYCLE_BLOCK: LifecycleBlock = buildLifecycle();

// ---------------------------------------------------------------------------
// Route metadata — mirrors tool-router.ts / compressible-router.ts decisions
// ---------------------------------------------------------------------------

const OFFLINE_TOOLS: ReadonlySet<string> = new Set(["unity_open_mcp_list_assets"]);

const OFFLINE_FIRST_TOOLS: ReadonlySet<string> = new Set([
  "unity_open_mcp_find_references",
  "unity_open_mcp_read_asset",
  "unity_open_mcp_search_assets",
  // M20 Plan 5 / T20.5 — read-only typed reads that parse asset metadata / JSON
  // and are offline-routeable in principle (the offline index can answer them
  // without a live Editor). Listed offline-first so a disconnected client still
  // gets a useful answer.
  "unity_open_mcp_list_assets_of_type",
  "unity_open_mcp_asmdef_list",
  "unity_open_mcp_asmdef_get",
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
//
// M31-optimizations Plan 4 / T4.5 (L13) — `TOOL_CATEGORY` is now DERIVED from
// `TOOL_GROUP_ASSIGNMENT` (tool-groups.ts) at module load, not hand-maintained
// as a parallel ~270-entry record. The group vocabulary and the category
// vocabulary agree for ~95% of tools (a tool in group `core` has category
// `core`, etc.); the small remainder where category ≠ group, plus the
// always-visible meta-tools (group=null) that need a real category, live in
// the explicit `TOOL_CATEGORY_OVERRIDES` map below.
//
// The parity test in capabilities/tool-groups.test.ts asserts every tool in
// ALL_TOOLS resolves to a non-`"other"` category — adding a tool without a
// group assignment AND without an override now fails loudly instead of
// silently falling through to `"other"`.

/**
 * The handful of tools whose category intentionally diverges from their
 * group, plus the always-visible meta-tools (group=null) that need a real
 * category. Two cases:
 *
 *   1. **Grouped tools where category ≠ group** — historical reasons where
 *      the session-visibility group and the capability-classification
 *      category split (e.g. `compile_check` is in the `gate-and-verify`
 *      group for session visibility but classified as `diagnostics`).
 *
 *   2. **Meta-tools (group=null)** — always-visible server meta-tools that
 *      are not in any group (they bypass the manage_tools visibility
 *      system) but still need a category label for the capabilities surface.
 *
 * Documented exhaustively so the parity test can enforce that every entry
 * here is a real, deliberate exception. Exported so the parity test can
 * inspect the override surface directly.
 */
export const TOOL_CATEGORY_OVERRIDES: Record<string, string> = {
  // --- Case 1: grouped tools whose category diverges from their group --------
  // compile_check is in the gate-and-verify GROUP (so it shows up alongside
  // validate_edit / scan_paths in manage_tools) but classified under
  // diagnostics in capabilities (it's a compile-error probe, not a verify
  // rule runner). The split is deliberate and surfaces in lifecycle too
  // (compile_check carries the editor_instance_locked note).
  unity_open_mcp_compile_check: "diagnostics",
  // dependencies + reimport_package are grouped (gate-and-verify /
  // typed-editor) but previously had NO entry in the hand-maintained
  // TOOL_CATEGORY record, silently falling through to "other". The
  // pre-change category is preserved here as a documented override so the
  // capabilities output stays byte-identical (T4.5 acceptance: "same
  // category for every tool"). A future deliberate reclassification can
  // flip these to their group id; until then the parity test treats them
  // as allowed-other.
  unity_open_mcp_dependencies: "other",
  unity_open_mcp_reimport_package: "other",

  // --- Case 2: always-visible meta-tools (group=null) -----------------------
  // The three capability-discovery entry points share one category so agents
  // reading the capabilities surface see them grouped. They have null group
  // because they MUST stay always-visible (an agent needs to reach
  // capabilities / list_rules / generate_skill before any other tool).
  unity_open_mcp_capabilities: "capability-discovery",
  unity_open_mcp_list_rules: "capability-discovery",
  unity_open_mcp_generate_skill: "capability-discovery",
  // The remaining meta-tools (pull_events, read_compile_errors,
  // bridge_status, manage_tools, restart_editor, resource_pressure) carry
  // the default `"other"` category — they are operator/debug surfaces that
  // do not fit any capability classification bucket. They are intentionally
  // NOT in this override map so the parity test can distinguish "deliberate
  // other" (these) from "missing category" (a new tool added without
  // thought). See ALLOWED_OTHER_CATEGORY_TOOLS below.
};

/**
 * Tools that intentionally carry the default `"other"` category. Two flavors:
 *
 *   - **Meta-tools** (group=null) — always-visible operator/debug surfaces
 *     that have no group assignment and no override. The parity test allows
 *     them through `"other"` explicitly so a NEW tool that lands in neither
 *     bucket fails loudly.
 *
 *   - **Grouped tools whose override is `"other"`** — `dependencies` and
 *     `reimport_package` (preserved pre-change behavior; see overrides).
 *
 * Documented; adding one means updating both this set and the test. Exported
 * so the parity test can enumerate the allowed-other surface.
 */
export const ALLOWED_OTHER_CATEGORY_TOOLS: ReadonlySet<string> = new Set([
  // Meta-tools (group=null, no override).
  "unity_open_mcp_manage_tools",
  "unity_senses_pull_events",
  "unity_open_mcp_read_compile_errors",
  "unity_open_mcp_bridge_status",
  "unity_open_mcp_restart_editor",
  "unity_open_mcp_resource_pressure",
  // Grouped tools whose override preserves the pre-change "other".
  "unity_open_mcp_dependencies",
  "unity_open_mcp_reimport_package",
]);

/**
 * The derived tool → category map. Built once at module load: starts from
 * the group assignment (group = category for the agreed majority), then
 * applies {@link TOOL_CATEGORY_OVERRIDES} for the documented exceptions.
 * The result is frozen so callers cannot mutate it.
 */
const TOOL_CATEGORY: Readonly<Record<string, string>> = (() => {
  const out: Record<string, string> = {};
  // Step 1: derive category === group for every grouped tool.
  for (const [tool, group] of Object.entries(toolGroupAssignment())) {
    out[tool] = group;
  }
  // Step 2: apply the documented exceptions (overrides win).
  for (const [tool, category] of Object.entries(TOOL_CATEGORY_OVERRIDES)) {
    out[tool] = category;
  }
  return out;
})();

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
 * Static routing narrative. Mirrors `docs/api/routing-lifecycle.md` plus the
 * `BATCH_TOOL_NAMES` / blocked-tool
 * lists in `batch-spawn.ts`. Kept concise; per-tool `batchCapable`
 * flags live on each {@link ToolCapability}.
 */
export const ROUTING_SUMMARY: RoutingSummary = {
  liveDefault: true,
  batchFallback: true,
  batchRequirements: ["UNITY_PATH", "UNITY_PROJECT_PATH"],
  // M26 Plan 3 — all four meta-tools are now batch-capable, so no
  // meta-tool is on the batch-blocked list. (execute_menu is gated by a
  // batch-viable allow-list inside the C# entry point; non-viable menus
  // return menu_not_viable_in_batchmode, but the tool itself is batch-capable.)
  batchBlocked: [],
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
  /**
   * M18 Plan 2 / T18.2.3 — group id (from tool-groups.ts). Tools with no
   * group assignment (server meta-tools) carry `null`. Lets an agent learn
   * which manage_tools group to activate to surface a planned tool.
   */
  group: string | null;
  /**
   * Lifecycle class describing the recovery concern an agent must reason
   * about when this call fails / stalls: none | compile-reload | modal-dialog
   * | scene-dirty | process-stale. Read it before the call to pick a recovery
   * strategy (see `lifecycleBlock` for the taxonomy).
   */
  lifecycle: LifecycleClass;
  /**
   * Optional tool-specific constraint or secondary recovery concern (e.g.
   * compile_check notes its batch-only lock; build_start notes the secondary
   * scene-dirty concern). Null when the class is self-describing.
   */
  lifecycleNote: string | null;
}

/**
 * Per-group capability entry as it appears in the capabilities response.
 *
 * Compiled-state only — does NOT reflect per-session activation. The session
 * activation state is exposed via manage_tools `list_groups`. The two concerns
 * are intentionally split (see M18 execution-plan.md §resolved decisions).
 */
export interface ToolGroupCapability {
  /** Stable lowercase group id (e.g. `"core"`, `"navigation"`). */
  id: string;
  description: string;
  /** True when the group is enabled by default for fresh sessions. */
  defaultEnabled: boolean;
  /** Count of compiled-in tools in the group. */
  toolCount: number;
  /** Compiled-in tool names in the group, sorted. */
  tools: string[];
  /**
   * Whether the group's Unity domain dependency compiled in. `true` for
   * groups without a domainDefine (always compiled in); `false` when the
   * bridge did not compile the domain in. The bridge detection is the
   * `availableBridgeTools` dependency — when absent (bridge offline),
   * domain-gated groups report `null` (unknown) and the agent should treat
   * them as "may or may not be available".
   */
  available: boolean | null;
  /** Free-text reason for `available: false` (dependency missing) or null. */
  availableReason: string | null;
  /** Unity package the group depends on, for install guidance. */
  unityPackage: string | null;
  /** Bridge compile define that gates this group (null when not gated). */
  domainDefine: string | null;
  /**
   * M20 Plan 7 / T20.7.0 — when true, the group opts into package-detection
   * auto-activation: it appears in a fresh session's ListTools automatically
   * when its `unityPackage` is installed, no manual manage_tools call
   * required. False (or absent) for manual-activation-only groups.
   */
  autoActivate: boolean;
  /**
   * M20 Plan 7 / T20.7.0 — the package id that gates this group's auto-
   * activation (mirrors `unityPackage` when `autoActivate` is true, null
   * otherwise). Surfaced so an agent can understand WHY a group is visible.
   */
  packageDependency: string | null;
  /**
   * Usage hint surfaced to the agent. Always points at manage_tools so the
   * agent knows to activate the group before invoking its tools.
   */
  usageHint: string;
}

export interface CapabilitiesResult {
  tools: ToolCapability[];
  rules: RuleCapability[];
  fixes: FixCapability[];
  /**
   * M18 Plan 2 / T18.2.3 — tool-group catalog (compiled-state only). Lets an
   * agent learn which groups exist, what they contain, and whether the
   * domain dependency is compiled in — before any tool call. Per-session
   * activation state is in manage_tools `list_groups`, not here.
   */
  toolGroups: ToolGroupCapability[];
  counts: {
    toolsImplemented: number;
    toolsPlanned: number;
    rulesImplemented: number;
    rulesPlanned: number;
    fixesImplemented: number;
    fixesPlanned: number;
    /** Count of catalog groups enabled by default for a fresh session. */
    toolGroupsDefaultEnabled: number;
    /** M18 Plan 2 — total group count. */
    toolGroupsTotal: number;
  };
  /**
   * One-shot routing narrative for agents. Lets a batch-narrative
   * agent learn how tool calls are routed without reading repo docs.
   * Concise on purpose — per-tool details live on each
   * {@link ToolCapability} entry, not here.
   */
  routing: RoutingSummary;
  /**
   * M22 Plan 1 / T22.1.5 — per-tool cost hints (approximate token cost per
   * output profile) + recommended tool chains. Lets an agent reason about
   * prompt cost before choosing a profile, and learn the budget-aware way to
   * accomplish common tasks. Independent of the kind filter — agents asking
   * for rules/fixes still benefit from the cost narrative.
   */
  costHints: CostHintsBlock;
  /**
   * Lifecycle policy taxonomy (5 classes) + the per-tool `lifecycle` field on
   * each ToolCapability. Lets an agent reason about recovery before a call:
   * read the class to learn what the bridge does (settle / dirty guard /
   * heartbeat) and how to recover when the call fails or stalls. Independent
   * of the kind filter — agents asking for rules/fixes still benefit from the
   * recovery narrative.
   */
  lifecycleBlock: LifecycleBlock;
  /**
   * specs/feedback.md 2026-07-03 — whether the live bridge was reachable when
   * this capabilities response was assembled. When false, every domain-gated
   * tool group reports `available: null` (compiled-state unknown) regardless of
   * whether the Unity package is installed; callers should treat `null`
   * availability as "I can't tell because the bridge is down" rather than
   * "genuinely unavailable". Lets an agent short-circuit: bridgeReachable=false
   * means the per-group `available:null` is a reachability artifact, not a
   * compile-state verdict.
   */
  bridgeReachable: boolean;
}

/**
 * Compact routing summary embedded in the capabilities response. This
 * is the agent-facing counterpart of `docs/api/routing-lifecycle.md` — it
 * does NOT duplicate the full prose.
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
  /**
   * M18 Plan 2 / T18.2.3 — optional: the set of tool names the bridge has
   * compiled in. Used to report per-group compiled-state availability
   * (`available: true/false` on each ToolGroupCapability). When omitted
   * (bridge offline, local capability call), domain-gated groups report
   * `available: null` (unknown) and the agent is directed at
   * manage_tools(list_groups) which probes the live bridge.
   */
  availableBridgeTools?: ReadonlySet<string>;
}

export function buildCapabilities(
  deps: BuildCapabilitiesDeps,
  filter: CapabilitiesFilter = {},
): CapabilitiesResult {
  const includePlanned = filter.includePlanned !== false;

  // M31-optimizations Plan 4 / T4.2 (M2) — the per-tool ToolCapability[] is
  // the most expensive per-call structure (~270 tools, each with a
  // `lifecycleFor` lookup). The inputs (`deps.tools` + `deps.batchToolNames`)
  // are module-level constants on the production path (ALL_TOOLS /
  // BATCH_TOOL_NAMES), so memoize on their identity: production hits, tests
  // with fixture arrays miss (and rebuild — the prior behavior). The cached
  // array is reused as-is; downstream consumers do not mutate it.
  const implementedTools = implementedToolCapabilities(
    deps.tools,
    deps.batchToolNames,
  );

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
        group: groupFor(p.name),
        // Planned tools have no lifecycle declaration yet — `none` is the safe
        // default until the tool ships and is classified.
        lifecycle: "none" as LifecycleClass,
        lifecycleNote: null,
      }))
    : [];

  const tools = [...implementedTools, ...plannedTools];

  const rules = includePlanned
    ? deps.rules
    : deps.rules.filter((r) => r.implemented);

  const fixes = includePlanned
    ? deps.fixes
    : deps.fixes.filter((f) => f.implemented);

  const toolGroups = buildToolGroups(
    implementedTools.map((t) => t.name),
    deps.availableBridgeTools,
  );

  return {
    tools: filter.kind === "rules" || filter.kind === "fixes" ? [] : tools,
    rules: filter.kind === "tools" || filter.kind === "fixes" ? [] : rules,
    fixes: filter.kind === "tools" || filter.kind === "rules" ? [] : fixes,
    // toolGroups is independent of the kind filter — agents that ask for
    // `kind: "rules"` still benefit from the group catalog.
    toolGroups,
    counts: {
      toolsImplemented: implementedTools.length,
      toolsPlanned: plannedTools.length,
      // M31-optimizations Plan 4 / T4.2 — single-pass counts. Previously the
      // rules/fixes catalog was scanned four times (.filter().length for
      // implemented + planned on each). Now one walk tallies all four in a
      // single pass; planned counts short-circuit to 0 when includePlanned is
      // false (the prior code computed them and then discarded the result).
      ...countRulesAndFixes(deps.rules, deps.fixes, includePlanned),
      toolGroupsDefaultEnabled: TOOL_GROUPS.filter((g) => g.defaultEnabled).length,
      toolGroupsTotal: TOOL_GROUPS.length,
    },
    // The routing summary is independent of the kind filter — agents
    // that ask for `kind: "rules"` still benefit from the routing
    // narrative. M31-optimizations Plan 4 / T4.2 — hoisted to a module-level
    // constant (ROUTING_SUMMARY already is one).
    routing: ROUTING_SUMMARY,
    // M22 Plan 1 / T22.1.5 — cost hints are independent of the kind filter for
    // the same reason as routing: an agent asking for rules/fixes still wants
    // to know how to budget its next heavy-tool call. M31-optimizations Plan
    // 4 / T4.2 — hoisted to COST_HINTS (computed once at module load).
    costHints: COST_HINTS,
    // Lifecycle taxonomy is independent of the kind filter for the same
    // reason: an agent asking for rules/fixes still benefits from the recovery
    // narrative. M31-optimizations Plan 4 / T4.2 — hoisted to LIFECYCLE_BLOCK
    // (computed once at module load).
    lifecycleBlock: LIFECYCLE_BLOCK,
    // specs/feedback.md 2026-07-03 — surface bridge reachability at the top
    // level so a caller can distinguish "group genuinely not compiled in"
    // (available:false + reason) from "I can't tell because the bridge is
    // down" (available:null when bridgeReachable:false). Without this flag an
    // agent reading all-null availability can't tell reachability from a real
    // compile-state gap.
    bridgeReachable: deps.availableBridgeTools !== undefined,
  };
}

// ---------------------------------------------------------------------------
// M31-optimizations Plan 4 / T4.2 (M2) — memoization helpers
// ---------------------------------------------------------------------------

/**
 * Single cache entry for the per-tool {@link ToolCapability} list. Keyed by
 * the identity of (`tools`, `batchToolNames`) — both are module-level
 * constants on the production path, so the cache always hits there. Tests
 * that pass fixture arrays get distinct identities and rebuild (the prior
 * per-call behavior), which keeps them honest about the inputs they pass.
 */
interface ToolCapabilitiesCacheEntry {
  tools: Tool[];
  batchToolNames: ReadonlySet<string>;
  capabilities: ToolCapability[];
}

let toolCapabilitiesCache: ToolCapabilitiesCacheEntry | null = null;

function implementedToolCapabilities(
  tools: Tool[],
  batchToolNames: ReadonlySet<string>,
): ToolCapability[] {
  const cached = toolCapabilitiesCache;
  if (cached && cached.tools === tools && cached.batchToolNames === batchToolNames) {
    return cached.capabilities;
  }
  const capabilities = tools.map((tool) => {
    const lifecycle = lifecycleFor(tool.name);
    return {
      name: tool.name,
      implemented: true,
      status: "implemented" as CapabilityStatus,
      category: categoryFor(tool.name),
      description: tool.description ?? "",
      routePolicy: routePolicyFor(tool.name),
      batchCapable: batchToolNames.has(tool.name),
      inputSchema: tool.inputSchema,
      group: groupFor(tool.name),
      lifecycle: lifecycle.class,
      lifecycleNote: lifecycle.note ?? null,
    };
  });
  toolCapabilitiesCache = { tools, batchToolNames, capabilities };
  return capabilities;
}

/**
 * Tally implemented + planned counts for rules and fixes in a single pass
 * over each catalog. Returns the four `counts` fields. When `includePlanned`
 * is false the planned counters short-circuit to 0 (the prior code computed
 * them via `.filter(!implemented).length` and then discarded the result).
 */
function countRulesAndFixes(
  rules: RuleCapability[],
  fixes: FixCapability[],
  includePlanned: boolean,
): {
  rulesImplemented: number;
  rulesPlanned: number;
  fixesImplemented: number;
  fixesPlanned: number;
} {
  let rulesImplemented = 0;
  let rulesPlanned = 0;
  for (const r of rules) {
    if (r.implemented) rulesImplemented++;
    else if (includePlanned) rulesPlanned++;
  }
  let fixesImplemented = 0;
  let fixesPlanned = 0;
  for (const f of fixes) {
    if (f.implemented) fixesImplemented++;
    else if (includePlanned) fixesPlanned++;
  }
  return { rulesImplemented, rulesPlanned, fixesImplemented, fixesPlanned };
}

// ---------------------------------------------------------------------------
// M18 Plan 2 / T18.2.3 — build the toolGroups catalog block (compiled-state
// only). Per-session activation lives in manage_tools; this surface reports
// what compiled in, not what is currently active.
// ---------------------------------------------------------------------------

function buildToolGroups(
  implementedToolNames: string[],
  availableBridgeTools: ReadonlySet<string> | undefined,
): ToolGroupCapability[] {
  // Bucket the implemented tool names by group. groupFor returns null for
  // meta-tools; those are intentionally absent from any group block.
  const toolsByGroup = new Map<string, string[]>();
  for (const name of implementedToolNames) {
    const g = groupFor(name);
    if (g === null) continue;
    const list = toolsByGroup.get(g) ?? [];
    list.push(name);
    toolsByGroup.set(g, list);
  }

  return TOOL_GROUPS.map((g) => {
    const tools = (toolsByGroup.get(g.id) ?? []).slice().sort();
    return buildOneGroup(g, tools, availableBridgeTools);
  });
}

function buildOneGroup(
  group: ToolGroup,
  tools: string[],
  availableBridgeTools: ReadonlySet<string> | undefined,
): ToolGroupCapability {
  // M20 Plan 7 / T20.7.0 — auto-activation metadata is the same across all
  // three availability branches, so compute it once.
  const autoActivate = group.autoActivate === true && !!group.unityPackage;
  const packageDependency = autoActivate ? group.unityPackage! : null;

  // No domainDefine → the group is always compiled in (core, gate-and-verify,
  // typed-editor, etc.).
  if (!group.domainDefine) {
    return {
      id: group.id,
      description: group.description,
      defaultEnabled: group.defaultEnabled,
      toolCount: tools.length,
      tools,
      available: true,
      availableReason: null,
      unityPackage: null,
      domainDefine: null,
      autoActivate: false,
      packageDependency: null,
      usageHint: buildUsageHint(group),
    };
  }

  // Domain-gated group. The bridge signals whether the domain compiled in by
  // exposing (or not) the group's tool names in its compiled-tool set. When
  // availableBridgeTools is undefined (bridge offline on a local capability
  // call), availability is unknown — the agent is directed at manage_tools
  // (which probes the live bridge) for the authoritative answer.
  if (availableBridgeTools === undefined) {
    return {
      id: group.id,
      description: group.description,
      defaultEnabled: group.defaultEnabled,
      toolCount: tools.length,
      tools,
      available: null,
      availableReason:
        "Bridge offline — compiled-state availability unknown. Call " +
        "manage_tools(action=\"list_groups\") when the bridge is up, or " +
        "install the Unity package to make the group compile in.",
      unityPackage: group.unityPackage ?? null,
      domainDefine: group.domainDefine,
      autoActivate,
      packageDependency,
      usageHint: buildUsageHint(group),
    };
  }

  // Bridge reachable — infer availability from whether any of the group's
  // compiled-in tool names appear in the bridge tool set. A single tool
  // present is enough; we don't need every tool (the bridge may have
  // disabled one via toggle policy).
  const anyToolCompiledIn =
    tools.length === 0 ? false : tools.some((t) => availableBridgeTools.has(t));
  return {
    id: group.id,
    description: group.description,
    defaultEnabled: group.defaultEnabled,
    toolCount: tools.length,
    tools,
    available: anyToolCompiledIn,
    // specs/feedback.md 2026-07-03 — when the bridge is reachable but the
    // group's tools are absent, EITHER the Unity package is not installed OR
    // the installed bridge binary was not built with the domain extension
    // pack. The reason must mention both so an operator chasing
    // "navigation shows available:false even though the package is in
    // manifest.json" lands on the bridge-build fix, not a false "install the
    // package" loop.
    availableReason: anyToolCompiledIn
      ? null
      : `Group tools are not compiled into the running bridge. Either the ` +
        `Unity package '${group.unityPackage}' is not installed, OR the ` +
        `installed bridge binary was built without the ${group.domainDefine} ` +
        `domain extension pack. Install the package, or rebuild/install the ` +
        `bridge with the extension pack included.`,
    unityPackage: group.unityPackage ?? null,
    domainDefine: group.domainDefine,
    autoActivate,
    packageDependency,
    usageHint: buildUsageHint(group),
  };
}

function buildUsageHint(group: ToolGroup): string {
  if (group.defaultEnabled) {
    return (
      "Default-on group. Its tools are visible in a fresh session without " +
      "calling manage_tools."
    );
  }
  // M20 Plan 7 / T20.7.0 — auto-activating groups surface their tools when
  // the package is installed without a manual manage_tools call.
  if (group.autoActivate && group.unityPackage) {
    return (
      `Auto-activates when Unity package '${group.unityPackage}' is ` +
      "installed — its tools appear in a fresh session's ListTools without " +
      "calling unity_open_mcp_manage_tools. Use " +
      "unity_open_mcp_manage_tools(action=\"deactivate\", group=\"" +
      `${group.id}\`) to hide them, or action=\"activate\" to re-enable ` +
      "after a manual deactivate."
    );
  }
  return (
    `Call unity_open_mcp_manage_tools(action=\"activate\", group=\"${group.id}\") ` +
    "before invoking this group's tools — fresh sessions start with only " +
    "`core` enabled."
  );
}
