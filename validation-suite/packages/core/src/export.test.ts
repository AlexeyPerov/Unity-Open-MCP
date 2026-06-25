/**
 * Node:test harness for the run-summary export (phase-5).
 * Run with: `npm run test:core`
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  buildExportMarkdown,
  closeoutGateVerdict,
  rollup,
  tierCounts,
} from "./export.ts";
import {
  ensureTestState,
  setStepStatus,
  emptyState,
} from "./state.ts";
import type { RequirementLevel, Scenario, SuiteState } from "./types.ts";

function makeScenario(
  id: string,
  level: RequirementLevel,
  order: number,
  milestone = "m9",
  coverage: string[] = [],
): Scenario {
  return {
    id,
    title: `Scenario ${id}`,
    milestone,
    engineId: "unity",
    order,
    requirementLevel: level,
    steps: [
      { id: "info", type: "info" },
      { id: "done", type: "mark_done" },
    ],
    ...(coverage.length > 0 ? { automatedCoverage: coverage } : {}),
  };
}

function doneState(state: SuiteState, scenario: Scenario): SuiteState {
  let s = ensureTestState(state, scenario);
  for (const step of scenario.steps) s = setStepStatus(s, scenario, step.id, "done");
  return s;
}

const scenarios: Scenario[] = [
  makeScenario("m9-core-a", "required-core", 1),
  makeScenario("m9-core-b", "required-core", 2),
  makeScenario("m9-ext-a", "required-extended", 3),
  makeScenario("m9-opt-a", "optional", 4, "m9", ["ReserializeAssetsToolTests.X"]),
  makeScenario("m9-opt-b", "optional", 5, "m9", ["OutputSerializerTests.Y"]),
];

function baseState(): SuiteState {
  return emptyState("/proj", "unity");
}

// ── rollup + tierCounts ────────────────────────────────────────────────────

test("tierCounts totals match the slice and default to awaiting", () => {
  const counts = tierCounts(scenarios, baseState(), "required-core");
  assert.equal(counts.total, 2);
  assert.equal(counts.done, 0);
  assert.equal(counts.blocked, 0);
  assert.equal(counts.awaiting, 2);
});

test("tierCounts counts done + blocked across tiers", () => {
  let state = baseState();
  state = doneState(state, scenarios[0]); // core-a done
  state = setStepStatus(state, scenarios[1], "info", "blocked"); // core-b blocked
  state = doneState(state, scenarios[3]); // opt-a done
  const core = tierCounts(scenarios, state, "required-core");
  assert.equal(core.done, 1);
  assert.equal(core.blocked, 1);
  assert.equal(core.awaiting, 0);
  const opt = tierCounts(scenarios, state, "optional");
  assert.equal(opt.done, 1);
  assert.equal(opt.total, 2);
});

test("rollup over an empty slice is all zeros", () => {
  const counts = rollup([], baseState());
  assert.deepEqual(counts, { total: 0, done: 0, blocked: 0, awaiting: 0 });
});

// ── closeoutGateVerdict ────────────────────────────────────────────────────

test("closeoutGateVerdict PASS only when every core scenario is done", () => {
  let state = baseState();
  state = doneState(state, scenarios[0]);
  assert.match(
    closeoutGateVerdict(tierCounts(scenarios, state, "required-core")),
    /NOT YET/,
  );
  state = doneState(state, scenarios[1]);
  assert.match(
    closeoutGateVerdict(tierCounts(scenarios, state, "required-core")),
    /PASS/,
  );
});

test("closeoutGateVerdict treats blocked as not-done", () => {
  let state = baseState();
  state = doneState(state, scenarios[0]);
  state = setStepStatus(state, scenarios[1], "info", "blocked");
  assert.match(
    closeoutGateVerdict(tierCounts(scenarios, state, "required-core")),
    /NOT YET/,
  );
});

test("closeoutGateVerdict handles an empty core tier", () => {
  assert.match(
    closeoutGateVerdict({ total: 0, done: 0, blocked: 0, awaiting: 0 }),
    /no required-core scenarios loaded/,
  );
});

// ── buildExportMarkdown ────────────────────────────────────────────────────

test("buildExportMarkdown includes meta, breakdown, milestone tables, verdict", () => {
  const md = buildExportMarkdown({
    scenarios,
    state: baseState(),
    projectPath: "/proj",
    engineProfileId: "unity",
    generatedAt: "2026-06-25T00:00:00.000Z",
  });
  assert.match(md, /# Validation Suite — run summary/);
  assert.match(md, /\*\*Generated:\*\* 2026-06-25T00:00:00\.000Z/);
  assert.match(md, /\*\*Project:\*\* `\/proj`/);
  assert.match(md, /\*\*Engine profile:\*\* unity/);
  assert.match(md, /## Requirement-tier breakdown/);
  assert.match(md, /\| Required · core \| 2 \| 0 \| 0 \| 2 \|/);
  assert.match(md, /\| Optional \| 2 \| 0 \| 0 \| 2 \|/);
  assert.match(md, /## m9/);
  assert.match(md, /NOT YET/);
});

test("buildExportMarkdown groups optional scenarios under an Optional subheading", () => {
  const md = buildExportMarkdown({
    scenarios,
    state: baseState(),
    projectPath: "/proj",
    engineProfileId: "unity",
    generatedAt: "2026-06-25T00:00:00.000Z",
  });
  // Optional subheading appears once per milestone; m9 optional row ids present.
  assert.match(md, /### Optional \(automated-covered; runnable\)/);
  assert.match(md, /`m9-opt-a`/);
  assert.match(md, /`m9-opt-b`/);
});

test("buildExportMarkdown lists automated coverage refs and a dash when none", () => {
  let state = baseState();
  state = doneState(state, scenarios[3]); // opt-a has coverage
  const md = buildExportMarkdown({
    scenarios,
    state,
    projectPath: "/proj",
    engineProfileId: "unity",
    generatedAt: "2026-06-25T00:00:00.000Z",
  });
  assert.match(md, /ReserializeAssetsToolTests\.X/);
  // A scenario without coverage renders an em dash in the coverage column.
  assert.match(md, /\| `m9-core-a` — Scenario m9-core-a \| core \| ⏳ awaiting \| — \|/);
});

test("buildExportMarkdown shows PASS verdict and done status when core is complete", () => {
  let state = baseState();
  state = doneState(state, scenarios[0]);
  state = doneState(state, scenarios[1]);
  const md = buildExportMarkdown({
    scenarios,
    state,
    projectPath: "/proj",
    engineProfileId: "unity",
    generatedAt: "2026-06-25T00:00:00.000Z",
  });
  assert.match(md, /\*\*Closeout gate \(required-core\):\*\* PASS/);
  assert.match(md, /`m9-core-a` — Scenario m9-core-a \| core \| ✅ done/);
});

test("buildExportMarkdown tolerates null state + null project/profile", () => {
  const md = buildExportMarkdown({
    scenarios,
    state: null,
    projectPath: null,
    engineProfileId: null,
    generatedAt: "2026-06-25T00:00:00.000Z",
  });
  assert.match(md, /\(no project selected\)/);
  assert.match(md, /\(no profile\)/);
  // Awaiting is the default when state is null.
  assert.match(md, /⏳ awaiting/);
});

test("buildExportMarkdown separates multiple milestones into their own sections", () => {
  const multi: Scenario[] = [
    makeScenario("m9-core-a", "required-core", 1, "m9"),
    makeScenario("m10-core-a", "required-core", 1, "m10"),
  ];
  const md = buildExportMarkdown({
    scenarios: multi,
    state: baseState(),
    projectPath: "/proj",
    engineProfileId: "unity",
    generatedAt: "2026-06-25T00:00:00.000Z",
  });
  // Milestones are sorted lexically (m10 before m9); both sections appear.
  assert.match(md, /## m9/);
  assert.match(md, /## m10/);
});
