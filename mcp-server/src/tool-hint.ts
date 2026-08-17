// Centralized "how to name a tool inside an agent-facing hint" helper.
//
// specs/feedback.md 2026-08-14 (hint regression) — a runtime hint that names a
// tool the caller cannot see is worse than no hint at all: the agent follows
// it, the tool is not in its surface (`ToolSearch` answers "no matching
// deferred tools found"), and it falls back to hand-rolling the behavior the
// tool exists to replace. This had already been fixed once by appending the
// `manage_tools` activation call at three hand-maintained call sites; the
// strings drifted apart again on the next release.
//
// The structural fix is this module: ONE helper that looks the tool's group up
// from the registry (`capabilities/tool-groups.ts`) and appends the activation
// call automatically when — and only when — that group is not enabled by
// default. No call site can forget it, and a future group move (or a group
// flipping to `defaultEnabled`) fixes every hint at once.
//
// `tool-hint.test.ts` pins the invariant at the string level: every emitted
// hint / tool description that names a non-default-group tool must also carry
// the `manage_tools` activation call.

import {
  DEFAULT_ENABLED_GROUPS,
  groupFor,
} from "./capabilities/tool-groups.js";

// ---------------------------------------------------------------------------
// feedback 2026-08-17 — a hint must not name a tool that is not registered.
// The reported case: the remediation hint pointed at
// unity_open_mcp_recompile_scripts (in the typed-editor group), the agent
// activated the group, and the tool was still absent from its surface — the
// hint sent it down a dead end. `tools/index.ts` registers the full
// ALL_TOOLS name list here once at module init (a push, so this module never
// imports the tool graph and no import cycle exists). Until that
// registration has run — i.e. during module-eval-time calls from tool
// definition files — the registry is empty and the guard is a no-op; the
// static companion test in tool-hint.test.ts pins the invariant at source
// level instead.
// ---------------------------------------------------------------------------

/** Names of every registered MCP tool. Empty until tools/index.ts registers. */
const registeredTools = new Set<string>();

/** Warn-once bookkeeping so an unregistered name logs a single line. */
const warnedUnregistered = new Set<string>();

/**
 * Register the reachable tool-name set. Called once from `tools/index.ts`
 * after `ALL_TOOLS` is built. Not part of the public agent surface.
 */
export function registerHintableToolNames(names: readonly string[]): void {
  for (const name of names) registeredTools.add(name);
}

/**
 * Build the `manage_tools` activation call for a group id. Mirrors the exact
 * invocation an agent must issue, so a hint can be followed verbatim.
 */
export function activateInstruction(groupId: string): string {
  return `activate with manage_tools(action:"activate", group:"${groupId}")`;
}

/**
 * True when `toolName` is reachable in a fresh session without a
 * `manage_tools` activation — i.e. it has no group assignment (always-visible
 * meta-tool) or its group is default-enabled.
 */
export function isDefaultVisible(toolName: string): boolean {
  const group = groupFor(toolName);
  if (group === null) return true;
  return DEFAULT_ENABLED_GROUPS.has(group);
}

/**
 * Render a tool reference for use inside an agent-facing hint or tool
 * description.
 *
 * Default-visible tools render as the bare name. A tool in a group that is NOT
 * enabled by default renders as the name plus the group and the concrete
 * activation call, e.g.
 *
 *   unity_open_mcp_recompile_scripts (in the typed-editor group, not enabled
 *   by default — activate with manage_tools(action:"activate",
 *   group:"typed-editor"))
 *
 * Unknown tool names render bare: this helper never invents an activation call
 * for a tool it cannot resolve. A name that is known to the group catalog but
 * NOT registered (verified against the names registered by tools/index.ts,
 * once available) also renders bare — a hint must not prescribe an activation
 * call for a tool the server cannot actually serve — and logs one maintainer
 * warning so the drift is visible.
 */
export function toolHintReference(toolName: string): string {
  const group = groupFor(toolName);
  const unregistered =
    registeredTools.size > 0 && !registeredTools.has(toolName);
  if (unregistered && !warnedUnregistered.has(toolName)) {
    warnedUnregistered.add(toolName);
    console.error(
      `[unity-open-mcp] tool-hint drift: '${toolName}' is assigned to group ` +
        `${group ?? "(none)"} but is not a registered tool — rendering the hint ` +
        `without its activation call. Check tools/index.ts.`,
    );
  }
  if (group === null || DEFAULT_ENABLED_GROUPS.has(group) || unregistered) {
    return toolName;
  }
  return (
    `${toolName} (in the ${group} group, not enabled by default — ` +
    `${activateInstruction(group)})`
  );
}
