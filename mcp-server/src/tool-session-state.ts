// Per-session tool-group visibility state.
//
// Pure in-memory store: ephemeral, per connected MCP client/session. The MCP
// server is the authority for session visibility; the bridge does NOT track
// session state. Every MCP-server restart restores the catalog's default-on
// groups. One stdio server process has one connected client and one store.
//
// `unity_open_mcp_manage_tools` is the only mutator of this state. ListTools
// reads it via `filterVisibleTools` to drop tools whose group is not active.
//
// The store is intentionally not keyed by session id — the stdio MCP server
// has exactly one client per process. HTTP/SSE MCP transports would need a
// per-client map.
//
// In addition to the manual activation path, the store records why each active
// group is active (manual vs auto),
// so capabilities / manage_tools can surface `autoActivated: true` with the
// driving package dependency. Auto-activation is driven from the live bridge's
// compiled-tool inventory: when a group with `autoActivate: true` has its
// tools compiled in (i.e. its Unity package is present), the router calls
// `activateAuto`. The store itself never probes packages — it only records
// the outcome. Auto-activation is ephemeral and idempotent within a session.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import {
  DEFAULT_ENABLED_GROUPS,
  GROUP_IDS,
  AUTO_ACTIVATE_GROUPS,
  getGroup,
  groupFor,
} from "./capabilities/tool-groups.js";

/**
 * Why a group is active in the current session.
 * - `"default"`  — default-on group (in {@link DEFAULT_ENABLED_GROUPS}).
 * - `"manual"`   — activated via `unity_open_mcp_manage_tools(action=activate)`.
 * - `"auto"`     — M20 Plan 7 / T20.7.0 auto-activated because the group's
 *                  Unity package dependency is detected as installed.
 */
export type ActivationSource = "default" | "manual" | "auto";

/**
 * Names of always-visible tools (meta-tools with no group assignment). These
 * are never filtered by the session state — an agent can always reach them.
 *
 * `unity_open_mcp_ping` is included (T6.3): it is the precise connectivity
 * health check (vs `bridge_status`, which is the coarse operator snapshot).
 * A health probe must survive `manage_tools(deactivate, core)` — an agent that
 * just tore down the core group still needs to re-probe the bridge before
 * re-activating. ping is also assigned to the `core` group in
 * `capabilities/tool-groups.ts`; the always-visible check runs first in
 * {@link filterVisibleTools}, so the group assignment is a fallback that never
 * applies.
 */
const ALWAYS_VISIBLE_TOOLS: ReadonlySet<string> = new Set([
  "unity_open_mcp_capabilities",
  "unity_open_mcp_list_rules",
  "unity_open_mcp_generate_skill",
  "unity_open_mcp_manage_tools",
  "unity_open_mcp_ping",
  "unity_open_mcp_pull_events",
  "unity_senses_pull_events",
  "unity_open_mcp_read_compile_errors",
  "unity_open_mcp_bridge_status",
]);

/**
 * Per-session tool-group visibility store.
 *
 * Lifecycle:
 *  - Constructed once per stdio server process (one connected MCP client).
 *  - Initial active set is {@link DEFAULT_ENABLED_GROUPS} — the groups
 *    marked `defaultEnabled: true` in the canonical tool-group catalog
 *    (see `capabilities/tool-groups.ts`). Today that is `core` plus the
 *    always-useful `gate-and-verify` / `asset-intelligence` / `typed-editor`
 *    / `diagnostics` groups; the catalog is the single source of truth.
 *  - Mutated only by {@link activate} / {@link deactivate} / {@link reset}
 *    (called from the manage_tools router).
 *  - Read by {@link isGroupActive} (manage_tools list_groups) and
 *    {@link filterVisibleTools} (ListTools handler).
 */
export class ToolSessionState {
  private active = new Set<string>(DEFAULT_ENABLED_GROUPS);
  /**
   * Per-active-group source tracking. Default-on groups map to `"default"`;
   * manually-activated groups map to `"manual"`; auto-activated groups map to
   * `"auto"`. A group that was auto-activated and then manually re-activated
   * flips to `"manual"` (manual intent wins). Absent from the map ⇒ the group
   * is not active.
   */
  private source = new Map<string, ActivationSource>();

  constructor() {
    for (const id of DEFAULT_ENABLED_GROUPS) this.source.set(id, "default");
  }

  /** Snapshot of currently-active group ids. */
  activeGroups(): string[] {
    return Array.from(this.active).sort();
  }

  /** True when the group is in the active set. */
  isGroupActive(groupId: string): boolean {
    return this.active.has(groupId);
  }

  /** Why the group is active, or `null` when it is not active. */
  activationSource(groupId: string): ActivationSource | null {
    return this.source.get(groupId) ?? null;
  }

  /**
   * Activate a group. Returns true if state changed (group was not active).
   * Unknown groups are rejected with `false` — callers should validate via
   * {@link GROUP_IDS} first and surface a structured error.
   */
  activate(groupId: string): boolean {
    if (!GROUP_IDS.has(groupId)) return false;
    if (this.active.has(groupId)) return false;
    this.active.add(groupId);
    this.source.set(groupId, "manual");
    return true;
  }

  /**
   * M20 Plan 7 / T20.7.0 — auto-activate a group because its Unity package
   * dependency is detected as installed. Idempotent: a no-op when the group
   * is already active. Manual activation wins: a group that was manually
   * activated or deactivated is NOT silently flipped back to `"auto"`.
   * Returns true if state changed (group was not active).
   */
  activateAuto(groupId: string): boolean {
    if (!GROUP_IDS.has(groupId)) return false;
    if (this.active.has(groupId)) return false;
    this.active.add(groupId);
    this.source.set(groupId, "auto");
    return true;
  }

  /**
   * Deactivate a group. Returns true if state changed (group was active).
   * Unknown groups are rejected with `false`. Deactivating the `core` group
   * is allowed — the meta-tools (capabilities, manage_tools) stay reachable
   * via {@link ALWAYS_VISIBLE_TOOLS}, but the rest of the core surface goes
   * dark until the session re-activates it.
   */
  deactivate(groupId: string): boolean {
    if (!GROUP_IDS.has(groupId)) return false;
    if (!this.active.has(groupId)) return false;
    this.active.delete(groupId);
    this.source.delete(groupId);
    return true;
  }

  /**
   * M20 Plan 7 / T20.7.0 — reconcile the auto-activated set against the
   * currently-satisfied package dependencies. Groups that auto-activate and
   * whose package is present are activated (if not already); auto-activated
   * groups whose package is no longer present are dropped (only when they
   * were auto-activated — a manual activation is preserved). Returns the
   * list of group ids whose active state changed (added or removed), so the
   * router can fire the listChanged notification exactly once.
   */
  reconcileAutoActivation(satisfiedGroupIds: ReadonlySet<string>): string[] {
    const changed: string[] = [];
    for (const entry of AUTO_ACTIVATE_GROUPS) {
      const { groupId } = entry;
      const satisfied = satisfiedGroupIds.has(groupId);
      const active = this.active.has(groupId);
      const src = this.source.get(groupId);
      if (satisfied && !active) {
        this.active.add(groupId);
        this.source.set(groupId, "auto");
        changed.push(groupId);
      } else if (!satisfied && active && src === "auto") {
        // Package removed and the group was only auto-activated (not manually
        // re-activated) → drop it so the "install X" UX surfaces again.
        this.active.delete(groupId);
        this.source.delete(groupId);
        changed.push(groupId);
      }
    }
    return changed;
  }

  /** Restore the default active set (see {@link DEFAULT_ENABLED_GROUPS}). Always returns true. */
  reset(): boolean {
    this.active = new Set(DEFAULT_ENABLED_GROUPS);
    this.source = new Map();
    for (const id of DEFAULT_ENABLED_GROUPS) this.source.set(id, "default");
    return true;
  }
}

/**
 * Filter a tool list to the tools visible in the current session.
 *
 * Visibility rules (precedence high → low):
 *  1. The tool name is in {@link ALWAYS_VISIBLE_TOOLS} → always visible.
 *  2. The tool has no group assignment (`groupFor` returns null) → always
 *     visible (defensive — matches the catalog intent for meta-tools).
 *  3. The tool's group is in the session's active set → visible.
 *  4. Otherwise → hidden.
 *
 * `getGroup` is plumbed in so tests can swap the resolver; production callers
 * omit it and get the default catalog resolver.
 */
export function filterVisibleTools(
  tools: Tool[],
  state: ToolSessionState,
  resolveGroup: (toolName: string) => string | null = groupFor,
): Tool[] {
  return tools.filter((tool) => {
    if (ALWAYS_VISIBLE_TOOLS.has(tool.name)) return true;
    const group = resolveGroup(tool.name);
    if (group === null) return true;
    return state.isGroupActive(group);
  });
}

// Re-exported so the manage_tools router and ListTools handler share one
// import surface. Keep the catalog definitions out of the public name — they
// are owned by tool-groups.ts.
export { getGroup, groupFor };
