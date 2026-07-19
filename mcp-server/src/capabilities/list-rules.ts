// Rule-listing builder for `unity_open_mcp_list_rules`.
//
// Distinct from `unity_open_mcp_capabilities` (kind: "rules") in two ways:
//   1. The response is purpose-built for "what rule should I run on this
//      asset?" — every rule carries a derived `defaultSeverity` (the worst
//      severity it can emit) and a flat `availableFixIds` list, so an agent
//      can scan the list in one pass without walking issue arrays.
//   2. It supports an `asset_kind` / `extension` filter so the caller can ask
//      "which rules apply to .prefab?" without post-processing the catalog.
//
// Pure transformation over the injected catalog — no runtime cross-file
// imports, loads cleanly under `node --experimental-strip-types`.

import type {
  RuleCapability,
  FixCapability,
  RuleIssueDescriptor,
} from "./rule-catalog.js";
import { FIX_CATALOG, getFixIndex, buildFixIndex } from "./rule-catalog.js";

export type Severity = "Error" | "Warning";

export interface ListRulesEntry {
  id: string;
  title: string;
  description: string;
  applicableAssetKinds: string[];
  applicableExtensions?: string[];
  implemented: boolean;
  status: "implemented" | "planned";
  /** Worst severity the rule can emit. `Warning` when the rule emits no errors. */
  defaultSeverity: Severity;
  /** Distinct fix IDs across all the rule's issue codes. */
  availableFixIds: string[];
  /** Issue codes the rule emits. Empty for planned rules. */
  issues: RuleIssueDescriptor[];
  /** Present only for planned rules. */
  guidance?: string;
}

export interface ListRulesResult {
  rules: ListRulesEntry[];
  counts: {
    implemented: number;
    planned: number;
    total: number;
  };
  filters?: {
    assetKind?: string;
    extension?: string;
    implementedOnly?: boolean;
  };
}

export interface ListRulesFilter {
  /** Filter to rules that declare this asset kind (e.g. "prefab", "scene"). */
  assetKind?: string;
  /** Filter to rules that declare this extension (e.g. ".prefab"). */
  extension?: string;
  /** When true, omit planned/unimplemented rules. */
  implementedOnly?: boolean;
}

export interface ListRulesDeps {
  rules: RuleCapability[];
  fixes: FixCapability[];
}

// M31-optimizations Plan 4 / T4.2 (M2) — `buildFixIndex` moved to
// rule-catalog.ts as a reusable helper + a lazy singleton over FIX_CATALOG.
// `listRules` reuses the singleton when the caller passed FIX_CATALOG (the
// production path); tests that pass a different fix list fall through to a
// fresh build via `buildFixIndex(deps.fixes)`.
function resolveFixIndex(fixes: FixCapability[]): Map<string, string[]> {
  return fixes === FIX_CATALOG ? getFixIndex() : buildFixIndex(fixes);
}

function deriveDefaultSeverity(issues: RuleIssueDescriptor[]): Severity {
  // "Default" severity is the level the gate treats as a failure by default.
  // We surface the *worst* severity the rule can emit so agents reading the
  // list understand the ceiling: a rule that ever emits Error will fail the
  // gate under the default `error` threshold.
  if (issues.some((i) => i.severity === "Error")) return "Error";
  if (issues.some((i) => i.severity === "Warning")) return "Warning";
  return "Warning";
}

function collectFixIds(
  rule: RuleCapability,
  fixIndex: Map<string, string[]>,
): string[] {
  if (!rule.implemented) return [];
  const ids = new Set<string>();
  for (const issue of rule.issues) {
    // Prefer the catalog's per-issue fixIds (source of truth) and fall back to
    // the fix-index lookup so the two never disagree silently.
    for (const id of issue.fixIds) ids.add(id);
    const fromIndex = fixIndex.get(issue.code);
    if (fromIndex) for (const id of fromIndex) ids.add(id);
  }
  return [...ids];
}

function ruleMatches(
  rule: RuleCapability,
  filter: ListRulesFilter,
): boolean {
  if (filter.assetKind) {
    const kinds = rule.applicableAssetKinds ?? [];
    if (!kinds.includes(filter.assetKind)) return false;
  }
  if (filter.extension) {
    const exts = rule.applicableExtensions ?? [];
    const normalized = filter.extension.startsWith(".")
      ? filter.extension.toLowerCase()
      : `.${filter.extension.toLowerCase()}`;
    if (!exts.map((e) => e.toLowerCase()).includes(normalized)) return false;
  }
  return true;
}

export function listRules(
  deps: ListRulesDeps,
  filter: ListRulesFilter = {},
): ListRulesResult {
  // M31-optimizations Plan 4 / T4.2 — reuse the FIX_CATALOG fix-index singleton
  // on the production path (routeListRules always passes FIX_CATALOG). Tests
  // that pass a different fix list fall through to a fresh build.
  const fixIndex = resolveFixIndex(deps.fixes);

  const entries: ListRulesEntry[] = [];
  for (const rule of deps.rules) {
    if (filter.implementedOnly && !rule.implemented) continue;
    if (!ruleMatches(rule, filter)) continue;

    entries.push({
      id: rule.id,
      title: rule.title,
      description: rule.description,
      applicableAssetKinds: rule.applicableAssetKinds,
      applicableExtensions: rule.applicableExtensions,
      implemented: rule.implemented,
      status: rule.status,
      defaultSeverity: deriveDefaultSeverity(rule.issues),
      availableFixIds: collectFixIds(rule, fixIndex),
      issues: rule.issues,
      guidance: rule.guidance,
    });
  }

  const implemented = entries.filter((e) => e.implemented).length;
  const planned = entries.filter((e) => !e.implemented).length;

  // Only surface the filter block when the caller actually passed one — keeps
  // the default response compact.
  const hasFilter =
    filter.assetKind !== undefined ||
    filter.extension !== undefined ||
    filter.implementedOnly === true;

  return {
    rules: entries,
    counts: {
      implemented,
      planned,
      total: entries.length,
    },
    filters: hasFilter
      ? {
          assetKind: filter.assetKind,
          extension: filter.extension,
          implementedOnly: filter.implementedOnly,
        }
      : undefined,
  };
}
