/**
 * Node:test harness for the core scenario loader + profile parser.
 * Run with: `npm run test:core`
 * (Node 22+ strips TS types; the suite mirrors the Hub's test layout.)
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  loadScenarios,
  parseProfile,
  parseScenario,
} from "./loader.ts";
import type { Scenario } from "./types.ts";

function sampleScenario(overrides: Record<string, unknown> = {}): unknown {
  return {
    id: "m9-reserialize-happy-path",
    title: "Reserialize happy path",
    milestone: "m9",
    engineId: "unity",
    order: 0,
    requirementLevel: "required-core",
    steps: [
      { id: "info", type: "info", body: "Open the project." },
      {
        id: "setup-fixture",
        type: "setup",
        actions: [{ action: "fs_copy", from: "a.prefab", to: "{fixtureRoot}/a.prefab" }],
      },
      { id: "done", type: "mark_done" },
    ],
    ...overrides,
  };
}

// ── parseScenario ─────────────────────────────────────────────────────────────

test("parseScenario accepts a well-formed scenario", () => {
  const { scenario, error } = parseScenario("a.json", sampleScenario());
  assert.equal(error, undefined);
  assert.equal(scenario!.id, "m9-reserialize-happy-path");
  assert.equal(scenario!.steps.length, 3);
  assert.equal(scenario!.requirementLevel, "required-core");
});

test("parseScenario rejects unknown requirement level", () => {
  const { scenario, error } = parseScenario(
    "a.json",
    sampleScenario({ requirementLevel: "must" }),
  );
  assert.equal(scenario, undefined);
  assert.match(error!, /Unknown requirementLevel/);
});

test("parseScenario rejects unknown step type", () => {
  const raw = sampleScenario({
    steps: [{ id: "x", type: "frobnicate" }],
  });
  const { error } = parseScenario("a.json", raw);
  assert.match(error!, /Unknown step type/);
});

test("parseScenario rejects duplicate step ids", () => {
  const raw = sampleScenario({
    steps: [
      { id: "x", type: "info" },
      { id: "x", type: "info" },
    ],
  });
  const { error } = parseScenario("a.json", raw);
  assert.match(error!, /duplicate step id/);
});

test("parseScenario rejects empty steps array", () => {
  const { error } = parseScenario("a.json", sampleScenario({ steps: [] }));
  assert.match(error!, /non-empty array/);
});

test("parseScenario rejects unknown action type in setup step", () => {
  const raw = sampleScenario({
    steps: [
      {
        id: "setup",
        type: "setup",
        actions: [{ action: "fs_blast", path: "x" }],
      },
    ],
  });
  const { error } = parseScenario("a.json", raw);
  assert.match(error!, /Unknown action type/);
});

// ── fs_patch patch-op validation (phase-2 task 3) ────────────────────────────

test("parseScenario rejects unknown fs_patch op at load time", () => {
  const raw = sampleScenario({
    steps: [
      {
        id: "setup",
        type: "setup",
        actions: [
          { action: "fs_patch", path: "x", patches: [{ op: "nuke_everything" }] },
        ],
      },
    ],
  });
  const { error } = parseScenario("a.json", raw);
  assert.match(error!, /Unknown patch op/);
});

test("parseScenario rejects fs_patch without patches array", () => {
  const raw = sampleScenario({
    steps: [
      { id: "setup", type: "setup", actions: [{ action: "fs_patch", path: "x" }] },
    ],
  });
  const { error } = parseScenario("a.json", raw);
  assert.match(error!, /non-empty "patches" array/);
});

test("parseScenario rejects replace_line_contains missing match/replace", () => {
  const raw = sampleScenario({
    steps: [
      {
        id: "setup",
        type: "setup",
        actions: [
          { action: "fs_patch", path: "x", patches: [{ op: "replace_line_contains", replace: "y" }] },
        ],
      },
    ],
  });
  const { error } = parseScenario("a.json", raw);
  assert.match(error!, /replace_line_contains needs "match"/);
});

test("parseScenario accepts all four pinned patch ops with required params", () => {
  const raw = sampleScenario({
    steps: [
      {
        id: "setup",
        type: "setup",
        actions: [
          {
            action: "fs_patch",
            path: "x",
            patches: [
              { op: "replace_line_contains", match: "a", replace: "b" },
              { op: "insert_after_line_contains", match: "a", insert: "c" },
              { op: "insert_before_line_contains", match: "a", insert: "d" },
              { op: "trim_trailing_whitespace" },
            ],
          },
        ],
      },
    ],
  });
  const { error, scenario } = parseScenario("a.json", raw);
  assert.equal(error, undefined);
  assert.equal(scenario!.steps[0].actions!.length, 1);
});

test("parseScenario rejects setup step without actions", () => {
  const raw = sampleScenario({
    steps: [{ id: "setup", type: "setup" }],
  });
  const { error } = parseScenario("a.json", raw);
  assert.match(error!, /non-empty "actions" array/);
});

test("parseScenario rejects missing required fields", () => {
  for (const field of ["id", "title", "milestone", "engineId", "requirementLevel"]) {
    const raw = sampleScenario();
    delete (raw as Record<string, unknown>)[field];
    const { error } = parseScenario("a.json", raw);
    assert.match(error!, new RegExp(`Missing required field "${field}"`), `field ${field}`);
  }
});

test("parseScenario rejects non-numeric order", () => {
  const { error } = parseScenario("a.json", sampleScenario({ order: "first" }));
  assert.match(error!, /"order"/);
});

test("parseScenario accepts all five action verbs", () => {
  const raw = sampleScenario({
    steps: [
      {
        id: "setup",
        type: "setup",
        actions: [
          { action: "fs_copy", from: "a", to: "b" },
          { action: "fs_patch", path: "b", patches: [{ op: "trim_trailing_whitespace" }] },
          { action: "fs_delete", paths: ["b"] },
          { action: "mcp_tool", tool: "unity_open_mcp_ping" },
          { action: "manual", note: "do it" },
        ],
      },
    ],
  });
  const { scenario, error } = parseScenario("a.json", raw);
  assert.equal(error, undefined);
  assert.equal(scenario!.steps[0].actions!.length, 5);
});

// ── loadScenarios ─────────────────────────────────────────────────────────────

test("loadScenarios collects errors without throwing and sorts scenarios", () => {
  const files = [
    { source: "good-b.json", content: sampleScenario({ id: "b", order: 1 }) },
    { source: "good-a.json", content: sampleScenario({ id: "a", order: 0 }) },
    {
      source: "bad.json",
      content: sampleScenario({ requirementLevel: "nope" }),
    },
  ];
  const res = loadScenarios(files);
  assert.equal(res.scenarios.length, 2);
  assert.equal(res.scenarios[0].id, "a");
  assert.equal(res.scenarios[1].id, "b");
  assert.equal(res.errors.length, 1);
  assert.equal(res.errors[0].source, "bad.json");
});

test("loadScenarios reports duplicate ids across files", () => {
  const files = [
    { source: "first.json", content: sampleScenario({ id: "dup" }) },
    { source: "second.json", content: sampleScenario({ id: "dup" }) },
  ];
  const res = loadScenarios(files);
  assert.equal(res.scenarios.length, 1);
  assert.equal(res.errors.length, 1);
  assert.match(res.errors[0].message, /Duplicate scenario id "dup"/);
});

test("loadScenarios orders by milestone then order then id", () => {
  const mk = (id: string, milestone: string, order: number): Scenario =>
    ({
      id,
      title: id,
      milestone,
      engineId: "unity",
      order,
      requirementLevel: "optional" as const,
      steps: [{ id: "info", type: "info" as const }],
    });
  const res = loadScenarios([
    { source: "1", content: mk("c", "m9", 5) },
    { source: "2", content: mk("a", "m9", 1) },
    { source: "3", content: mk("b", "m9", 1) },
    { source: "4", content: mk("z", "m8", 9) },
  ]);
  assert.deepEqual(
    res.scenarios.map((s) => s.id),
    ["z", "a", "b", "c"],
  );
});

// ── parseProfile ──────────────────────────────────────────────────────────────

function unityProfile(): unknown {
  return {
    id: "unity",
    displayName: "Unity Open MCP",
    mcpCliBinary: "unity-open-mcp",
    paths: {
      fixtureRoot: "Assets/_ValidationSuite/<test-id>/",
      stateRoot: "UserSettings/ValidationSuite/",
      stateFile: "UserSettings/ValidationSuite/.state.json",
      actualsDir: "UserSettings/ValidationSuite/actuals/",
      exportsDir: "UserSettings/ValidationSuite/exports/",
    },
    markers: { dirs: ["Assets", "ProjectSettings"], files: ["ProjectSettings/ProjectVersion.txt"] },
    companions: [{ primary: "*.prefab", companion: "*.prefab.meta" }],
    placeholders: ["{fixtureRoot}", "{projectRoot}"],
    toolNamePrefix: "unity_open_mcp_",
  };
}

test("parseProfile accepts the unity profile", () => {
  const p = parseProfile(unityProfile());
  assert.equal(p.id, "unity");
  assert.equal(p.paths.fixtureRoot, "Assets/_ValidationSuite/<test-id>/");
  assert.equal(p.companions.length, 1);
});

test("parseProfile throws on missing paths block", () => {
  const raw = unityProfile() as Record<string, unknown>;
  delete raw.paths;
  assert.throws(() => parseProfile(raw), /paths/);
});

test("parseProfile throws on bad companion entry", () => {
  const raw = unityProfile() as Record<string, unknown>;
  (raw as { companions: unknown[] }).companions = [{ primary: "x" }];
  assert.throws(() => parseProfile(raw), /companions/);
});
