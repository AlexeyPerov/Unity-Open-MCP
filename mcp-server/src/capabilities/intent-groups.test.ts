// Tests for the intent/tag → group recommendation catalog.

import test from "node:test";
import assert from "node:assert/strict";

import { GROUP_IDS, TOOL_GROUPS } from "./tool-groups.js";
import {
  INTENT_TAGS,
  MUTATION_SIGNAL_KEYWORDS,
  recommendGroups,
  tokenize,
  extractIntentTags,
  extractMutationSignals,
} from "./intent-groups.js";

// ---------------------------------------------------------------------------
// Catalog invariants
// ---------------------------------------------------------------------------

test("every intent tag has at least one group, and every group exists in TOOL_GROUPS", () => {
  for (const entry of INTENT_TAGS) {
    assert.ok(entry.groups.length > 0, `${entry.tag} must map to ≥1 group`);
    for (const gid of entry.groups) {
      assert.ok(
        GROUP_IDS.has(gid),
        `${entry.tag} → unknown group '${gid}'`,
      );
    }
    assert.ok(
      typeof entry.reason === "string" && entry.reason.length > 5,
      `${entry.tag} must have a meaningful reason`,
    );
  }
});

test("intent tag names are lowercase and unique", () => {
  const seen = new Set<string>();
  for (const entry of INTENT_TAGS) {
    assert.equal(entry.tag, entry.tag.toLowerCase(), `${entry.tag} must be lowercase`);
    assert.ok(!seen.has(entry.tag), `${entry.tag} duplicate`);
    seen.add(entry.tag);
  }
});

test("intent keywords are lowercase (catalog order is the only canonicalization at lookup)", () => {
  for (const entry of INTENT_TAGS) {
    for (const kw of entry.keywords ?? []) {
      assert.equal(kw, kw.toLowerCase(), `${entry.tag} keyword '${kw}' must be lowercase`);
    }
  }
});

// ---------------------------------------------------------------------------
// tokenize + extract
// ---------------------------------------------------------------------------

test("tokenize splits on non-alphanumeric runs and lowercases", () => {
  assert.deepEqual(tokenize("NavMesh / path-finding"), ["navmesh", "path", "finding"]);
  assert.deepEqual(tokenize("GameObject  create!"), ["gameobject", "create"]);
  assert.deepEqual(tokenize(""), []);
  assert.deepEqual(tokenize("   "), []);
});

test("extractIntentTags matches single tokens and adjacent-token bigrams", () => {
  // single token
  assert.ok(extractIntentTags("animation").tags.includes("animation"));
  // bigram: "nav mesh" → joined "navmesh" → navigation tag
  const r = extractIntentTags("bake a nav mesh for the level");
  assert.ok(r.tags.includes("navigation"), "bigram navmesh should map to navigation");
  assert.ok(r.keywords.includes("navmesh"));
});

test("extractIntentTags returns empty arrays for empty / unknown text", () => {
  assert.deepEqual(extractIntentTags(""), { tags: [], keywords: [] });
  assert.deepEqual(extractIntentTags("zzz qqq xxx"), { tags: [], keywords: [] });
});

test("extractMutationSignals surfaces mutating/verify verbs", () => {
  const r = extractMutationSignals("create then modify and verify the scene");
  assert.ok(r.includes("create"));
  assert.ok(r.includes("modify"));
  assert.ok(r.includes("verify"));
});

// ---------------------------------------------------------------------------
// recommendGroups — happy path
// ---------------------------------------------------------------------------

test("recommendGroups: navigation intent returns the navigation group", () => {
  const rec = recommendGroups({ intent: "bake a navmesh for the dungeon" });
  assert.equal(rec.empty, false);
  const ids = rec.groups.map((g) => g.id);
  assert.ok(ids.includes("navigation"));
  const nav = rec.groups.find((g) => g.id === "navigation")!;
  assert.ok(nav.matchedTags.includes("navigation"));
});

test("recommendGroups: explicit tags activate the implied groups", () => {
  const rec = recommendGroups({ tags: ["audio", "lighting"] });
  const ids = rec.groups.map((g) => g.id);
  assert.ok(ids.includes("audio"));
  assert.ok(ids.includes("lighting"));
});

test("recommendGroups: a bare group id as a tag works", () => {
  const rec = recommendGroups({ tags: ["terrain"] });
  const ids = rec.groups.map((g) => g.id);
  assert.ok(ids.includes("terrain"));
});

test("recommendGroups: groups come back in catalog order", () => {
  // tags deliberately out of catalog order; the result must follow TOOL_GROUPS.
  const rec = recommendGroups({ tags: ["vfx", "core-via-reflection", "audio"] });
  const catalogOrder = TOOL_GROUPS.map((g) => g.id);
  const resultOrder = rec.groups.map((g) => g.id);
  const indices = resultOrder.map((id) => catalogOrder.indexOf(id));
  for (let i = 1; i < indices.length; i++) {
    assert.ok(indices[i] > indices[i - 1], "result must follow catalog order");
  }
});

test("recommendGroups: reasons are joined when multiple tags imply the same group", () => {
  // asset + scene + gameobject all imply typed-editor.
  const rec = recommendGroups({ tags: ["asset", "scene", "gameobject"] });
  const te = rec.groups.find((g) => g.id === "typed-editor");
  assert.ok(te, "typed-editor should be recommended");
  // At least three tags contributed.
  assert.ok(te!.matchedTags.length >= 3);
});

// ---------------------------------------------------------------------------
// recommendGroups — mutation / verify signal → gate-intelligence
// ---------------------------------------------------------------------------

test("recommendGroups: mutating intent adds gate-intelligence", () => {
  const rec = recommendGroups({ intent: "create and modify several gameobjects" });
  const gi = rec.groups.find((g) => g.id === "gate-intelligence");
  assert.ok(gi, "gate-intelligence must be added for a mutating intent");
  assert.equal(rec.gateIntelligenceAdded, true);
});

test("recommendGroups: verify intent adds gate-intelligence", () => {
  const rec = recommendGroups({ intent: "verify the scene references and run a scan" });
  const gi = rec.groups.find((g) => g.id === "gate-intelligence");
  assert.ok(gi, "gate-intelligence must be added for a verify intent");
});

test("recommendGroups: read-only intent does NOT add gate-intelligence", () => {
  const rec = recommendGroups({ intent: "list all prefabs in the project" });
  assert.equal(rec.gateIntelligenceAdded, false);
  assert.ok(
    !rec.groups.some((g) => g.id === "gate-intelligence"),
    "read-only intent must not surface gate-intelligence",
  );
});

test("recommendGroups: risk tag adds gate-intelligence", () => {
  const rec = recommendGroups({ tags: ["risk"] });
  const gi = rec.groups.find((g) => g.id === "gate-intelligence");
  assert.ok(gi);
});

test("MUTATION_SIGNAL_KEYWORDS are lowercase and non-empty", () => {
  for (const k of MUTATION_SIGNAL_KEYWORDS) {
    assert.equal(k, k.toLowerCase());
    assert.ok(k.length > 0);
  }
});

// ---------------------------------------------------------------------------
// recommendGroups — empty / unknown
// ---------------------------------------------------------------------------

test("recommendGroups: unknown intent returns an empty recommendation with a hint", () => {
  const rec = recommendGroups({ intent: "zzz qqq xxx yyy" });
  assert.equal(rec.empty, true);
  assert.equal(rec.groups.length, 0);
  assert.ok(rec.hint.length > 0);
  assert.match(rec.hint, /list_groups/);
});

test("recommendGroups: empty intent + empty tags returns an empty recommendation", () => {
  const rec = recommendGroups({ intent: "", tags: [] });
  assert.equal(rec.empty, true);
  assert.equal(rec.groups.length, 0);
});

test("recommendGroups: no args at all returns an empty recommendation", () => {
  const rec = recommendGroups({});
  assert.equal(rec.empty, true);
});

test("recommendGroups: unknown caller tags are reported as unmatchedTags, not invented as groups", () => {
  const rec = recommendGroups({ tags: ["floob", "blarg"] });
  assert.equal(rec.empty, true);
  assert.deepEqual(rec.unmatchedTags, ["blarg", "floob"]);
  // No invented group ids.
  for (const g of rec.groups) {
    assert.ok(GROUP_IDS.has(g.id), `invented group ${g.id}`);
  }
});

test("recommendGroups: mixed known + unknown tags recommend the known, report the unknown", () => {
  const rec = recommendGroups({ tags: ["navigation", "floob"] });
  assert.equal(rec.empty, false);
  assert.ok(rec.groups.some((g) => g.id === "navigation"));
  assert.deepEqual(rec.unmatchedTags, ["floob"]);
});

test("recommendGroups: whitespace-only caller tags are trimmed and ignored", () => {
  const rec = recommendGroups({ tags: ["  navigation  ", "   "] });
  assert.ok(rec.groups.some((g) => g.id === "navigation"));
  // The whitespace-only tag is dropped, not reported as unmatched.
  assert.deepEqual(rec.unmatchedTags, []);
});

// ---------------------------------------------------------------------------
// Pure-function determinism
// ---------------------------------------------------------------------------

test("recommendGroups is deterministic for the same input", () => {
  const a = recommendGroups({ intent: "build the project for android", tags: ["build"] });
  const b = recommendGroups({ intent: "build the project for android", tags: ["build"] });
  assert.deepEqual(a, b);
});
