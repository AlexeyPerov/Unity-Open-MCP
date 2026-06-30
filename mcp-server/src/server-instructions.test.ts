// Tests for the M22 Plan 1 / T22.1.5 rich server-instructions module.
//
// Pins:
//  1. The instructions string is non-empty and multi-section (paging,
//     API verification, mutate→gate→fix, routing).
//  2. It names the heavy tools, the profile/page_size knobs, and the
//     drill-down flags — the contract an agent needs to budget.
//  3. It is clean of internal IDs (AGENTS.md §"No internal references").
//  4. getServerInstructions memoizes (same reference on repeat calls).
//  5. findBannedInstructionsReferences detects planted offenders (test the
//     guard, not just the happy path).
//
// Pure-function tests — runs under `node --test --experimental-strip-types`.

import test from "node:test";
import assert from "node:assert/strict";

import {
  buildServerInstructions,
  getServerInstructions,
  findBannedInstructionsReferences,
} from "./server-instructions.js";

// ---------------------------------------------------------------------------
// Structure
// ---------------------------------------------------------------------------

test("server instructions are non-empty and multi-paragraph", () => {
  const s = buildServerInstructions();
  assert.ok(typeof s === "string");
  assert.ok(s.length > 500, "instructions should be a substantial briefing");
  // Multiple sections separated by blank lines.
  assert.ok(s.split(/\n\s*\n/).length >= 4, "should have several sections");
});

test("instructions name the heavy tools", () => {
  const s = buildServerInstructions();
  for (const tool of [
    "read_asset",
    "search_assets",
    "scene_get_data",
    "find_references",
    "validate_edit",
    "scan_paths",
  ]) {
    assert.ok(
      s.includes(tool),
      `instructions should mention the heavy tool ${tool}`,
    );
  }
});

test("instructions document the profile + page_size knobs", () => {
  const s = buildServerInstructions();
  assert.match(s, /profile/);
  assert.match(s, /compact/);
  assert.match(s, /balanced/);
  assert.match(s, /full/);
  assert.match(s, /page_size/);
  assert.match(s, /next_cursor/);
  assert.match(s, /pagination/);
});

test("instructions document the drill-down flags", () => {
  const s = buildServerInstructions();
  for (const flag of ["component", "path", "id", "override"]) {
    assert.ok(s.includes(flag), `instructions should mention drill-down flag ${flag}`);
  }
});

test("instructions document the Unity-API verification workflow", () => {
  const s = buildServerInstructions();
  // The four-step workflow is named in order.
  assert.match(s, /search_assets/);
  assert.match(s, /find_members/);
  assert.match(s, /type_schema/);
  assert.match(s, /execute_csharp/);
  // And the hallucination warning.
  assert.match(s, /hallucinat/i);
});

test("instructions document mutate → gate → fix + inline logs", () => {
  const s = buildServerInstructions();
  assert.match(s, /paths_hint/);
  assert.match(s, /gate/i);
  assert.match(s, /delta/);
  assert.match(s, /apply_fix/);
  assert.match(s, /dry_run/);
  assert.match(s, /logs/);
});

test("instructions document offline + bridge-offline recovery", () => {
  const s = buildServerInstructions();
  assert.match(s, /offline/);
  assert.match(s, /bridge_offline|bridge_compile_failed/);
  assert.match(s, /read_compile_errors/);
});

test("instructions document path conventions (Assets/, forward slashes)", () => {
  const s = buildServerInstructions();
  assert.match(s, /Assets\//);
  assert.match(s, /forward slash/i);
});

// ---------------------------------------------------------------------------
// Clean of internal IDs
// ---------------------------------------------------------------------------

test("instructions are clean of internal IDs and reference-project handles", () => {
  const s = buildServerInstructions();
  const offenders = findBannedInstructionsReferences(s);
  assert.deepEqual(
    offenders,
    [],
    `instructions carry banned references: ${offenders.join(", ")}`,
  );
});

test("findBannedInstructionsReferences detects planted offenders", () => {
  // Guard the guard: each banned category must be detected when planted.
  const samples = [
    "see M22 for details",
    "tracked in T22.1.5",
    "lives in specs/execution",
    "ported from /references/unity-mcp",
    "IvanMurzak style",
    "AnkleBreaker model",
    "unity-scanner compression",
    "Unity-MCP plugin",
    "unity-mcp-beta server",
    "Coplay _build_instructions",
    "UCP output caps",
  ];
  for (const sample of samples) {
    const offenders = findBannedInstructionsReferences(sample);
    assert.ok(
      offenders.length >= 1,
      `planted offender not detected: ${sample}`,
    );
  }
});

test("findBannedInstructionsReferences is empty for clean text", () => {
  const clean = "Use compact profile then drill down with the component flag.";
  assert.deepEqual(findBannedInstructionsReferences(clean), []);
});

// `Plan`, `milestone`, `task` in plain English are allowed (not banned).
test("plain-English plan/milestone/task words are NOT flagged", () => {
  const prose = "Plan your calls; each milestone has its own task list.";
  assert.deepEqual(findBannedInstructionsReferences(prose), []);
});

// ---------------------------------------------------------------------------
// Memoization
// ---------------------------------------------------------------------------

test("getServerInstructions memoizes — repeat calls return the same reference", () => {
  const a = getServerInstructions();
  const b = getServerInstructions();
  assert.equal(a, b, "getServerInstructions should return the same string reference");
});

test("getServerInstructions content equals a fresh build", () => {
  assert.equal(getServerInstructions(), buildServerInstructions());
});
