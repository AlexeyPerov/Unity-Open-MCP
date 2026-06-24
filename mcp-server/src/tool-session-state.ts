// M18 Plan 2 / T18.2 — per-session tool-group visibility state.
//
// Pure in-memory store: ephemeral, per connected MCP client/session. Matches
// the resolved decision in M18 execution-plan.md — the MCP server is the
// authority for session visibility; the bridge does NOT track session state.
// The state resets to `core`-only on every MCP-server restart and is shared
// across one stdio server process (one connected MCP client ↔ one store).
//
// `unity_open_mcp_manage_tools` is the only mutator of this state. ListTools
// reads it via `filterVisibleTools` to drop tools whose group is not active.
//
// The store is intentionally not keyed by session id — the stdio MCP server
// has exactly one client per process. HTTP/SSE MCP transports would need a
// per-client map; that is a Phase-2 concern (see M18 spec §Out of scope).

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import {
  DEFAULT_ENABLED_GROUPS,
  GROUP_IDS,
  getGroup,
  groupFor,
} from "./capabilities/tool-groups.js";

/**
 * Names of always-visible tools (meta-tools with no group assignment). These
 * are never filtered by the session state — an agent can always reach them.
 */
const ALWAYS_VISIBLE_TOOLS: ReadonlySet<string> = new Set([
  "unity_open_mcp_capabilities",
  "unity_open_mcp_list_rules",
  "unity_open_mcp_generate_skill",
  "unity_open_mcp_manage_tools",
  "unity_open_mcp_pull_events",
  "unity_senses_pull_events",
  "unity_open_mcp_read_compile_errors",
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

  /** Snapshot of currently-active group ids. */
  activeGroups(): string[] {
    return Array.from(this.active).sort();
  }

  /** True when the group is in the active set. */
  isGroupActive(groupId: string): boolean {
    return this.active.has(groupId);
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
    return true;
  }

  /** Restore the default active set (see {@link DEFAULT_ENABLED_GROUPS}). Always returns true. */
  reset(): boolean {
    this.active = new Set(DEFAULT_ENABLED_GROUPS);
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
