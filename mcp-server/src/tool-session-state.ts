// Per-session tool-group visibility state.
//
// Pure in-memory store: ephemeral, per connected MCP client/session. The MCP
// server is the authority for session visibility; the bridge does NOT track
// session state. Every MCP-server restart restores the catalog's default-on
// groups. One stdio server process has one connected client and one store.
//
// `unity_open_mcp_manage_tools` is the only mutator of the tool-group state.
// ListTools reads it via `filterVisibleTools` to drop tools whose group is not
// active.
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
//
// M31 Plan 3 / T31.4 — the store ALSO carries the session-scoped fd-sample
// ring for `resource_pressure`. The samples live here (not on disk, per the
// offline-read no-cache philosophy) and are cleared on `reset()` / server
// restart. See `process-diagnostics.ts` for the probe + trend math.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import {
  DEFAULT_ENABLED_GROUPS,
  GROUP_IDS,
  AUTO_ACTIVATE_GROUPS,
  getGroup,
  groupFor,
} from "./capabilities/tool-groups.js";
// M31 Plan 3 / T31.4 — fd-sample type shared with process-diagnostics.ts.
// type-only import keeps the module dependency graph clean (no runtime cycle:
// process-diagnostics does not import tool-session-state).
import type { FdSample } from "./process-diagnostics.js";

/**
 * M31 Plan 3 / T31.4 — capacity of the session-scoped fd-sample ring. Enough
 * samples to detect a monotonic climb across several domain reloads (the leak
 * signature) without unbounded growth. The trend math in
 * `process-diagnostics.ts` only needs ≥3 same-PID known-count samples to flag
 * `leaking`, so 20 is comfortably above the minimum and keeps the response
 * payload small.
 */
export const FD_SAMPLE_RING_CAPACITY = 20;

/**
 * Why a group is active in the current session.
 * - `"default"`  — default-on group (in {@link DEFAULT_ENABLED_GROUPS}).
 * - `"manual"`   — activated via `unity_open_mcp_manage_tools(action=activate)`.
 * - `"auto"`     — M20 Plan 7 / T20.7.0 auto-activated because the group's
 *                  Unity package dependency is detected as installed.
 * - `"suppressed"` — M8 (round-2 review): the group was explicitly deactivated
 *                  by the operator and must NOT be resurrected by a later
 *                  {@link reconcileAutoActivation} pass. A suppressed group is
 *                  NOT active (it is absent from the active set); the source
 *                  entry is retained solely so reconciliation can see the
 *                  deactivation intent. Clearing it requires `activate` /
 *                  `activateAuto` (which overwrite the source) or `reset`.
 */
export type ActivationSource = "default" | "manual" | "auto" | "suppressed";

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
  // M31 Plan 3 — operator-only recovery + prediction surfaces for the
  // Editor fd-exhaustion failure mode. Sibling to bridge_status: they act on
  // the OS process, not project assets, and must survive any group teardown.
  "unity_open_mcp_restart_editor",
  "unity_open_mcp_resource_pressure",
]);

/**
 * Per-session tool-group visibility store.
 *
 * Lifecycle:
 *  - Constructed once per stdio server process (one connected MCP client).
 *  - Initial active set is {@link DEFAULT_ENABLED_GROUPS} — the groups
 *    marked `defaultEnabled: true` in the canonical tool-group catalog
 *    (see `capabilities/tool-groups.ts`). The lean baseline is `core` (the
 *    essential entry points) plus `gate-and-verify` (the safety surface);
 *    every other group activates on demand or auto-activates when its Unity
 *    package is present. The catalog is the single source of truth.
 *  - Mutated only by {@link activate} / {@link deactivate} / {@link reset}
 *    (called from the manage_tools router).
 *  - Read by {@link isGroupActive} (manage_tools list_groups) and
 *    {@link filterVisibleTools} (ListTools handler).
 */
export class ToolSessionState {
  private active = new Set<string>(DEFAULT_ENABLED_GROUPS);
  /**
   * Per-group source tracking. Active groups carry `"default"` / `"manual"` /
   * `"auto"` (see {@link ActivationSource}). A group that was auto-activated
   * and then manually re-activated flips to `"manual"` (manual intent wins).
   *
   * M8 (round-2 review): a group the operator explicitly deactivated is
   * recorded here as `"suppressed"` instead of being deleted, so a subsequent
   * {@link reconcileAutoActivation} pass can see the deactivation intent and
   * refuse to resurrect it. A suppressed group is NOT in the active set. An
   * entry is absent from the map only when the group has never been touched
   * this session (initial state for opt-in groups) — for those,
   * {@link activationSource} returns `null`.
   */
  private source = new Map<string, ActivationSource>();

  /**
   * M31 Plan 3 / T31.4 — session-scoped ring of recent fd samples for the
   * `resource_pressure` trend signal. In-memory only (no disk cache, per the
   * offline-read no-cache philosophy); LRU-evicted at capacity; cleared on
   * `reset()` and on server restart. Kept on the session store because it is
   * per-client ephemeral state, exactly like the tool-group active set.
   */
  private fdSamples: FdSample[] = [];

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

  /**
   * Why the group is in its current state, or `null` when the group has never
   * been touched this session (initial opt-in state). Active groups report
   * `"default"` / `"manual"` / `"auto"`. A group the operator explicitly
   * deactivated reports `"suppressed"` (M8) — it is NOT active, but the
   * intent is retained so {@link reconcileAutoActivation} does not resurrect
   * it on the next meta-tool call. Callers that only care about "is it
   * active" should use {@link isGroupActive}; this method exposes the WHY.
   */
  activationSource(groupId: string): ActivationSource | null {
    return this.source.get(groupId) ?? null;
  }

  /**
   * Activate a group. Returns true if state changed (group was not active).
   * Unknown groups are rejected with `false` — callers should validate via
   * {@link GROUP_IDS} first and surface a structured error.
   *
   * M8 — activating a previously-suppressed group clears the suppression
   * (the source is overwritten to `"manual"`), so a later reconcile pass can
   * auto-deactivate/reactivate it normally again.
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
   *
   * M8 — note this method is the DIRECT auto-activation entry (called when
   * the package is first detected). It does NOT clear a `"suppressed"` record:
   * {@link reconcileAutoActivation} is the path that respects suppression, and
   * it refuses to re-add a suppressed group. A direct `activateAuto` call on a
   * suppressed group is treated as a fresh auto-activation (the operator did
   * not call `manage_tools(activate)`, so the suppression was a transient
   * state). This preserves the documented "Idempotent" contract while keeping
   * suppression meaningful on the reconcile path every meta-tool call drives.
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
   *
   * M8 (round-2 review) — the deactivation is recorded as `"suppressed"` in
   * the source map (not deleted), so a subsequent {@link reconcileAutoActivation}
   * pass sees the operator's intent and does NOT resurrect an auto-activate
   * group whose package is still installed. Previously `deactivate` deleted
   * the source entry, leaving no record; the next meta-tool call's reconcile
   * then re-added the group as `"auto"` and fired a spurious
   * `tools/list_changed`, contradicting the documented contract that
   * "deactivate to hide them" is sticky for the session.
   */
  deactivate(groupId: string): boolean {
    if (!GROUP_IDS.has(groupId)) return false;
    if (!this.active.has(groupId)) return false;
    this.active.delete(groupId);
    this.source.set(groupId, "suppressed");
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
      if (satisfied && !active && src !== "suppressed") {
        // M8 — a group the operator explicitly deactivated is recorded as
        // "suppressed"; do NOT resurrect it just because its package is still
        // installed. The deactivation is sticky for the session (cleared only
        // by `activate` / `activateAuto` overwriting the source, or by reset).
        // Without this guard the next meta-tool call's reconcile re-added the
        // group as "auto" and fired a spurious tools/list_changed.
        this.active.add(groupId);
        this.source.set(groupId, "auto");
        changed.push(groupId);
      } else if (!satisfied && active && src === "auto") {
        // Package removed and the group was only auto-activated (not manually
        // re-activated) → drop it so the "install X" UX surfaces again.
        // Note: a suppressed group is already inactive, so it falls through
        // both branches harmlessly when its package later disappears.
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
    // M31 Plan 3 / T31.4 — clear the fd-sample ring too. The trend signal is
    // session-scoped; a reset means "start over" and stale samples from a
    // prior workflow would mislead the trend detector.
    this.fdSamples = [];
    return true;
  }

  // -------------------------------------------------------------------------
  // M31 Plan 3 / T31.4 — session-scoped fd samples for resource_pressure.
  // -------------------------------------------------------------------------

  /**
   * Record one fd sample at the tail of the ring. Capacity-bounded (LRU on
   * insertion). A `null` count is recorded too — the trend detector needs to
   * see probe-failure gaps so it does not falsely interpolate across them.
   */
  recordFdSample(sample: FdSample): void {
    this.fdSamples.push(sample);
    // M31 Plan 6 / T6.1 — single O(n) bulk drop instead of a shift() per excess
    // element. Same shape as the BridgeEventStream overflow fix; capacity is
    // small (FD_SAMPLE_RING_CAPACITY = 20) so this is a consistency cleanup,
    // not a perf-critical path.
    const excess = this.fdSamples.length - FD_SAMPLE_RING_CAPACITY;
    if (excess > 0) this.fdSamples.splice(0, excess);
  }

  /** Snapshot of the recorded fd samples (oldest-first). */
  fdSamplesSnapshot(): readonly FdSample[] {
    return this.fdSamples.slice();
  }

  /** Drop all recorded fd samples without touching the tool-group state. */
  clearFdSamples(): void {
    this.fdSamples = [];
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
