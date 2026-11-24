// Playtest scenarios — the replayable form of a flow (unity_open_mcp_playtest_replay).
//
// A scenario is one JSON file in the project, by default
// <project>/Playtests/Scenarios/<name>.json: outside Assets/ (Unity does not
// import it), versioned and reviewed with the code it checks. It is written by
// `export` (from a session's recorded calls or a UI crawl), rewritten by a
// `record` run (the baseline), or written by hand in the same format.
//
// A step acts (tap / type / wait / command / input / restart) or only looks
// (check), and may carry what the screen must look like afterwards (`expect`):
// the screen's signature (the crawler's control / text set, numbers masked),
// visible texts, and the detector findings of that screen. Targets are named by
// hierarchy path and label, never by instance id (ids change every play
// session). `${name}` in a typed text or in step arguments is a variable the
// run fills in (`vars`); a secret variable is never written to the session log.
import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, statSync, writeFileSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import type { RestartMode } from "./explore.js";

export const SCENARIO_FORMAT = "unity-open-mcp.playtest-scenario/1";
/** Default scenario folder, relative to the Unity project root. */
export const SCENARIO_DIR = "Playtests/Scenarios";
export const SCENARIO_NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$/;
export const STEP_KINDS = ["tap", "type", "wait", "command", "input", "restart", "check"] as const;
export type StepKind = typeof STEP_KINDS[number];
export const SCENARIO_LIMITS = { max_steps: 300, max_signature: 400, max_wait_ms: 40_000, max_file_bytes: 4 * 1024 * 1024 } as const;
export const SCENARIO_DEFAULTS = { same_screen: 0.8, step_timeout_ms: 5_000, routing: "dispatch" as const };

/** inputsim tools an `input` step may replay as sent (taps and typing have their own steps). */
export const REPLAY_INPUT_TOOLS = new Set([
  "unity_open_mcp_inputsim_pointer", "unity_open_mcp_inputsim_touch", "unity_open_mcp_inputsim_key",
  "unity_open_mcp_inputsim_gesture", "unity_open_mcp_inputsim_step", "unity_open_mcp_inputsim_pointer3d",
]);

export interface ScenarioTarget {
  /** Hierarchy path (BuildPath form: root/child/...). */
  path?: string;
  /** The control's label as ui_snapshot shows it. */
  text?: string;
  role?: string;
  /** Which of several equal matches (snapshot order, 0-based). */
  index?: number;
}

export interface ScreenExpectation { title?: string; signature: string[] }

export interface StepExpectation {
  screen?: ScreenExpectation;
  /** Visible texts that must be on screen (case-insensitive substring). */
  texts?: string[];
  /** Detector finding keys of the screen after the step (the baseline). */
  findings?: string[];
}

export interface ScenarioStep {
  do: StepKind;
  label?: string;
  /** The session mark this step ended (export), shown in the report. */
  checkpoint?: string;
  target?: ScenarioTarget;
  /** type: what to type (`${var}` allowed). */
  text?: string;
  mode?: "replace" | "append" | "clear";
  end?: "end_edit" | "submit" | "none";
  secret?: boolean;
  /** tap: overrides settings.routing. */
  routing?: "dispatch" | "raycast_real";
  /** command: project command id. */
  command?: string;
  /** input: the inputsim tool. */
  tool?: string;
  /** wait: playtest_wait arguments; command: the command's arguments; input: the tool's arguments. */
  args?: Record<string, unknown>;
  expect?: StepExpectation;
  /** false = no visual comparison for this step (an animated screen). */
  visual?: false;
}

export interface ScenarioStart {
  restart: RestartMode;
  command?: string;
  args?: Record<string, unknown>;
  /** The first screen after the restart. */
  screen?: ScreenExpectation;
  findings?: string[];
}

export interface ScenarioSettings {
  same_screen: number;
  step_timeout_ms: number;
  routing: "dispatch" | "raycast_real";
  /** Console messages (regex, first line) that do not fail a step. */
  ignore_errors: string[];
  /** Present = every step is also compared as a picture with its visual baseline. */
  visual?: VisualSettings;
}

export interface VisualSettings {
  /** Share of compared pixels (in blocks over the noise floor) that may differ. */
  threshold: number;
  /** Per-channel difference a pixel may have and still match. */
  pixel_tolerance: number;
  /** Capture size (longest edge). */
  max_edge: number;
  /** Hierarchy paths / suffixes, or normalized [x, y, w, h] boxes, not compared. */
  mask: Array<string | number[]>;
  /** Mask visible texts with digits (counters, timers). */
  mask_numbers: boolean;
}

export const VISUAL_SETTING_DEFAULTS: VisualSettings = { threshold: 0.0002, pixel_tolerance: 24, max_edge: 640, mask: [], mask_numbers: true };

/** Normalized visual settings, or null with problems pushed. */
export function parseVisualSettings(value: unknown, where: string, problems: string[]): VisualSettings | null {
  if (value === true) return { ...VISUAL_SETTING_DEFAULTS, mask: [] };
  if (!isObject(value)) { problems.push(`${where} must be an object (threshold, pixel_tolerance, max_edge, mask, mask_numbers) or true`); return null; }
  const out: VisualSettings = { ...VISUAL_SETTING_DEFAULTS, mask: [] };
  const range = (key: "threshold" | "pixel_tolerance" | "max_edge", min: number, max: number) => {
    const v = value[key];
    if (v === undefined) return;
    if (typeof v !== "number" || !(v >= min && v <= max)) problems.push(`${where}.${key} must be ${min}..${max}`);
    else out[key] = v;
  };
  range("threshold", 0, 0.5);
  range("pixel_tolerance", 0, 255);
  range("max_edge", 128, 2048);
  if (value.mask_numbers !== undefined) out.mask_numbers = value.mask_numbers !== false;
  if (value.mask !== undefined) {
    if (!Array.isArray(value.mask)) problems.push(`${where}.mask must be an array of paths or [x, y, w, h] boxes (0–1)`);
    else for (const m of value.mask) {
      if (typeof m === "string" && m) out.mask.push(m);
      else if (Array.isArray(m) && m.length === 4 && m.every(n => typeof n === "number" && n >= 0 && n <= 1)) out.mask.push(m as number[]);
      else problems.push(`${where}.mask: ${JSON.stringify(m)} is neither a path nor a normalized [x, y, w, h]`);
    }
  }
  return out;
}

export interface Scenario {
  format: string;
  name: string;
  description?: string;
  source?: Record<string, unknown>;
  start: ScenarioStart;
  settings: ScenarioSettings;
  vars: Record<string, { secret?: boolean; description?: string }>;
  steps: ScenarioStep[];
}

export class ScenarioError extends Error {
  constructor(readonly code: string, message: string, readonly detail: Record<string, unknown> = {}) { super(message); }
}

const isObject = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);
const str = (value: unknown) => (typeof value === "string" ? value : undefined);

function parseScreen(value: unknown, where: string, problems: string[]): ScreenExpectation | undefined {
  if (value === undefined) return undefined;
  if (!isObject(value) || !Array.isArray(value.signature) || value.signature.some(t => typeof t !== "string")) {
    problems.push(`${where}: screen needs signature (an array of strings)`);
    return undefined;
  }
  if (value.signature.length > SCENARIO_LIMITS.max_signature) problems.push(`${where}: screen signature has over ${SCENARIO_LIMITS.max_signature} entries`);
  return { ...(typeof value.title === "string" ? { title: value.title } : {}), signature: value.signature as string[] };
}

function parseStrings(value: unknown, where: string, problems: string[]): string[] | undefined {
  if (value === undefined) return undefined;
  if (!Array.isArray(value) || value.some(t => typeof t !== "string")) { problems.push(`${where} must be an array of strings`); return undefined; }
  return value as string[];
}

function parseTarget(value: unknown, where: string, problems: string[]): ScenarioTarget | undefined {
  if (!isObject(value)) { problems.push(`${where}: target is required ({path, text, role, index})`); return undefined; }
  const target: ScenarioTarget = {};
  if (typeof value.path === "string" && value.path) target.path = value.path;
  if (typeof value.text === "string" && value.text) target.text = value.text;
  if (typeof value.role === "string" && value.role) target.role = value.role;
  if (value.index !== undefined) {
    if (typeof value.index !== "number" || !Number.isInteger(value.index) || value.index < 0) problems.push(`${where}: target.index must be a non-negative integer`);
    else target.index = value.index;
  }
  if (!target.path && !target.text) problems.push(`${where}: target needs path or text`);
  if (value.object_id !== undefined) problems.push(`${where}: target.object_id is not allowed (instance ids change every play session) — use path / text`);
  return target;
}

/** Validates and normalizes a scenario; `problems` lists what is wrong (empty = valid). */
export function parseScenario(value: unknown): { scenario: Scenario | null; problems: string[] } {
  const problems: string[] = [];
  if (!isObject(value)) return { scenario: null, problems: ["a scenario is a JSON object"] };
  if (value.format !== SCENARIO_FORMAT) problems.push(`format must be "${SCENARIO_FORMAT}"`);
  const name = str(value.name)?.trim() || "scenario";
  const startIn = isObject(value.start) ? value.start : {};
  const restart = (startIn.restart ?? "play_mode") as RestartMode;
  if (!["play_mode", "command", "none"].includes(restart)) problems.push("start.restart must be play_mode, command or none");
  if (restart === "command" && !(typeof startIn.command === "string" && startIn.command.startsWith("project.")))
    problems.push("start.restart command needs start.command (a project.* command id)");
  const start: ScenarioStart = {
    restart,
    ...(typeof startIn.command === "string" ? { command: startIn.command } : {}),
    ...(isObject(startIn.args) ? { args: startIn.args } : {}),
  };
  const startScreen = parseScreen(startIn.screen, "start", problems);
  if (startScreen) start.screen = startScreen;
  const startFindings = parseStrings(startIn.findings, "start.findings", problems);
  if (startFindings) start.findings = startFindings;

  const settingsIn = isObject(value.settings) ? value.settings : {};
  const sameScreen = typeof settingsIn.same_screen === "number" ? settingsIn.same_screen : SCENARIO_DEFAULTS.same_screen;
  if (!(sameScreen >= 0.5 && sameScreen <= 1)) problems.push("settings.same_screen must be between 0.5 and 1");
  const stepTimeout = typeof settingsIn.step_timeout_ms === "number" ? settingsIn.step_timeout_ms : SCENARIO_DEFAULTS.step_timeout_ms;
  if (!(stepTimeout >= 500 && stepTimeout <= 60_000)) problems.push("settings.step_timeout_ms must be between 500 and 60000");
  const routing = settingsIn.routing === "raycast_real" ? "raycast_real" : "dispatch";
  const ignoreErrors = parseStrings(settingsIn.ignore_errors, "settings.ignore_errors", problems) ?? [];
  const visual = settingsIn.visual === undefined || settingsIn.visual === false ? null : parseVisualSettings(settingsIn.visual, "settings.visual", problems);
  for (const pattern of ignoreErrors) {
    try { new RegExp(pattern); } catch { problems.push(`settings.ignore_errors: '${pattern}' is not a valid regular expression`); }
  }

  const vars: Scenario["vars"] = {};
  if (value.vars !== undefined) {
    if (!isObject(value.vars)) problems.push("vars must be an object {name: {secret, description}}");
    else for (const [key, spec] of Object.entries(value.vars)) {
      if (!/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(key)) { problems.push(`vars: '${key}' is not a variable name`); continue; }
      const s = isObject(spec) ? spec : {};
      vars[key] = { ...(s.secret === true ? { secret: true } : {}), ...(typeof s.description === "string" ? { description: s.description } : {}) };
    }
  }

  const steps: ScenarioStep[] = [];
  const rawSteps = Array.isArray(value.steps) ? value.steps : null;
  if (!rawSteps) problems.push("steps must be an array");
  else if (rawSteps.length > SCENARIO_LIMITS.max_steps) problems.push(`at most ${SCENARIO_LIMITS.max_steps} steps`);
  (rawSteps ?? []).slice(0, SCENARIO_LIMITS.max_steps).forEach((raw, i) => {
    const where = `steps[${i}]`;
    if (!isObject(raw)) { problems.push(`${where} is not an object`); return; }
    const kind = raw.do as StepKind;
    if (!STEP_KINDS.includes(kind)) { problems.push(`${where}: do must be one of ${STEP_KINDS.join(", ")}`); return; }
    const step: ScenarioStep = { do: kind };
    if (typeof raw.label === "string") step.label = raw.label;
    if (typeof raw.checkpoint === "string") step.checkpoint = raw.checkpoint;
    switch (kind) {
      case "tap": {
        const target = parseTarget(raw.target, where, problems);
        if (target) step.target = target;
        if (raw.routing === "dispatch" || raw.routing === "raycast_real") step.routing = raw.routing;
        break;
      }
      case "type": {
        const target = parseTarget(raw.target, where, problems);
        if (target) step.target = target;
        const mode = raw.mode ?? "replace";
        if (!["replace", "append", "clear"].includes(String(mode))) problems.push(`${where}: mode must be replace, append or clear`);
        else step.mode = mode as ScenarioStep["mode"];
        const end = raw.end ?? "end_edit";
        if (!["end_edit", "submit", "none"].includes(String(end))) problems.push(`${where}: end must be end_edit, submit or none`);
        else step.end = end as ScenarioStep["end"];
        if (typeof raw.text === "string") step.text = raw.text;
        else if (mode !== "clear") problems.push(`${where}: type needs text (unless mode is clear)`);
        if (raw.secret === true) step.secret = true;
        break;
      }
      case "wait": {
        if (!isObject(raw.args) || !isObject(raw.args.condition)) { problems.push(`${where}: wait needs args.condition (playtest_wait's condition)`); break; }
        const timeout = raw.args.timeout_ms;
        if (timeout !== undefined && !(typeof timeout === "number" && timeout >= 1 && timeout <= SCENARIO_LIMITS.max_wait_ms))
          problems.push(`${where}: args.timeout_ms must be 1..${SCENARIO_LIMITS.max_wait_ms} (a replay wait runs inline)`);
        step.args = raw.args;
        break;
      }
      case "command": {
        if (typeof raw.command !== "string" || !raw.command.startsWith("project.")) problems.push(`${where}: command needs a project.* command id`);
        else step.command = raw.command;
        if (raw.args !== undefined && !isObject(raw.args)) problems.push(`${where}: args must be an object`);
        else if (raw.args) step.args = raw.args;
        break;
      }
      case "input": {
        if (typeof raw.tool !== "string" || !REPLAY_INPUT_TOOLS.has(raw.tool)) problems.push(`${where}: tool must be one of ${[...REPLAY_INPUT_TOOLS].join(", ")}`);
        else step.tool = raw.tool;
        if (!isObject(raw.args)) problems.push(`${where}: input needs args (the tool's arguments)`);
        else step.args = raw.args;
        break;
      }
      case "restart":
      case "check":
        break;
    }
    if (raw.visual === false) step.visual = false;
    else if (raw.visual !== undefined) problems.push(`${where}: visual may only be false (skip the picture check)`);
    if (raw.expect !== undefined) {
      if (!isObject(raw.expect)) problems.push(`${where}: expect must be an object`);
      else {
        const expect: StepExpectation = {};
        const screen = parseScreen(raw.expect.screen, where, problems);
        if (screen) expect.screen = screen;
        const texts = parseStrings(raw.expect.texts, `${where}.expect.texts`, problems);
        if (texts) expect.texts = texts;
        const findings = parseStrings(raw.expect.findings, `${where}.expect.findings`, problems);
        if (findings) expect.findings = findings;
        if (Object.keys(expect).length) step.expect = expect;
      }
    }
    steps.push(step);
  });
  const scenario: Scenario = {
    format: SCENARIO_FORMAT,
    name,
    ...(typeof value.description === "string" ? { description: value.description } : {}),
    ...(isObject(value.source) ? { source: value.source } : {}),
    start,
    settings: { same_screen: sameScreen, step_timeout_ms: stepTimeout, routing, ignore_errors: ignoreErrors, ...(visual ? { visual } : {}) },
    vars,
    steps,
  };
  // Variables used but not declared are an error a run would hit midway.
  for (const name of usedVars(scenario)) if (!(name in vars)) problems.push(`\${${name}} is used but not declared in vars`);
  return { scenario, problems };
}

// ---------------------------------------------------------------------------
// Variables
// ---------------------------------------------------------------------------

const VAR_PATTERN = /\$\{([A-Za-z_][A-Za-z0-9_]{0,63})\}/g;

function collectVars(value: unknown, into: Set<string>): void {
  if (typeof value === "string") { for (const m of value.matchAll(VAR_PATTERN)) into.add(m[1]); return; }
  if (Array.isArray(value)) { value.forEach(v => collectVars(v, into)); return; }
  if (isObject(value)) for (const v of Object.values(value)) collectVars(v, into);
}

/** Variable names the steps use (`${name}` in typed text and step arguments). */
export function usedVars(scenario: Pick<Scenario, "steps" | "start">): string[] {
  const names = new Set<string>();
  collectVars(scenario.start.args, names);
  for (const step of scenario.steps) { collectVars(step.text, names); collectVars(step.args, names); }
  return [...names];
}

/** `value` with every `${name}` replaced from `vars` (strings only; other values as they are). */
export function fillVars<T>(value: T, vars: Record<string, string>): T {
  if (typeof value === "string") return value.replace(VAR_PATTERN, (whole, name: string) => (name in vars ? vars[name] : whole)) as T;
  if (Array.isArray(value)) return value.map(v => fillVars(v, vars)) as T;
  if (isObject(value)) return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, fillVars(v, vars)])) as T;
  return value;
}

/** Whether a step's typed text uses a secret variable. */
export function usesSecret(step: ScenarioStep, scenario: Pick<Scenario, "vars">): boolean {
  const names = new Set<string>();
  collectVars(step.text, names);
  collectVars(step.args, names);
  return [...names].some(n => scenario.vars[n]?.secret === true);
}

// ---------------------------------------------------------------------------
// Files
// ---------------------------------------------------------------------------

export function scenarioDir(projectPath: string): string {
  return join(projectPath, ...SCENARIO_DIR.split("/"));
}

function inside(root: string, path: string): boolean {
  const rel = relative(resolve(root), resolve(path));
  return rel !== "" && !rel.startsWith("..") && !isAbsolute(rel);
}

/**
 * The file a `scenario` argument names: a bare name → <project>/Playtests/Scenarios/<name>.json;
 * a path ending in .json → relative to the project (or absolute). `forWrite` keeps it inside the project.
 */
export function resolveScenarioPath(projectPath: string, ref: string, forWrite = false): string {
  const value = ref.trim();
  if (!value) throw new ScenarioError("invalid_arguments", "scenario is empty.");
  let file: string;
  if (/\.json$/i.test(value) || value.includes("/") || value.includes("\\")) {
    if (!/\.json$/i.test(value)) throw new ScenarioError("invalid_arguments", `'${value}' is not a .json file; name a scenario by its name or a .json path.`);
    file = isAbsolute(value) ? value : resolve(projectPath, value);
  } else {
    if (!SCENARIO_NAME_PATTERN.test(value))
      throw new ScenarioError("invalid_arguments", `'${value}' is not a scenario name (letters, digits, . _ -; up to 80 characters).`);
    file = join(scenarioDir(projectPath), `${value}.json`);
  }
  if (forWrite && !inside(projectPath, file))
    throw new ScenarioError("invalid_arguments", `Scenarios are written inside the project; '${value}' is outside ${projectPath}.`);
  return file;
}

export function readScenarioFile(file: string): { scenario: Scenario; problems: string[]; raw: unknown } {
  if (!existsSync(file)) throw new ScenarioError("scenario_not_found", `No scenario file at ${file}.`, { file });
  const size = statSync(file).size;
  if (size > SCENARIO_LIMITS.max_file_bytes) throw new ScenarioError("scenario_invalid", `${file} is ${size} bytes; a scenario is at most ${SCENARIO_LIMITS.max_file_bytes}.`);
  let raw: unknown;
  try { raw = JSON.parse(readFileSync(file, "utf8")); }
  catch (error) { throw new ScenarioError("scenario_invalid", `${file} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`, { file }); }
  const { scenario, problems } = parseScenario(raw);
  if (!scenario) throw new ScenarioError("scenario_invalid", `${file}: ${problems.join("; ")}`, { file, problems });
  return { scenario, problems, raw };
}

/** Writes the scenario (pretty JSON, trailing newline) through a temp file + rename. */
export function writeScenarioFile(file: string, scenario: Scenario): void {
  mkdirSync(dirname(file), { recursive: true });
  const temp = `${file}.${process.pid}.tmp`;
  writeFileSync(temp, `${JSON.stringify(scenario, null, 2)}\n`);
  renameSync(temp, file);
}

/** Steps whose screen after the step has no baseline yet (only acting steps; a check looks at the screen as it is). */
export function unbaselinedSteps(scenario: Scenario): number {
  return scenario.steps.filter(s => !s.expect?.screen).length;
}

export interface ScenarioListing {
  name: string;
  file: string;
  steps: number;
  description?: string;
  source?: string;
  unbaselined: number;
  vars: string[];
  problems?: string[];
}

export function listScenarios(projectPath: string): ScenarioListing[] {
  const dir = scenarioDir(projectPath);
  if (!existsSync(dir)) return [];
  const out: ScenarioListing[] = [];
  for (const entry of readdirSync(dir).filter(f => f.toLowerCase().endsWith(".json")).sort()) {
    const file = join(dir, entry);
    const name = entry.replace(/\.json$/i, "");
    try {
      const { scenario, problems } = readScenarioFile(file);
      out.push({
        name, file, steps: scenario.steps.length,
        ...(scenario.description ? { description: scenario.description } : {}),
        ...(typeof scenario.source?.kind === "string" ? { source: scenario.source.kind } : {}),
        unbaselined: unbaselinedSteps(scenario), vars: Object.keys(scenario.vars),
        ...(problems.length ? { problems: problems.slice(0, 5) } : {}),
      });
    } catch (error) {
      out.push({ name, file, steps: 0, unbaselined: 0, vars: [], problems: [error instanceof Error ? error.message : String(error)] });
    }
  }
  return out;
}

/** A project-relative display path (forward slashes) when the file is inside the project. */
export function displayPath(projectPath: string, file: string): string {
  return inside(projectPath, file) ? relative(resolve(projectPath), resolve(file)).split(sep).join("/") : file;
}
