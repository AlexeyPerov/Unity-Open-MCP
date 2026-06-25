/**
 * Scenario + engine-profile loading and validation.
 *
 * Pure functions: callers pass the raw file contents (the Rust backend
 * owns disk I/O via Tauri commands). The loader validates ids,
 * uniqueness, requirement levels, and unknown step/action types at load
 * time (phase-1 task 4; idea.md → action schema; execution-plan risk
 * mitigation #2 "config-action drift"). Action *executor* stubs are OK
 * until Phase 2 — we only validate the `action` verb here, not its
 * params.
 *
 * On any validation error the loader returns a {@link ScenarioLoadError}
 * in the result rather than throwing, so a single bad file never blocks
 * the rest of the suite (phase-1 validation: readable UI error without
 * crashing).
 */

import {
  ACTION_TYPES,
  REQUIREMENT_LEVELS,
  STEP_TYPES,
  missingField,
  unknownValue,
} from "./errors.ts";
import type {
  ActionType,
  EngineProfile,
  RequirementLevel,
  Scenario,
  ScenarioLoadError,
  ScenarioLoadResult,
  ScenarioStep,
  SetupAction,
  StepType,
} from "./types.ts";

/** Minimal duck-type check that a parsed value is a plain object. */
function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function asString(v: unknown): string | undefined {
  return typeof v === "string" && v.length > 0 ? v : undefined;
}

function asStringArray(v: unknown): string[] | undefined {
  if (!Array.isArray(v)) return undefined;
  const out: string[] = [];
  for (const item of v) {
    if (typeof item !== "string") return undefined;
    out.push(item);
  }
  return out;
}

/** Validate a single setup action's `action` verb + basic shape. */
function validateAction(
  raw: unknown,
  stepId: string,
  index: number,
): SetupAction | undefined {
  // Returns the action if valid; the caller collects the error message.
  // (We re-validate inside validateStep, but keep this typed helper so
  // the happy path produces a strongly-typed action.)
  if (!isRecord(raw)) return undefined;
  return raw as SetupAction;
  // Full per-action validation is deferred to the Phase 2 executor; the
  // verb-level check happens in validateStep so we can report the step.
}

/** Validate the ordered steps array of a scenario. Returns steps or a message. */
function validateSteps(raw: unknown): { steps?: ScenarioStep[]; error?: string } {
  if (!Array.isArray(raw) || raw.length === 0) {
  return { error: "Field \"steps\" must be a non-empty array." };
  }
  const steps: ScenarioStep[] = [];
  const seenIds = new Set<string>();
  for (const [i, rawStep] of raw.entries()) {
    if (!isRecord(rawStep)) {
      return { error: `steps[${i}] must be an object.` };
    }
    const id = asString(rawStep.id);
    if (!id) return { error: `steps[${i}]: ${missingField("id")}` };
    if (seenIds.has(id)) {
      return { error: `steps[${i}]: duplicate step id "${id}".` };
    }
    seenIds.add(id);

    const type = asString(rawStep.type) as StepType | undefined;
    if (!type) return { error: `steps[${i}] ("${id}"): ${missingField("type")}` };
    if (!STEP_TYPES.includes(type)) {
      return { error: `steps[${i}] ("${id}"): ${unknownValue("step type", type, STEP_TYPES)}` };
    }

    const step: ScenarioStep = { id, type };
    if (typeof rawStep.title === "string") step.title = rawStep.title;
    if (typeof rawStep.body === "string") step.body = rawStep.body;
    if (Array.isArray(rawStep.items)) {
      const items = asStringArray(rawStep.items);
      if (!items) return { error: `steps[${i}] ("${id}"): "items" must be strings.` };
      step.items = items;
    }
    if (typeof rawStep.tool === "string") step.tool = rawStep.tool;
    if (rawStep.payload !== undefined) step.payload = rawStep.payload;
    if (typeof rawStep.docPath === "string") step.docPath = rawStep.docPath;

    // `setup` steps carry an ordered `actions` list. We validate each
    // action's `action` verb here (config-action drift guard). Params
    // are passed through opaquely — the Phase 2 executor validates them.
    if (type === "setup") {
      const rawActions = rawStep.actions;
      if (!Array.isArray(rawActions) || rawActions.length === 0) {
        return { error: `steps[${i}] ("${id}"): setup step needs a non-empty "actions" array.` };
      }
      const actions: SetupAction[] = [];
      for (const [j, rawAction] of rawActions.entries()) {
        if (!isRecord(rawAction)) {
          return { error: `steps[${i}] ("${id}"): actions[${j}] must be an object.` };
        }
        const verb = asString(rawAction.action) as ActionType | undefined;
        if (!verb) {
          return { error: `steps[${i}] ("${id}"): actions[${j}]: ${missingField("action")}` };
        }
        if (!ACTION_TYPES.includes(verb)) {
          return {
            error: `steps[${i}] ("${id}"): actions[${j}]: ${unknownValue("action type", verb, ACTION_TYPES)}`,
          };
        }
        actions.push(validateAction(rawAction, id, j)!);
      }
      step.actions = actions;
    }

    steps.push(step);
  }
  return { steps };
}

/** Validate + parse a single scenario document. */
export function parseScenario(
  source: string,
  raw: unknown,
): { scenario?: Scenario; error?: string } {
  if (!isRecord(raw)) {
    return { error: "Scenario root must be an object." };
  }

  const id = asString(raw.id);
  if (!id) return { error: missingField("id") };

  const title = asString(raw.title);
  if (!title) return { error: missingField("title") };

  const milestone = asString(raw.milestone);
  if (!milestone) return { error: missingField("milestone") };

  const engineId = asString(raw.engineId);
  if (!engineId) return { error: missingField("engineId") };

  if (typeof raw.order !== "number" || !Number.isFinite(raw.order)) {
    return { error: missingField("order") + " (must be a number.)" };
  }

  const levelStr = asString(raw.requirementLevel) as RequirementLevel | undefined;
  if (!levelStr) return { error: missingField("requirementLevel") };
  if (!REQUIREMENT_LEVELS.includes(levelStr)) {
    return { error: unknownValue("requirementLevel", levelStr, REQUIREMENT_LEVELS) };
  }

  const stepsRes = validateSteps(raw.steps);
  if (stepsRes.error) return { error: stepsRes.error };

  const scenario: Scenario = {
    id,
    title,
    milestone,
    engineId,
    order: raw.order,
    requirementLevel: levelStr,
    steps: stepsRes.steps!,
  };
  if (Array.isArray(raw.tags)) {
    const tags = asStringArray(raw.tags);
    if (tags) scenario.tags = tags;
  }
  if (Array.isArray(raw.automatedCoverage)) {
    const cov = asStringArray(raw.automatedCoverage);
    if (cov) scenario.automatedCoverage = cov;
  }
  if (isRecord(raw.reset)) {
    const afterStep: Scenario["reset"] = {};
    const rawAfter = raw.reset.afterStep;
    if (isRecord(rawAfter)) {
      const resolved: Record<string, { actions: SetupAction[] }> = {};
      for (const [stepId, val] of Object.entries(rawAfter)) {
        if (!isRecord(val) || !Array.isArray(val.actions)) continue;
        const actions: SetupAction[] = [];
        for (const ra of val.actions) {
          if (!isRecord(ra)) continue;
          const verb = asString(ra.action) as ActionType | undefined;
          if (!verb || !ACTION_TYPES.includes(verb)) continue;
          actions.push(ra as SetupAction);
        }
        resolved[stepId] = { actions };
      }
      afterStep.afterStep = resolved;
    }
    if (typeof raw.reset.note === "string") afterStep.note = raw.reset.note;
    if (Object.keys(afterStep).length > 0) scenario.reset = afterStep;
  }
  return { scenario };
}

/**
 * Parse a bundle of scenario documents. Each entry is
 * `{ source, content }` where `content` is the parsed JSON (the caller
 * decides how to read+parse; this keeps the loader pure).
 *
 * Scenarios with valid but duplicate ids across files are reported as
 * errors and dropped from the returned list (id uniqueness is a
 * scenario-model invariant — idea.md).
 */
export function loadScenarios(
  files: { source: string; content: unknown }[],
): ScenarioLoadResult {
  const scenarios: Scenario[] = [];
  const errors: ScenarioLoadError[] = [];
  const seenIds = new Map<string, string>(); // id → first source

  for (const { source, content } of files) {
    const { scenario, error } = parseScenario(source, content);
    if (error) {
      errors.push({ source, message: error });
      continue;
    }
    const prev = seenIds.get(scenario!.id);
    if (prev !== undefined) {
      errors.push({
        source,
        message: `Duplicate scenario id "${scenario!.id}" (first seen in ${prev}).`,
      });
      continue;
    }
    seenIds.set(scenario!.id, source);
    scenarios.push(scenario!);
  }

  // Stable ordering: milestone → order → id (UI mirrors this).
  scenarios.sort((a, b) => {
    if (a.milestone !== b.milestone) return a.milestone.localeCompare(b.milestone);
    if (a.order !== b.order) return a.order - b.order;
    return a.id.localeCompare(b.id);
  });

  return { scenarios, errors };
}

/**
 * Validate an engine profile document. Mirrors the frozen Unity profile
 * contract (unity.md). Returns the typed profile or throws with a
 * readable message — profiles are bundled, so a bad profile is a build
 * error rather than a per-file UI error.
 */
export function parseProfile(raw: unknown): EngineProfile {
  if (!isRecord(raw)) throw new Error("Engine profile root must be an object.");
  const id = asString(raw.id);
  if (!id) throw new Error(missingField("id"));
  const displayName = asString(raw.displayName);
  if (!displayName) throw new Error(missingField("displayName"));
  const mcpCliBinary = asString(raw.mcpCliBinary);
  if (!mcpCliBinary) throw new Error(missingField("mcpCliBinary"));

  const pathsRaw = raw.paths;
  if (!isRecord(pathsRaw)) throw new Error(missingField("paths"));
  for (const key of ["fixtureRoot", "stateRoot", "stateFile", "actualsDir", "exportsDir"]) {
    if (!asString((pathsRaw as Record<string, unknown>)[key])) {
      throw new Error(`paths.${key} must be a non-empty string.`);
    }
  }

  const markersRaw = raw.markers;
  if (!isRecord(markersRaw)) throw new Error(missingField("markers"));
  if (!Array.isArray(markersRaw.dirs)) throw new Error("markers.dirs must be an array.");
  if (!Array.isArray(markersRaw.files)) throw new Error("markers.files must be an array.");

  const companionsRaw = raw.companions;
  if (!Array.isArray(companionsRaw)) throw new Error(missingField("companions"));
  const companions = companionsRaw.map((c, i) => {
    if (!isRecord(c) || !asString(c.primary) || !asString(c.companion)) {
      throw new Error(`companions[${i}] must have "primary" and "companion" strings.`);
    }
    return { primary: c.primary as string, companion: c.companion as string };
  });

  const placeholdersRaw = raw.placeholders;
  if (!Array.isArray(placeholdersRaw)) throw new Error(missingField("placeholders"));

  const toolNamePrefix = asString(raw.toolNamePrefix) ?? "unity_open_mcp_";

  return {
    id,
    displayName,
    mcpCliBinary,
    paths: {
      fixtureRoot: pathsRaw.fixtureRoot as string,
      stateRoot: pathsRaw.stateRoot as string,
      stateFile: pathsRaw.stateFile as string,
      actualsDir: pathsRaw.actualsDir as string,
      exportsDir: pathsRaw.exportsDir as string,
    },
    markers: {
      dirs: (markersRaw.dirs as unknown[]).filter((x): x is string => typeof x === "string"),
      files: (markersRaw.files as unknown[]).filter((x): x is string => typeof x === "string"),
    },
    companions,
    placeholders: placeholdersRaw.filter(
      (x): x is "{fixtureRoot}" | "{projectRoot}" => x === "{fixtureRoot}" || x === "{projectRoot}",
    ),
    toolNamePrefix,
  };
}
