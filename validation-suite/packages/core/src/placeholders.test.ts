/**
 * Tests for placeholder expansion (phase-2 task 7) + the patch transform.
 * Run with: `npm run test:core`
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  expandString,
  expandValue,
  hasUnresolvedPlaceholder,
} from "./placeholders.ts";
import { applyPatches, splitLines } from "./patch.ts";

const CTX = { projectRoot: "/proj", fixtureRoot: "/proj/Assets/_ValidationSuite/m9-x" };

// ── placeholders ─────────────────────────────────────────────────────────────

test("expandString replaces {fixtureRoot} and {projectRoot}", () => {
  assert.equal(
    expandString("{fixtureRoot}/Player.prefab", CTX),
    "/proj/Assets/_ValidationSuite/m9-x/Player.prefab",
  );
  assert.equal(expandString("{projectRoot}/Assets", CTX), "/proj/Assets");
});

test("expandString leaves unknown tokens untouched", () => {
  assert.equal(expandString("{unknown}/{fixtureRoot}", CTX), "{unknown}/" + CTX.fixtureRoot);
});

test("expandValue recurses into objects and arrays", () => {
  const out = expandValue(
    { path: "{fixtureRoot}/a", items: ["{projectRoot}", 5, { nested: "{fixtureRoot}" }] },
    CTX,
  );
  assert.deepEqual(out, {
    path: "/proj/Assets/_ValidationSuite/m9-x/a",
    items: ["/proj", 5, { nested: "/proj/Assets/_ValidationSuite/m9-x" }],
  });
});

test("hasUnresolvedPlaceholder detects leftover tokens", () => {
  assert.equal(hasUnresolvedPlaceholder("{fixtureRoot}/x"), true);
  assert.equal(hasUnresolvedPlaceholder("/plain/path"), false);
});

// ── patch transform ──────────────────────────────────────────────────────────

test("splitLines keeps trailing newlines attached", () => {
  assert.deepEqual(splitLines("a\nb\n"), ["a\n", "b\n"]);
  assert.deepEqual(splitLines("a\nb"), ["a\n", "b"]);
  assert.deepEqual(splitLines(""), []);
});

test("applyPatches: replace_line_contains swaps the matched line", () => {
  const out = applyPatches("  m_Name: Player\n  m_Script: 1\n", [
    { op: "replace_line_contains", match: "m_Name:", replace: "  m_Name: PlayerPatched" },
  ]);
  assert.equal(out, "  m_Name: PlayerPatched\n  m_Script: 1\n");
});

test("applyPatches: replace on a no-newline file preserves the missing newline", () => {
  const out = applyPatches("only line", [
    { op: "replace_line_contains", match: "only", replace: "patched line" },
  ]);
  assert.equal(out, "patched line");
});

test("applyPatches: insert_after / insert_before splice correctly", () => {
  const base = "%YAML 1.1\n---\n";
  const after = applyPatches(base, [
    { op: "insert_after_line_contains", match: "%YAML", insert: "# inserted after" },
  ]);
  assert.equal(after, "%YAML 1.1\n# inserted after\n---\n");
  const before = applyPatches(base, [
    { op: "insert_before_line_contains", match: "---", insert: "# before sep" },
  ]);
  assert.equal(before, "%YAML 1.1\n# before sep\n---\n");
});

test("applyPatches: trim_trailing_whitespace strips spaces/tabs per line", () => {
  const out = applyPatches("a   \nb\t\n", [{ op: "trim_trailing_whitespace" }]);
  assert.equal(out, "a\nb\n");
});

test("applyPatches: multi-line insert preserves each inserted line", () => {
  const out = applyPatches("head\n", [
    { op: "insert_after_line_contains", match: "head", insert: "x\ny" },
  ]);
  assert.equal(out, "head\nx\ny\n");
});

test("applyPatches throws on a missing match (deterministic failure)", () => {
  assert.throws(
    () => applyPatches("a\n", [{ op: "replace_line_contains", match: "zzz", replace: "b" }]),
    /no line matched/,
  );
});

test("applyPatches applies multiple patches in order", () => {
  const out = applyPatches("a\nb\nc\n", [
    { op: "replace_line_contains", match: "a", replace: "A" },
    { op: "insert_after_line_contains", match: "A", insert: "between" },
    { op: "trim_trailing_whitespace" },
  ]);
  assert.equal(out, "A\nbetween\nb\nc\n");
});
