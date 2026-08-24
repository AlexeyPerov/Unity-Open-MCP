// Output-profile + paging view over a built capabilities result.
//
// specs/feedback.md 2026-08-24 — `capabilities` is the tool the server
// instructions tell an agent to "call first", but its full response is ~513 KB
// on one line (per-tool `inputSchema` alone is ~446 KB of that), which blows
// past a typical harness per-result ceiling and gets spilled to a file. The
// heavy read tools already have the M22 answer for this — a `profile` knob plus
// `page_size`/`cursor` — and this module applies the same contract to the
// discovery surface so "call this first" is affordable:
//
//   compact  (default) — no per-tool `inputSchema`, no per-tool `description`,
//                        no per-rule `description`, per-group `tools[]` folded
//                        to `toolCount`. ~105 KB, or ~20 KB with `kind:"tools"`
//                        + `page_size`.
//   balanced           — adds descriptions back; still no `inputSchema`.
//   full               — the unfolded shape (every field, including schemas).
//
// Paging applies to the `tools[]` array (the dominant cost); `rules`/`fixes`
// are small enough to return whole. The module is a pure transformation over an
// already-built result so `build-capabilities.ts` keeps its shape and its
// direct callers (generate_skill) are unaffected.

import {
  applyPaging,
  attachPagination,
  type OutputProfile,
  type PaginationBlock,
  type PagingInput,
} from "../output-profile.js";
import type {
  CapabilitiesResult,
  ToolCapability,
  ToolGroupCapability,
} from "./build-capabilities.js";
import type { RuleCapability } from "./rule-catalog.js";

/** Cursor tool key — a cursor minted here never pages another tool's list. */
export const CAPABILITIES_CURSOR_KEY = "capabilities";

/**
 * `tools[]` entry under `compact` — identity + routing metadata only. Mirrors
 * {@link ToolCapability} minus `description` and `inputSchema`. `guidance`
 * (planned tools only) is retained: it is short and is the whole point of a
 * planned entry.
 */
export type CompactToolCapability = Omit<
  ToolCapability,
  "description" | "inputSchema"
>;

/** `tools[]` entry under `balanced` — everything but the input schema. */
export type BalancedToolCapability = Omit<ToolCapability, "inputSchema">;

/**
 * `toolGroups[]` entry under `compact` — the per-group roster folded away
 * (`toolCount` keeps the size; each tool reports its own `group`) plus the two
 * prose fields dropped, consistent with dropping descriptions elsewhere.
 * `manage_tools(list_groups)` is the surface that carries the prose.
 */
export type CompactToolGroupCapability = Omit<
  ToolGroupCapability,
  "tools" | "description" | "usageHint"
>;

/** `rules[]` entry under `compact` — prose dropped, applicability kept. */
export type CompactRuleCapability = Omit<RuleCapability, "description">;

export interface CapabilitiesViewOptions extends PagingInput {
  /** Effective profile. Callers resolve the default (`compact` for the tool). */
  profile: OutputProfile;
}

/**
 * The folded + paged response body. Field names match
 * {@link CapabilitiesResult} so a caller that upgrades from `full` to
 * `compact` reads the same keys with thinner values; `profile`, `profileHint`
 * and `pagination` are the only additions.
 */
export interface CapabilitiesView {
  profile: OutputProfile;
  /** What this profile folded away, and how to get it back. */
  profileHint: string;
  pagination: PaginationBlock;
  [key: string]: unknown;
}

const HINTS: Record<OutputProfile, string> = {
  compact:
    "compact (default): all prose dropped (per-tool inputSchema + description, " +
    "per-rule description, per-group description + usageHint) and per-group " +
    "tools[] folded to toolCount — each tool still reports its own `group`, and " +
    "manage_tools(list_groups) carries the group prose. Re-call with " +
    "profile:\"balanced\" for descriptions, profile:\"full\" for inputSchema — " +
    "and narrow first with kind:\"tools\" + page_size to keep the response small.",
  balanced:
    "balanced: descriptions included, per-tool inputSchema still omitted. " +
    "Re-call with profile:\"full\" (ideally with page_size + cursor) for the " +
    "input schemas — they are ~85% of the unfolded payload.",
  full:
    "full: every field including per-tool inputSchema (~450 KB across the " +
    "whole catalog). Use page_size + cursor to walk it, or profile:" +
    "\"compact\"/\"balanced\" when you only need the roster.",
};

/**
 * Fold and page a built capabilities result.
 *
 * Ordering matters: the profile fold runs BEFORE paging so `page_size` counts
 * response entries, not pre-fold entries — a caller that asks for 40 tools gets
 * 40 tools in whatever shape the profile produced.
 */
export function viewCapabilities(
  result: CapabilitiesResult,
  options: CapabilitiesViewOptions,
): CapabilitiesView {
  const { profile } = options;

  const tools = foldTools(result.tools, profile);
  const { page, block } = applyPaging(tools, CAPABILITIES_CURSOR_KEY, options);

  const body = {
    ...result,
    tools: page,
    rules: foldRules(result.rules, profile),
    toolGroups: foldToolGroups(result.toolGroups, profile),
    profile,
    profileHint: HINTS[profile],
  };

  return attachPagination(body, block) as CapabilitiesView;
}

function foldTools(
  tools: readonly ToolCapability[],
  profile: OutputProfile,
): (ToolCapability | BalancedToolCapability | CompactToolCapability)[] {
  if (profile === "full") return tools.slice();
  return tools.map((tool) => {
    const { inputSchema: _schema, description, ...rest } = tool;
    void _schema;
    return profile === "balanced" ? { ...rest, description } : rest;
  });
}

function foldRules(
  rules: readonly RuleCapability[],
  profile: OutputProfile,
): (RuleCapability | CompactRuleCapability)[] {
  if (profile !== "compact") return rules.slice();
  return rules.map((rule) => {
    const { description: _description, ...rest } = rule;
    void _description;
    return rest;
  });
}

function foldToolGroups(
  groups: readonly ToolGroupCapability[],
  profile: OutputProfile,
): (ToolGroupCapability | CompactToolGroupCapability)[] {
  if (profile !== "compact") return groups.slice();
  return groups.map((group) => {
    // `toolCount` already carries the size, and every tool reports its own
    // `group`, so the roster is reconstructible from the tools list. The prose
    // (description + usageHint) is ~16 KB across the catalog and duplicated by
    // manage_tools(list_groups).
    const {
      tools: _tools,
      description: _description,
      usageHint: _usageHint,
      ...rest
    } = group;
    void _tools;
    void _description;
    void _usageHint;
    return rest;
  });
}
