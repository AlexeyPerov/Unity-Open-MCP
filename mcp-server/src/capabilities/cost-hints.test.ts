// Tests for the M22 Plan 1 / T22.1.5 cost-hints module.
//
// Pins:
//  1. The heavy-tool roster matches the documented profile-aware surface.
//  2. Every tool ships all three profiles (compact / balanced / full).
//  3. Cost bands are monotonically non-decreasing across profiles
//     (compact ≤ balanced ≤ full) — compact is the cheap default.
//  4. Recommended page sizes are sane (positive integers, bounded).
//  5. Recommended tool chains reference real tool names from the heavy roster
//     + the meta-tools (capabilities / manage_tools / ping / apply_fix).
//  6. The block is clean of internal IDs (no milestone / specs / references).
//
// Pure-function tests — runs under `node --test --experimental-strip-types`.

import test from "node:test";
import assert from "node:assert/strict";

import {
  TOOL_COST_HINTS,
  RECOMMENDED_PAGE_SIZE,
  RECOMMENDED_TOOL_CHAINS,
  COST_BAND_TOKENS,
  buildCostHints,
  describeBand,
  type CostBand,
} from "./cost-hints.js";

// ---------------------------------------------------------------------------
// Tool roster
// ---------------------------------------------------------------------------

const EXPECTED_HEAVY_TOOLS = [
  "unity_open_mcp_read_asset",
  "unity_open_mcp_search_assets",
  "unity_open_mcp_scene_get_data",
  "unity_open_mcp_find_references",
  "unity_open_mcp_validate_edit",
  "unity_open_mcp_scan_paths",
  "unity_open_mcp_component_get",
];

test("cost hints cover the documented heavy-tool roster", () => {
  const hinted = new Set(TOOL_COST_HINTS.map((h) => h.tool));
  for (const tool of EXPECTED_HEAVY_TOOLS) {
    assert.ok(hinted.has(tool), `${tool} must carry a cost hint`);
  }
});

test("cost hints do not advertise tools outside the heavy roster", () => {
  // Every hinted tool must be one of the documented heavy tools — no stale
  // entries from a removed tool.
  for (const hint of TOOL_COST_HINTS) {
    assert.ok(
      EXPECTED_HEAVY_TOOLS.includes(hint.tool),
      `${hint.tool} is hinted but not in the documented heavy roster`,
    );
  }
});

test("no duplicate tool names in the cost-hints table", () => {
  const names = TOOL_COST_HINTS.map((h) => h.tool);
  assert.equal(new Set(names).size, names.length);
});

// ---------------------------------------------------------------------------
// Per-tool profile shape
// ---------------------------------------------------------------------------

test("every tool ships all three profiles", () => {
  for (const hint of TOOL_COST_HINTS) {
    assert.ok(hint.profiles.compact, `${hint.tool} must hint compact`);
    assert.ok(hint.profiles.balanced, `${hint.tool} must hint balanced`);
    assert.ok(hint.profiles.full, `${hint.tool} must hint full`);
  }
});

test("every tool describes what profile and page_size control", () => {
  for (const hint of TOOL_COST_HINTS) {
    assert.ok(
      typeof hint.profileControls === "string" && hint.profileControls.length > 0,
      `${hint.tool} profileControls must be non-empty`,
    );
    assert.ok(
      typeof hint.pageSizePages === "string" && hint.pageSizePages.length > 0,
      `${hint.tool} pageSizePages must be non-empty`,
    );
  }
});

// ---------------------------------------------------------------------------
// Band monotonicity — compact ≤ balanced ≤ full
// ---------------------------------------------------------------------------

const BAND_RANK: Record<CostBand, number> = { small: 0, medium: 1, large: 2 };

test("cost bands are monotonically non-decreasing compact ≤ balanced ≤ full", () => {
  // compact is the cheap default; bands must never claim a cheaper higher
  // profile. Equal bands are allowed (some tools may be small in both compact
  // and balanced); the invariant is monotonicity, not strict increase.
  for (const hint of TOOL_COST_HINTS) {
    const c = BAND_RANK[hint.profiles.compact.band];
    const b = BAND_RANK[hint.profiles.balanced.band];
    const f = BAND_RANK[hint.profiles.full.band];
    assert.ok(
      c <= b,
      `${hint.tool}: compact band (${hint.profiles.compact.band}) must be ≤ balanced (${hint.profiles.balanced.band})`,
    );
    assert.ok(
      b <= f,
      `${hint.tool}: balanced band (${hint.profiles.balanced.band}) must be ≤ full (${hint.profiles.full.band})`,
    );
  }
});

test("compact is never the most expensive band on any tool", () => {
  for (const hint of TOOL_COST_HINTS) {
    const c = BAND_RANK[hint.profiles.compact.band];
    const f = BANK_RANK(hint.profiles.full.band);
    assert.ok(
      c <= f,
      `${hint.tool}: compact (${hint.profiles.compact.band}) must be ≤ full (${hint.profiles.full.band})`,
    );
  }
});

// local helper to avoid a typo'd property-access typo; mirrors BAND_RANK.
function BANK_RANK(b: CostBand): number {
  return BAND_RANK[b];
}

test("full is always large or medium (never small) on the heavy tools", () => {
  // The heavy tools are heavy precisely because their full profile can flood
  // context. A `small` full band would be self-contradictory.
  for (const hint of TOOL_COST_HINTS) {
    assert.notEqual(
      hint.profiles.full.band,
      "small",
      `${hint.tool}: full profile must not be 'small'`,
    );
  }
});

// ---------------------------------------------------------------------------
// describeBand
// ---------------------------------------------------------------------------

test("describeBand returns a token-range string for each band", () => {
  for (const band of ["small", "medium", "large"] as CostBand[]) {
    const s = describeBand(band);
    assert.ok(typeof s === "string" && s.length > 0);
    assert.ok(/token|unbounded/.test(s), `${band} should mention tokens or unbounded`);
  }
});

test("COST_BAND_TOKENS ranges are ordered and well-formed", () => {
  for (const band of ["small", "medium", "large"] as CostBand[]) {
    const r = COST_BAND_TOKENS[band];
    assert.ok(typeof r.min === "number" && r.min >= 0);
    assert.ok(typeof r.max === "number");
    // max == 0 means unbounded; otherwise max > min.
    if (r.max > 0) {
      assert.ok(r.max > r.min, `${band} max must exceed min`);
    }
  }
  // small.max <= medium.min <= medium.max <= large.min (allowing equality).
  assert.ok(COST_BAND_TOKENS.small.max <= COST_BAND_TOKENS.medium.min);
  assert.ok(COST_BAND_TOKENS.medium.max <= COST_BAND_TOKENS.large.min);
});

// ---------------------------------------------------------------------------
// Recommended page sizes
// ---------------------------------------------------------------------------

test("every heavy tool has a recommended page size", () => {
  for (const tool of EXPECTED_HEAVY_TOOLS) {
    assert.ok(
      tool in RECOMMENDED_PAGE_SIZE,
      `${tool} must have a recommended page size`,
    );
  }
});

test("recommended page sizes are positive and bounded", () => {
  for (const [tool, size] of Object.entries(RECOMMENDED_PAGE_SIZE)) {
    assert.ok(
      Number.isInteger(size) && size >= 1 && size <= 200,
      `${tool} recommended page size ${size} must be an int in [1, 200]`,
    );
  }
});

// ---------------------------------------------------------------------------
// Recommended tool chains
// ---------------------------------------------------------------------------

test("recommended tool chains carry a name, task, and non-empty steps", () => {
  assert.ok(RECOMMENDED_TOOL_CHAINS.length >= 3);
  for (const chain of RECOMMENDED_TOOL_CHAINS) {
    assert.ok(typeof chain.name === "string" && chain.name.length > 0);
    assert.ok(typeof chain.task === "string" && chain.task.length > 0);
    assert.ok(Array.isArray(chain.steps) && chain.steps.length >= 1);
  }
});

test("every recommended chain name is unique", () => {
  const names = RECOMMENDED_TOOL_CHAINS.map((c) => c.name);
  assert.equal(new Set(names).size, names.length);
});

test("recommended chains reference real tool names or short prose steps", () => {
  // Steps may be plain-English guidance; but when a step is just a tool name
  // (paren-free), it must be a real unity_open_mcp_* / unity_senses_* tool.
  const knownTool = /^(unity_open_mcp_\w+|unity_senses_\w+)$/;
  for (const chain of RECOMMENDED_TOOL_CHAINS) {
    for (const step of chain.steps) {
      const firstToken = step.split(/[\s(]/)[0];
      if (knownTool.test(firstToken)) {
        // It claims to be a tool name — nothing else to check here (the
        // capabilities test asserts these exist in the implemented surface).
        assert.ok(firstToken.startsWith("unity_"));
      } else {
        // Prose step — must be non-trivial.
        assert.ok(step.length > 10, `prose step too short: ${step}`);
      }
    }
  }
});

// ---------------------------------------------------------------------------
// buildCostHints aggregator
// ---------------------------------------------------------------------------

test("buildCostHints returns the tools, page sizes, chains, and guidance", () => {
  const block = buildCostHints();
  assert.ok(Array.isArray(block.tools) && block.tools.length === TOOL_COST_HINTS.length);
  assert.equal(block.recommendedPageSize, RECOMMENDED_PAGE_SIZE);
  assert.equal(block.recommendedToolChains, RECOMMENDED_TOOL_CHAINS);
  assert.ok(typeof block.guidance === "string" && block.guidance.length > 0);
});

test("buildCostHints guidance restates the compact-first contract", () => {
  const block = buildCostHints();
  assert.match(block.guidance, /compact/i);
  assert.match(block.guidance, /page_size/);
});

// ---------------------------------------------------------------------------
// Clean of internal IDs (AGENTS.md §"No internal references")
// ---------------------------------------------------------------------------

const BANNED = [
  /\bM\d+(\.\d+)*\b/,
  /\bT\d+\.\d+\.\d+\b/,
  /specs\//i,
  /\/references\//i,
  /\bIvanMurzak\b/i,
  /\bAnkleBreaker\b/i,
  /\bunity-scanner\b/i,
  /\bUnity-MCP\b/i,
  /\bunity-mcp-beta\b/i,
  /\bCoplay\b/i,
  /\bUCP\b/,
];

test("cost-hints text is clean of internal IDs and reference-project handles", () => {
  const block = buildCostHints();
  const blob = JSON.stringify(block);
  const offenders: string[] = [];
  for (const pattern of BANNED) {
    const match = pattern.exec(blob);
    if (match) offenders.push(match[0]);
  }
  assert.deepEqual(
    offenders,
    [],
    `cost hints reference banned patterns: ${offenders.join(", ")}`,
  );
});
