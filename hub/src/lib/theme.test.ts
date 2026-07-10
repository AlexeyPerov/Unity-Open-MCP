/**
 * Node:test coverage for the pure theme helpers. resolveTheme for explicit
 * palettes is a pure function with no DOM / no `$state`; applyTheme /
 * activeTheme mutate module-level state + the document and are not unit-
 * tested here (they need a DOM).
 *
 * Run with: `npm test` (the plain test script picks this up).
 */
import test from "node:test";
import assert from "node:assert/strict";

import { resolveTheme } from "./theme.svelte.ts";

// ---------------------------------------------------------------------------
// resolveTheme — explicit palettes are pure
// ---------------------------------------------------------------------------

test('resolveTheme("dark") returns "dark"', () => {
  assert.equal(resolveTheme("dark"), "dark");
});

test('resolveTheme("light") returns "light"', () => {
  assert.equal(resolveTheme("light"), "light");
});

test('resolveTheme("system") with no window falls back to "dark"', () => {
  // In Node there is no window, so the system path returns the documented
  // headless fallback ("dark"). This pins the defensive branch.
  assert.equal(resolveTheme("system"), "dark");
});
