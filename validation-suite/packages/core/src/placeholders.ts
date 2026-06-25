/**
 * Placeholder expansion for scenario params (phase-2 task 7).
 *
 * The Unity profile declares two placeholders (unity.md → Path
 * conventions):
 *  - `{fixtureRoot}` → the test's fixture directory under the project:
 *    `Assets/_ValidationSuite/<test-id>/`.
 *  - `{projectRoot}` → the selected project root.
 *
 * Expansion is pure + total: given a `ProjectPaths` (resolved, absolute)
 * context, a placeholder token is replaced everywhere it occurs in a
 * string. Unknown placeholders are left untouched so a future profile
 * can introduce its own without the core needing an update. This keeps
 * the loader/runner engine-neutral (idea.md → Multi-engine reuse).
 *
 * The runtime (Tauri/Rust) resolves absolute paths; the core only does
 * the string substitution on already-resolved context, so there is no
 * fs access here.
 */

import type { PlaceholderToken } from "./types.ts";

/**
 * Resolved absolute paths the runner passes to the expander. Built by
 * the backend from the active engine profile + selected project.
 */
export interface ExpandContext {
  /** Selected project root (absolute). */
  projectRoot: string;
  /** Resolved fixture root for the current test (absolute). */
  fixtureRoot: string;
}

/** A substitution pair: token → its resolved value. */
export interface PlaceholderBinding {
  token: PlaceholderToken;
  value: string;
}

/**
 * Build the binding list for a context. Order matters only for display;
 * substitution replaces exact tokens, so longer tokens never collide.
 */
export function bindingsFor(ctx: ExpandContext): PlaceholderBinding[] {
  return [
    { token: "{fixtureRoot}", value: ctx.fixtureRoot },
    { token: "{projectRoot}", value: ctx.projectRoot },
  ];
}

/**
 * Expand all known placeholders in a single string. Unknown tokens are
 * left as-is (forward-compatible with profile-specific placeholders).
 */
export function expandString(input: string, ctx: ExpandContext): string {
  let out = input;
  for (const { token, value } of bindingsFor(ctx)) {
    // global replace; value has no special pattern chars we care about.
    out = out.split(token).join(value);
  }
  return out;
}

/**
 * Recursively expand placeholders inside an arbitrary JSON value
 * (objects, arrays, strings). Non-string leaves are returned unchanged.
 * Used to expand `{fixtureRoot}` inside a full action or payload object.
 */
export function expandValue<T>(value: T, ctx: ExpandContext): T {
  if (typeof value === "string") return expandString(value, ctx) as unknown as T;
  if (Array.isArray(value)) return value.map((v) => expandValue(v, ctx)) as unknown as T;
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) out[k] = expandValue(v, ctx);
    return out as unknown as T;
  }
  return value;
}

/**
 * Does `input` still contain any unresolved placeholder token? The
 * runner uses this to warn when a scenario references a placeholder the
 * active profile did not resolve (e.g. `{fixtureRoot}` left in place
 * because the fixture root wasn't computed yet).
 */
export function hasUnresolvedPlaceholder(input: string): boolean {
  return /\{(fixtureRoot|projectRoot)\}/.test(input);
}
