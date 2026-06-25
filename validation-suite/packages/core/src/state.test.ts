/**
 * Node:test harness for the core state model.
 * Run with: `npm run test:core`
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  checkShape,
  defaultTestState,
  emptyState,
  ensureTestState,
  resetTestState,
  setStepStatus,
} from "./state.ts";
import { STATE_VERSION, type Scenario } from "./types.ts";

const scenario: Scenario = {
  id: "m9-x",
  title: "X",
  milestone: "m9",
  engineId: "unity",
  order: 0,
  requirementLevel: "required-core",
  steps: [
    { id: "info", type: "info" },
    { id: "done", type: "mark_done" },
  ],
};

test("defaultTestState marks all steps awaiting with null manifest refs", () => {
  const t = defaultTestState(scenario);
  assert.equal(t.status, "awaiting");
  assert.equal(t.stepStatus.info, "awaiting");
  assert.equal(t.stepStatus.done, "awaiting");
  assert.equal(t.manifestRefs.info, null);
  assert.equal(t.manifestRefs.done, null);
});

test("setStepStatus completes the test only when every step is done", () => {
  let state = emptyState("/p", "unity");
  state = ensureTestState(state, scenario);
  state = setStepStatus(state, scenario, "info", "done");
  assert.equal(state.tests[scenario.id].status, "awaiting");
  assert.equal(state.tests[scenario.id].completedAt, null);
  state = setStepStatus(state, scenario, "done", "done");
  assert.equal(state.tests[scenario.id].status, "done");
  assert.ok(state.tests[scenario.id].completedAt);
});

test("setStepStatus flips test to blocked when a step is blocked", () => {
  let state = emptyState("/p", "unity");
  state = setStepStatus(state, scenario, "info", "blocked");
  assert.equal(state.tests[scenario.id].status, "blocked");
});

test("resetTestState returns all steps to awaiting and clears completion", () => {
  let state = emptyState("/p", "unity");
  state = setStepStatus(state, scenario, "info", "done");
  state = setStepStatus(state, scenario, "done", "done");
  assert.equal(state.tests[scenario.id].status, "done");
  state = resetTestState(state, scenario);
  assert.equal(state.tests[scenario.id].status, "awaiting");
  assert.equal(state.tests[scenario.id].stepStatus.info, "awaiting");
  assert.equal(state.tests[scenario.id].completedAt, null);
});

test("ensureTestState adds step entries when scenario gains a step", () => {
  let state = emptyState("/p", "unity");
  state = ensureTestState(state, scenario);
  const grown: Scenario = {
    ...scenario,
    steps: [...scenario.steps, { id: "extra", type: "info" }],
  };
  state = ensureTestState(state, grown);
  assert.equal(state.tests[scenario.id].stepStatus.extra, "awaiting");
  assert.equal(state.tests[scenario.id].manifestRefs.extra, null);
});

test("checkShape returns missing for null/undefined", () => {
  assert.equal(checkShape(null).kind, "missing");
  assert.equal(checkShape(undefined).kind, "missing");
});

test("checkShape returns incompatible on version mismatch", () => {
  const res = checkShape({ version: 99, project: { path: "/p" }, tests: {} });
  assert.equal(res.kind, "incompatible");
  if (res.kind === "incompatible") {
    assert.equal(res.expected, STATE_VERSION);
    assert.equal(res.found, 99);
  }
});

test("checkShape returns malformed for a non-object root", () => {
  const res = checkShape([1, 2, 3]);
  assert.equal(res.kind, "malformed");
});

test("checkShape returns malformed when project/tests blocks are absent", () => {
  const res = checkShape({ version: 1 });
  assert.equal(res.kind, "malformed");
});

test("checkShape returns compatible for a valid state", () => {
  const state = emptyState("/p", "unity");
  const res = checkShape(state);
  assert.equal(res.kind, "compatible");
  if (res.kind === "compatible") {
    assert.equal(res.state.version, STATE_VERSION);
  }
});
