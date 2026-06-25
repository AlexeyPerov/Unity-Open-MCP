/**
 * State-model helpers: defaults, step-status transitions, and the
 * incompatible-shape check that powers the warn+reset policy.
 *
 * No migration logic (idea.md → Schema policy for v1): when the
 * on-disk shape's `version` does not match {@link STATE_VERSION}, the
 * app warns and asks the operator to reset local suite data before
 * continuing. The Rust backend owns atomic read/write; these pure
 * helpers operate on already-parsed state.
 */

import { STATE_VERSION, type Scenario, type Status, type SuiteState, type TestState } from "./types.ts";

/** Build a fresh default {@link TestState} for a scenario. */
export function defaultTestState(scenario: Scenario): TestState {
  const stepStatus: Record<string, Status> = {};
  for (const step of scenario.steps) stepStatus[step.id] = "awaiting";
  const manifestRefs: Record<string, null> = {};
  for (const step of scenario.steps) manifestRefs[step.id] = null;
  return {
    status: "awaiting",
    stepStatus,
    startedAt: null,
    completedAt: null,
    actualsRefs: {},
    manifestRefs,
  };
}

/**
 * Build an empty {@link SuiteState} for a project + profile. Used as the
 * fallback when no state file exists yet, and as the reset target.
 */
export function emptyState(projectPath: string, engineProfileId: string): SuiteState {
  return {
    version: STATE_VERSION,
    project: {
      path: projectPath,
      engineProfileId,
      lastOpenedAt: nowIso(),
    },
    tests: {},
  };
}

/**
 * Ensure a scenario has a state entry, creating a default if missing.
 * Also reconciles any steps that were added to the scenario JSON since
 * the state was written (new steps default to `awaiting`); stale
 * step ids are left in place so the operator can see them, never
 * silently dropped.
 */
export function ensureTestState(state: SuiteState, scenario: Scenario): SuiteState {
  const existing = state.tests[scenario.id];
  if (!existing) {
    return {
      ...state,
      tests: { ...state.tests, [scenario.id]: defaultTestState(scenario) },
    };
  }
  const stepStatus: Record<string, Status> = { ...existing.stepStatus };
  const manifestRefs: Record<string, string | null> = { ...existing.manifestRefs };
  let changed = false;
  for (const step of scenario.steps) {
    if (!(step.id in stepStatus)) {
      stepStatus[step.id] = "awaiting";
      changed = true;
    }
    if (!(step.id in manifestRefs)) {
      manifestRefs[step.id] = null;
      changed = true;
    }
  }
  if (!changed) return state;
  return {
    ...state,
    tests: {
      ...state.tests,
      [scenario.id]: { ...existing, stepStatus, manifestRefs },
    },
  };
}

/**
 * Set a step's status and recompute the test-level status. A test is
 * `done` only when every step is `done`; otherwise it reflects the
 * worst remaining step (`blocked` wins over `awaiting`).
 */
export function setStepStatus(
  state: SuiteState,
  scenario: Scenario,
  stepId: string,
  status: Status,
): SuiteState {
  const ensured = ensureTestState(state, scenario);
  const test = ensured.tests[scenario.id];
  const stepStatus = { ...test.stepStatus, [stepId]: status };
  const updated: TestState = { ...test, stepStatus };
  // Recompute test status from the step map.
  const all = scenario.steps.map((s) => stepStatus[s.id] ?? "awaiting");
  if (all.length > 0 && all.every((s) => s === "done")) {
    updated.status = "done";
    updated.completedAt = nowIso();
    if (!updated.startedAt) updated.startedAt = nowIso();
  } else if (all.some((s) => s === "blocked")) {
    updated.status = "blocked";
  } else {
    updated.status = "awaiting";
    updated.completedAt = null;
    if (Object.values(stepStatus).some((s) => s === "done")) {
      if (!updated.startedAt) updated.startedAt = nowIso();
    }
  }
  return { ...ensured, tests: { ...ensured.tests, [scenario.id]: updated } };
}

/** Mark a test entirely reset (all steps awaiting, payloads/manifests cleared). */
export function resetTestState(state: SuiteState, scenario: Scenario): SuiteState {
  return { ...state, tests: { ...state.tests, [scenario.id]: defaultTestState(scenario) } };
}

/**
 * Outcome of a state-shape compatibility check.
 * `compatible` → use the parsed state. `incompatible` → warn + require
 * operator reset (per unity.md → State file schema → version policy).
 * `malformed` → the file exists but is not valid JSON / wrong envelope.
 */
export type ShapeCheck =
  | { kind: "compatible"; state: SuiteState }
  | { kind: "incompatible"; found: number; expected: number }
  | { kind: "malformed"; reason: string }
  | { kind: "missing" };

/** Inspect a parsed `.state.json` candidate for shape compatibility. */
export function checkShape(parsed: unknown): ShapeCheck {
  if (parsed === null || parsed === undefined) return { kind: "missing" };
  if (
    typeof parsed !== "object" ||
    Array.isArray(parsed) ||
    typeof (parsed as { version?: unknown }).version !== "number"
  ) {
    return { kind: "malformed", reason: "State root must be an object with a numeric \"version\"." };
  }
  const p = parsed as SuiteState;
  if (p.version !== STATE_VERSION) {
    return { kind: "incompatible", found: p.version, expected: STATE_VERSION };
  }
  // Light envelope check: project + tests maps.
  if (
    typeof p.project !== "object" ||
    p.project === null ||
    typeof p.project.path !== "string" ||
    typeof p.tests !== "object" ||
    p.tests === null
  ) {
    return { kind: "malformed", reason: "State is missing a valid \"project\" or \"tests\" block." };
  }
  return { kind: "compatible", state: p };
}

/** ISO-8601 timestamp for `now` (UTC, milliseconds). */
export function nowIso(): string {
  return new Date().toISOString();
}
