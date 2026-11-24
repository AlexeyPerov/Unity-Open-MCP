// Playtest — project memory: what earlier playtests of this project learned.
//
// Two files in the Unity project, next to the replay scenarios, shared and
// versioned with the code, written only on request (opt-in):
//
//   Playtests/profile.json — settings the playtest tools apply on their own:
//     `explore` (playtest_explore arguments), `devices` (the team's devices),
//     `detectors` (placeholder pattern, minimum touch size — for every detector
//     run), `back_labels` (labels that leave a screen, e.g. localised "Back"),
//     `ignore_errors` (expected console noise) and `known` (findings the team
//     accepted: listed as known, not counted). Sections are validated against
//     the tools' own schemas. Precedence: call arguments > profile section >
//     profile detectors > built-in defaults; deny / allow / ignore_errors add up.
//   Playtests/NOTES.md — prose for the agent and the team: how to boot, the
//     screen map, dangerous controls, known issues, flaky spots. Session start
//     returns its head; `note` appends dated bullets, `map` writes the screen map
//     of a crawl as a generated section.
//
// Pure file I/O; the router (local tool unity_open_mcp_playtest_memory) and the
// job glue call it. An invalid profile is refused by every tool that applies it.
import { existsSync, mkdirSync, readFileSync, renameSync, statSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { validateSchema } from "../tool-contract.js";
import { playtestDevices } from "../tools/playtest-devices.js";
import { playtestExplore } from "../tools/playtest-explore.js";
import { FINDING_SEVERITY, type Finding, type FindingKind } from "./detectors.js";

export const PROFILE_FORMAT = "unity-open-mcp.playtest-profile/1";
export const PROFILE_FILE = "Playtests/profile.json";
export const NOTES_FILE = "Playtests/NOTES.md";
export const SCREEN_MAP_SECTION = "Screen map";
export const MEMORY_LIMITS = {
  max_file_bytes: 256 * 1024, max_known: 200, max_list: 50, note_chars: 2_000, start_notes_chars: 4_000, show_chars: 20_000,
  map_screens: 60,
} as const;

export class MemoryError extends Error {
  constructor(readonly code: string, message: string, readonly detail: Record<string, unknown> = {}) {
    super(message);
  }
}

export interface KnownRule {
  kind: FindingKind;
  /** Hierarchy path of the element, or its tail ("Settings/Close"). */
  path?: string;
  /** Words of the finding's label or detail (case-insensitive). */
  text?: string;
  /** Part of the Device Simulator device id (device matrix findings). */
  device?: string;
  /** Why it is not a bug. */
  reason: string;
}

export interface PlaytestProfile {
  format?: string;
  explore?: Record<string, unknown>;
  devices?: Record<string, unknown>;
  detectors?: { placeholder_pattern?: string; min_touch_dp?: number };
  back_labels?: string[];
  ignore_errors?: string[];
  known?: KnownRule[];
}

// ---------------------------------------------------------------------------
// Schema (the tools' own argument schemas, so the profile cannot drift)
// ---------------------------------------------------------------------------

type Schema = Record<string, any>;
const exploreProps = playtestExplore.inputSchema.properties as Record<string, Schema>;
const devicesProps = playtestDevices.inputSchema.properties as Record<string, Schema>;
const pick = (props: Record<string, Schema>, keys: string[]) => Object.fromEntries(keys.map(k => [k, props[k]]));

/** playtest_explore arguments a profile may set (every one but allow_unsandboxed: that stays a per-call decision). */
export const PROFILE_EXPLORE_KEYS = Object.keys(exploreProps).filter(k => k !== "allow_unsandboxed");
export const PROFILE_DEVICES_KEYS = ["devices", "preset", "max_devices", "orientations", "settle_timeout_ms"];
export const PROFILE_DETECTOR_KEYS = ["placeholder_pattern", "min_touch_dp"];
const FINDING_KINDS = Object.keys(FINDING_SEVERITY) as FindingKind[];

const stringList = (max: number, maxLength: number) => ({
  type: "array", maxItems: max, items: { type: "string", minLength: 1, maxLength },
});

export const PROFILE_SCHEMA: Schema = {
  type: "object",
  additionalProperties: false,
  properties: {
    format: { type: "string", enum: [PROFILE_FORMAT] },
    explore: { type: "object", additionalProperties: false, properties: pick(exploreProps, PROFILE_EXPLORE_KEYS) },
    devices: { type: "object", additionalProperties: false, properties: pick(devicesProps, PROFILE_DEVICES_KEYS) },
    detectors: { type: "object", additionalProperties: false, properties: pick(exploreProps, PROFILE_DETECTOR_KEYS) },
    back_labels: stringList(MEMORY_LIMITS.max_list, 60),
    ignore_errors: stringList(MEMORY_LIMITS.max_list, 300),
    known: {
      type: "array", maxItems: MEMORY_LIMITS.max_known,
      items: {
        type: "object", additionalProperties: false, required: ["kind", "reason"],
        properties: {
          kind: { type: "string", enum: FINDING_KINDS },
          path: { type: "string", minLength: 1, maxLength: 300 },
          text: { type: "string", minLength: 1, maxLength: 200 },
          device: { type: "string", minLength: 1, maxLength: 100 },
          reason: { type: "string", minLength: 1, maxLength: 300 },
        },
      },
    },
  },
};

const isObject = (value: unknown): value is Record<string, any> => !!value && typeof value === "object" && !Array.isArray(value);

function regexProblem(where: string, source: unknown): string | null {
  if (typeof source !== "string") return null;
  try { new RegExp(source); return null; }
  catch (error) { return `${where} '${source}' is not a valid regular expression: ${error instanceof Error ? error.message : String(error)}`; }
}

/** Everything wrong with a profile value (empty = valid). */
export function validateProfile(value: unknown): string[] {
  if (!isObject(value)) return ["profile.json must hold a JSON object."];
  const problems = validateSchema(value, PROFILE_SCHEMA, "profile");
  if (problems.length) return problems;
  const p = value as PlaytestProfile;
  const explore = p.explore ?? {};
  if (explore.restart === "command" && typeof explore.restart_command !== "string")
    problems.push("profile.explore: restart \"command\" needs restart_command (a synchronous project command such as project.<owner>.playtest.boot).");
  if (explore.restart !== undefined && explore.restart !== "command" && (explore.restart_command !== undefined || explore.restart_args !== undefined))
    problems.push("profile.explore: restart_command / restart_args are used only with restart \"command\".");
  for (const [i, source] of (Array.isArray(explore.deny) ? explore.deny : []).entries()) {
    const problem = regexProblem(`profile.explore.deny[${i}]`, source);
    if (problem) problems.push(problem);
  }
  for (const [i, source] of (p.ignore_errors ?? []).entries()) {
    const problem = regexProblem(`profile.ignore_errors[${i}]`, source);
    if (problem) problems.push(problem);
  }
  for (const where of ["explore", "detectors"] as const) {
    const problem = regexProblem(`profile.${where}.placeholder_pattern`, (p[where] as Record<string, unknown> | undefined)?.placeholder_pattern || undefined);
    if (problem) problems.push(problem);
  }
  if (p.devices?.devices !== undefined && p.devices?.preset !== undefined)
    problems.push("profile.devices: set devices or preset, not both.");
  for (const [i, rule] of (p.known ?? []).entries())
    if (!rule.path && !rule.text)
      problems.push(`profile.known[${i}] (${rule.kind}) needs path or text: a known entry accepts one element's finding, not a whole kind (turn a check off with profile.detectors).`);
  return problems;
}

// ---------------------------------------------------------------------------
// Files
// ---------------------------------------------------------------------------

export function profilePath(projectPath: string): string { return join(projectPath, ...PROFILE_FILE.split("/")); }
export function notesPath(projectPath: string): string { return join(projectPath, ...NOTES_FILE.split("/")); }

function writeAtomic(file: string, text: string): void {
  mkdirSync(dirname(file), { recursive: true });
  const temp = `${file}.${process.pid}.tmp`;
  writeFileSync(temp, text);
  renameSync(temp, file);
}

function readLimited(file: string, what: string): string {
  const size = statSync(file).size;
  if (size > MEMORY_LIMITS.max_file_bytes)
    throw new MemoryError("memory_too_large", `${what} is ${Math.round(size / 1024)} KB; the limit is ${MEMORY_LIMITS.max_file_bytes / 1024} KB. Trim it by hand.`);
  return readFileSync(file, "utf8");
}

export interface ProfileRead {
  file: string;
  exists: boolean;
  /** The parsed value when the file is JSON (even an invalid profile). */
  raw: unknown;
  profile: PlaytestProfile | null;
  problems: string[];
}

export function readProfile(projectPath: string): ProfileRead {
  const file = profilePath(projectPath);
  if (!existsSync(file)) return { file, exists: false, raw: null, profile: null, problems: [] };
  let text: string;
  try { text = readLimited(file, PROFILE_FILE); }
  catch (error) { return { file, exists: true, raw: null, profile: null, problems: [error instanceof Error ? error.message : String(error)] }; }
  let raw: unknown;
  try { raw = JSON.parse(text); }
  catch (error) { return { file, exists: true, raw: null, profile: null, problems: [`${PROFILE_FILE} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`] }; }
  const problems = validateProfile(raw);
  return { file, exists: true, raw, profile: problems.length ? null : raw as PlaytestProfile, problems };
}

/** The project's profile for a tool that applies it: null when there is none; invalid_profile when it is broken. */
export function requireProfile(projectPath: string | undefined): PlaytestProfile | null {
  if (!projectPath) return null;
  const read = readProfile(projectPath);
  if (!read.problems.length) return read.profile;
  throw new MemoryError("invalid_profile",
    `${PROFILE_FILE} has problems, so its deny-list and settings cannot be applied: ${read.problems.slice(0, 6).join("; ")}`,
    {
      problems: read.problems,
      agentNextSteps: [`Fix ${PROFILE_FILE} (playtest_memory {"action":"set","profile":{…}} with the corrected keys, or edit the file), then call again.`],
    });
}

function writeProfile(projectPath: string, profile: PlaytestProfile): void {
  const { format: _format, ...rest } = profile;
  writeAtomic(profilePath(projectPath), `${JSON.stringify({ format: PROFILE_FORMAT, ...rest }, null, 2)}\n`);
}

// ---------------------------------------------------------------------------
// Writing the profile: set (JSON merge patch) and add (append to lists)
// ---------------------------------------------------------------------------

/** RFC 7386: objects merge, null removes, everything else (arrays included) replaces. */
export function mergePatch(target: unknown, patch: unknown): unknown {
  if (!isObject(patch)) return patch;
  const out: Record<string, unknown> = isObject(target) ? { ...target } : {};
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete out[key];
    else out[key] = mergePatch(out[key], value);
  }
  return out;
}

/** The lists `add` appends to. */
export const PROFILE_LISTS = ["explore.deny", "explore.allow", "devices.devices", "devices.orientations", "back_labels", "ignore_errors", "known"] as const;

const ruleKey = (value: unknown) => isObject(value)
  ? JSON.stringify([value.kind, value.path ?? "", value.text ?? "", value.device ?? ""]).toLowerCase()
  : JSON.stringify(value);

/** Appends the patch's list items to the profile's lists, skipping duplicates (a known entry by kind + path + text + device). */
export function addToProfile(target: unknown, patch: unknown): { value: Record<string, unknown>; added: Record<string, number> } {
  if (!isObject(patch)) throw new MemoryError("invalid_arguments", "add needs profile: an object of lists to append to.");
  const out = structuredClone(isObject(target) ? target : {}) as Record<string, any>;
  const added: Record<string, number> = {};
  const visit = (value: Record<string, unknown>, prefix: string) => {
    for (const [key, item] of Object.entries(value)) {
      const path = prefix ? `${prefix}.${key}` : key;
      const isList = (PROFILE_LISTS as readonly string[]).includes(path);
      if (!isList && isObject(item) && !prefix) { visit(item, path); continue; }
      if (!isList)
        throw new MemoryError("invalid_arguments", `add appends to lists only (${PROFILE_LISTS.join(", ")}); '${path}' is not one — use action "set" for it.`);
      if (!Array.isArray(item)) throw new MemoryError("invalid_arguments", `add: '${path}' must be an array of items to append.`);
      const parts = path.split(".");
      const holder = parts.length === 2 ? (out[parts[0]] = isObject(out[parts[0]]) ? out[parts[0]] : {}) : out;
      const leaf = parts[parts.length - 1];
      const list: unknown[] = Array.isArray(holder[leaf]) ? holder[leaf] : [];
      const seen = new Set(list.map(ruleKey));
      let n = 0;
      for (const entry of item) {
        const key = ruleKey(entry);
        if (seen.has(key)) continue;
        seen.add(key);
        list.push(entry);
        n++;
      }
      holder[leaf] = list;
      added[path] = n;
    }
  };
  visit(patch, "");
  return { value: out, added };
}

/** `set` / `add`: change the profile, validate the result, write it. Nothing is written on a problem. */
export function changeProfile(projectPath: string, action: "set" | "add", patch: unknown): Record<string, unknown> {
  if (!isObject(patch) || !Object.keys(patch).length)
    throw new MemoryError("invalid_arguments", `${action} needs profile: an object (${action === "set" ? "a JSON merge patch: objects merge, null removes a key, lists are replaced" : "lists to append to"}).`);
  const read = readProfile(projectPath);
  if (read.exists && read.raw === null)
    throw new MemoryError("invalid_profile", `${read.problems[0] ?? `${PROFILE_FILE} cannot be read`}. Fix the file by hand first.`, { problems: read.problems });
  const current = isObject(read.raw) ? read.raw : {};
  const { format: _format, ...body } = patch;
  let next: Record<string, unknown>;
  let added: Record<string, number> | undefined;
  if (action === "set") next = mergePatch(current, body) as Record<string, unknown>;
  else ({ value: next, added } = addToProfile(current, body));
  // Empty sections left by a removal are dropped.
  for (const [key, value] of Object.entries(next)) if (isObject(value) && !Object.keys(value).length) delete next[key];
  const problems = validateProfile({ ...next, format: PROFILE_FORMAT });
  if (problems.length)
    throw new MemoryError("invalid_profile", `The change would make ${PROFILE_FILE} invalid, so nothing was written: ${problems.slice(0, 6).join("; ")}`, { problems });
  writeProfile(projectPath, next as PlaytestProfile);
  return {
    file: PROFILE_FILE, created: !read.exists, ...(added ? { added } : {}),
    profile: { format: PROFILE_FORMAT, ...next }, summary: summarizeProfile(next as PlaytestProfile),
  };
}

/** One line per section, for the agent. */
export function summarizeProfile(profile: PlaytestProfile): string[] {
  const out: string[] = [];
  const keys = (section: Record<string, unknown> | undefined) => Object.entries(section ?? {})
    .map(([k, v]) => Array.isArray(v) ? `${k} (${v.length})` : typeof v === "object" ? k : `${k} ${JSON.stringify(v)}`);
  if (profile.explore && Object.keys(profile.explore).length) out.push(`explore: ${keys(profile.explore).join(", ")}`);
  if (profile.devices && Object.keys(profile.devices).length) out.push(`devices: ${keys(profile.devices).join(", ")}`);
  if (profile.detectors && Object.keys(profile.detectors).length) out.push(`detectors: ${keys(profile.detectors).join(", ")}`);
  if (profile.back_labels?.length) out.push(`back_labels: ${profile.back_labels.map(l => `'${l}'`).join(", ")}`);
  if (profile.ignore_errors?.length) out.push(`ignore_errors: ${profile.ignore_errors.length}`);
  if (profile.known?.length) out.push(`known: ${profile.known.length} accepted finding(s)`);
  return out;
}

// ---------------------------------------------------------------------------
// Applying the profile to a call's arguments
// ---------------------------------------------------------------------------

export interface WithProfile {
  args: Record<string, unknown>;
  /** What the profile supplied (for the agent and the report). */
  applied: string[];
}

const concat = (a: unknown, b: unknown) => [...(Array.isArray(a) ? a : []), ...(Array.isArray(b) ? b : [])];

/** playtest_explore arguments with the profile's explore + detectors sections under them. */
export function exploreArgsWithProfile(args: Record<string, unknown>, profile: PlaytestProfile | null, detectors = true): WithProfile {
  const out = { ...args };
  const applied: string[] = [];
  if (!profile) return { args: out, applied };
  const section = profile.explore ?? {};
  const restartGiven = args.restart !== undefined;
  for (const [key, value] of Object.entries(section)) {
    if (key === "deny" || key === "allow") continue;
    if (key === "restart" && restartGiven) continue;
    // The profile's restart command only where the run restarts by command and names none itself.
    if ((key === "restart_command" || key === "restart_args") && ((out.restart ?? section.restart) !== "command" || args.restart_command !== undefined)) continue;
    if (out[key] !== undefined) continue;
    out[key] = value;
    applied.push(`explore.${key}`);
  }
  for (const key of ["deny", "allow"] as const) {
    const listed = section[key];
    if (!Array.isArray(listed) || !listed.length) continue;
    out[key] = concat(listed, args[key]);
    applied.push(`explore.${key} (${listed.length})`);
  }
  if (detectors) applyDetectors(out, profile, applied);
  return { args: out, applied };
}

function applyDetectors(out: Record<string, unknown>, profile: PlaytestProfile, applied: string[]): void {
  for (const key of PROFILE_DETECTOR_KEYS) {
    const value = (profile.detectors as Record<string, unknown> | undefined)?.[key];
    if (value === undefined || out[key] !== undefined) continue;
    out[key] = value;
    applied.push(`detectors.${key}`);
  }
}

/** playtest_devices arguments with the profile's devices + detectors (and, for a crawl per device, explore) sections. */
export function devicesArgsWithProfile(args: Record<string, unknown>, profile: PlaytestProfile | null): WithProfile {
  const out = { ...args };
  const applied: string[] = [];
  if (!profile) return { args: out, applied };
  const section = profile.devices ?? {};
  for (const [key, value] of Object.entries(section)) {
    if ((key === "devices" || key === "preset") && (args.devices !== undefined || args.preset !== undefined)) continue;
    if (out[key] !== undefined) continue;
    out[key] = value;
    applied.push(`devices.${key}`);
  }
  applyDetectors(out, profile, applied);
  if (args.screens === "explore") {
    const given = isObject(args.explore) ? args.explore : {};
    const nested = exploreArgsWithProfile(given, profile, false);
    // The call's own detector settings beat the profile's explore section.
    for (const key of PROFILE_DETECTOR_KEYS)
      if (args[key] !== undefined && given[key] === undefined && nested.args[key] !== undefined) {
        delete nested.args[key];
        nested.applied = nested.applied.filter(a => a !== `explore.${key}`);
      }
    if (nested.applied.length) { out.explore = nested.args; applied.push(...nested.applied); }
  }
  return { args: out, applied };
}

/** Detector settings (playtest_session check, playtest_replay run) from the profile's detectors section. */
export function detectorArgsWithProfile(args: Record<string, unknown>, profile: PlaytestProfile | null): WithProfile {
  const out = { ...args };
  const applied: string[] = [];
  if (profile) applyDetectors(out, profile, applied);
  return { args: out, applied };
}

// ---------------------------------------------------------------------------
// What the engines take from the profile beyond arguments
// ---------------------------------------------------------------------------

export interface KnownMatch { reason: string }

/** The profile's accepted findings. */
export class KnownFindings {
  constructor(readonly rules: KnownRule[]) {}

  match(finding: Pick<Finding, "kind" | "path" | "label" | "detail" | "device">): KnownRule | null {
    for (const rule of this.rules) {
      if (rule.kind !== finding.kind) continue;
      if (rule.path && !pathMatches(finding.path, rule.path)) continue;
      if (rule.text && !`${finding.label ?? ""}\n${finding.detail ?? ""}`.toLowerCase().includes(rule.text.toLowerCase())) continue;
      if (rule.device && !String(finding.device ?? "").toLowerCase().includes(rule.device.toLowerCase())) continue;
      return rule;
    }
    return null;
  }

  /** Findings split into the ones to report and the accepted ones (with the profile's reason). */
  split<T extends Finding>(findings: T[]): { kept: T[]; known: Array<T & KnownMatch> } {
    const kept: T[] = [];
    const known: Array<T & KnownMatch> = [];
    for (const finding of findings) {
      const rule = this.match(finding);
      if (rule) known.push({ ...finding, reason: rule.reason }); else kept.push(finding);
    }
    return { kept, known };
  }
}

function pathMatches(path: string | undefined, rule: string): boolean {
  if (!path) return false;
  const p = path.toLowerCase();
  const r = rule.toLowerCase().replace(/^\/+|\/+$/g, "");
  return p === r || p.endsWith(`/${r}`);
}

export interface MemoryOptions {
  known: KnownFindings | null;
  /** Console messages (first line) that are never findings. */
  ignoreErrors: RegExp[];
  /** Lowercased labels that leave a screen (tried last), besides the built-in ones. */
  backLabels: Set<string>;
}

export const NO_MEMORY: MemoryOptions = { known: null, ignoreErrors: [], backLabels: new Set() };

export function memoryOptions(profile: PlaytestProfile | null): MemoryOptions {
  if (!profile) return NO_MEMORY;
  return {
    known: profile.known?.length ? new KnownFindings(profile.known) : null,
    ignoreErrors: (profile.ignore_errors ?? []).map(source => new RegExp(source)),
    backLabels: new Set((profile.back_labels ?? []).map(l => l.trim().toLowerCase())),
  };
}

/** A known finding as results and the report list it. */
export function knownEntry(finding: Finding & KnownMatch): Record<string, unknown> {
  return {
    kind: finding.kind, severity: finding.severity, ...(finding.screen ? { screen: finding.screen } : {}),
    ...(finding.path ? { path: finding.path } : {}), ...(finding.label ? { label: finding.label } : {}),
    ...(finding.device ? { device: finding.device, orientation: finding.orientation } : {}),
    detail: finding.detail, reason: finding.reason,
  };
}

// ---------------------------------------------------------------------------
// Notes (NOTES.md)
// ---------------------------------------------------------------------------

export const NOTES_TEMPLATE = [
  "# Playtest notes",
  "",
  "What playtests of this project have learned. Agents read this file when a playtest session starts",
  "(playtest_session start returns it) and add to it at the end (playtest_memory note / map); edit it freely.",
  `Settings the tools apply on their own (deny-list, detector thresholds, accepted findings) are in profile.json next to it.`,
  "",
  "## Boot",
  "",
  "## Dangerous controls",
  "",
  "## Known issues",
  "",
  "## Flaky spots",
  "",
].join("\n");

const SECTION_NAME = /^[^\r\n#][^\r\n]{0,59}$/;

interface Section { title: string; start: number; end: number }

/** `## ` sections: the heading line and the line after the section's last line. */
function sections(lines: string[]): Section[] {
  const out: Section[] = [];
  for (let i = 0; i < lines.length; i++) {
    const m = /^##\s+(.+?)\s*$/.exec(lines[i]);
    if (!m) continue;
    if (out.length) out[out.length - 1].end = i;
    out.push({ title: m[1], start: i, end: lines.length });
  }
  return out;
}

function readNotesText(projectPath: string): string | null {
  const file = notesPath(projectPath);
  return existsSync(file) ? readLimited(file, NOTES_FILE) : null;
}

function writeNotes(projectPath: string, text: string): void {
  if (Buffer.byteLength(text) > MEMORY_LIMITS.max_file_bytes)
    throw new MemoryError("memory_too_large", `${NOTES_FILE} would grow over ${MEMORY_LIMITS.max_file_bytes / 1024} KB; trim old notes first (edit the file).`);
  writeAtomic(notesPath(projectPath), text.endsWith("\n") ? text : `${text}\n`);
}

/** Cut at a line boundary under `max` characters. */
function head(text: string, max: number): { text: string; truncated: boolean } {
  if (text.length <= max) return { text, truncated: false };
  const cut = text.lastIndexOf("\n", max);
  return { text: text.slice(0, cut > max / 2 ? cut : max), truncated: true };
}

/** Appends a dated bullet under `## <section>` (the file and the section are created as needed). */
export function appendNote(projectPath: string, section: string, text: string, date: string): Record<string, unknown> {
  const title = section.trim();
  if (!SECTION_NAME.test(title)) throw new MemoryError("invalid_arguments", "section must be a short heading (1–60 characters, one line, no '#').");
  if (title.toLowerCase() === SCREEN_MAP_SECTION.toLowerCase())
    throw new MemoryError("invalid_arguments", `'${SCREEN_MAP_SECTION}' is written by action "map" (and replaced by the next map); pick another section.`);
  const body = text.replace(/\r\n?/g, "\n").trim();
  if (!body) throw new MemoryError("invalid_arguments", "note needs text.");
  if (body.length > MEMORY_LIMITS.note_chars) throw new MemoryError("invalid_arguments", `A note is at most ${MEMORY_LIMITS.note_chars} characters.`);
  const existing = readNotesText(projectPath);
  const lines = (existing ?? NOTES_TEMPLATE).replace(/\r\n?/g, "\n").split("\n");
  const bullet = body.split("\n").map((line, i) => (i === 0 ? `- ${date} — ${line}` : `  ${line}`));
  const found = sections(lines).find(s => s.title.toLowerCase() === title.toLowerCase());
  if (found) {
    let at = found.end;
    while (at > found.start + 1 && lines[at - 1].trim() === "") at--;
    // Right under the heading: keep one blank line after it.
    const insert = at === found.start + 1 ? ["", ...bullet] : bullet;
    lines.splice(at, 0, ...insert);
    if (lines[at + insert.length] !== undefined && lines[at + insert.length].trim() !== "") lines.splice(at + insert.length, 0, "");
  } else {
    while (lines.length && lines[lines.length - 1].trim() === "") lines.pop();
    lines.push("", `## ${title}`, "", ...bullet, "");
  }
  writeNotes(projectPath, lines.join("\n"));
  return { file: NOTES_FILE, created: existing === null, section: found?.title ?? title, added: bullet.join("\n") };
}

/** Replaces (or adds) a whole `## <title>` section. */
export function replaceSection(text: string, title: string, body: string[]): string {
  const lines = text.replace(/\r\n?/g, "\n").split("\n");
  const found = sections(lines).find(s => s.title.toLowerCase() === title.toLowerCase());
  const block = [`## ${found?.title ?? title}`, "", ...body, ""];
  if (found) {
    lines.splice(found.start, found.end - found.start, ...block);
    return lines.join("\n");
  }
  while (lines.length && lines[lines.length - 1].trim() === "") lines.pop();
  return [...lines, "", ...block].join("\n");
}

/** show: the whole notes (bounded) or one section. */
export function showNotes(projectPath: string, section?: string): Record<string, unknown> | null {
  const text = readNotesText(projectPath);
  if (text === null) return null;
  const lines = text.replace(/\r\n?/g, "\n").split("\n");
  const all = sections(lines);
  const titles = all.map(s => ({ title: s.title, bullets: lines.slice(s.start + 1, s.end).filter(l => /^\s*[-*]\s/.test(l)).length }));
  if (section !== undefined) {
    const found = all.find(s => s.title.toLowerCase() === section.trim().toLowerCase());
    if (!found) throw new MemoryError("section_not_found", `${NOTES_FILE} has no section '${section}'; its sections: ${titles.map(t => t.title).join(", ") || "none"}.`);
    const shown = head(lines.slice(found.start, found.end).join("\n").trimEnd(), MEMORY_LIMITS.show_chars);
    return { file: NOTES_FILE, sections: titles, section: found.title, text: shown.text, ...(shown.truncated ? { truncated: true } : {}) };
  }
  const shown = head(text, MEMORY_LIMITS.show_chars);
  return { file: NOTES_FILE, sections: titles, text: shown.text, ...(shown.truncated ? { truncated: true } : {}) };
}

/** What playtest_session start returns about the project's memory. */
export function memoryBlock(projectPath: string | undefined): Record<string, unknown> {
  if (!projectPath) return { notes: null, profile: null };
  let notes: Record<string, unknown> | null = null;
  try {
    const text = readNotesText(projectPath);
    if (text !== null) {
      const shown = head(text, MEMORY_LIMITS.start_notes_chars);
      notes = { file: NOTES_FILE, text: shown.text, ...(shown.truncated ? { truncated: true, chars: text.length } : {}) };
    }
  } catch (error) {
    notes = { file: NOTES_FILE, problem: error instanceof Error ? error.message : String(error) };
  }
  const read = readProfile(projectPath);
  const profile = !read.exists ? null
    : read.problems.length ? { file: PROFILE_FILE, problems: read.problems.slice(0, 8) }
      : { file: PROFILE_FILE, summary: summarizeProfile(read.profile!) };
  return { notes, profile };
}

// ---------------------------------------------------------------------------
// Screen map from a crawl
// ---------------------------------------------------------------------------

/** The generated Screen map section from one crawl's `explore` block. */
export function screenMapLines(note: Record<string, any>, source: { session: string; crawl: number; date: string }): { lines: string[]; screens: number } {
  const screens: Array<Record<string, any>> = Array.isArray(note.screens) ? note.screens : [];
  if (!screens.length) throw new MemoryError("nothing_to_map", "That crawl filed no screens.");
  const edges: Array<Record<string, any>> = Array.isArray(note.edges) ? note.edges : [];
  const titleCount = new Map<string, number>();
  for (const s of screens) titleCount.set(String(s.title ?? ""), (titleCount.get(String(s.title ?? "")) ?? 0) + 1);
  const name = (s: Record<string, any> | undefined, id: string) => {
    const title = String(s?.title ?? "").trim();
    if (!title) return id;
    return (titleCount.get(title) ?? 0) > 1 ? `${title} (${id})` : title;
  };
  const byId = new Map(screens.map(s => [String(s.id), s]));
  const startId = String(screens[0].id);
  // Shortest tap path from the start screen to each screen.
  const paths = new Map<string, string[]>([[startId, []]]);
  const queue = [startId];
  while (queue.length) {
    const id = queue.shift()!;
    for (const edge of edges) {
      if (edge.from !== id || edge.to === id || paths.has(String(edge.to))) continue;
      paths.set(String(edge.to), [...paths.get(id)!, String(edge.action ?? "?")]);
      queue.push(String(edge.to));
    }
  }
  const denied = new Map<string, string[]>();
  for (const d of Array.isArray(note.denied) ? note.denied : []) {
    const list = denied.get(String(d.screen)) ?? [];
    list.push(`'${d.label}'`);
    denied.set(String(d.screen), list);
  }
  const where = note.device ? ` on ${note.device} (${note.orientation ?? "portrait"})` : "";
  const lines = [
    `<!-- Generated by playtest_memory map from crawl ${source.crawl} of session ${source.session} (${source.date}); the next map replaces this section. -->`,
    "",
    `${screens.length} screen(s)${where} from the start screen '${name(byId.get(startId), startId)}'; the crawl stopped: ${note.stop_reason ?? "?"}. ` +
      "A path lists the labels tapped from the start screen.",
    "",
  ];
  for (const s of screens.slice(0, MEMORY_LIMITS.map_screens)) {
    const id = String(s.id);
    const path = paths.get(id);
    const parts: string[] = [];
    parts.push(id === startId ? "start" : path ? `start → ${path.map(a => `'${a}'`).join(" → ")}` : "reached only after a restart");
    const never = denied.get(id);
    if (never?.length) parts.push(`never tapped: ${never.slice(0, 6).join(", ")}${never.length > 6 ? ` +${never.length - 6}` : ""}`);
    if (s.dead_end) parts.push("dead end");
    lines.push(`- **${name(s, id)}** — ${parts.join("; ")}`);
  }
  if (screens.length > MEMORY_LIMITS.map_screens) lines.push(`- … ${screens.length - MEMORY_LIMITS.map_screens} more screen(s)`);
  return { lines, screens: screens.length };
}

/** `map`: write the Screen map section of NOTES.md from a session's crawl. */
export function writeScreenMap(projectPath: string, entries: Array<Record<string, any>>, session: string, crawl: number | undefined, date: string,
  clean: (text: string) => string = t => t): Record<string, unknown> {
  const notes = entries.filter(e => e.k === "explore");
  if (!notes.length) throw new MemoryError("nothing_to_map", `Session ${session} has no finished UI crawl (playtest_explore) to map.`);
  const which = crawl ?? notes.length;
  if (which < 1 || which > notes.length) throw new MemoryError("invalid_arguments", `crawl ${which} does not exist; session ${session} has ${notes.length} crawl(s).`);
  const { lines, screens } = screenMapLines(notes[which - 1], { session, crawl: which, date });
  const body = lines.map(clean);
  const existing = readNotesText(projectPath);
  writeNotes(projectPath, replaceSection(existing ?? NOTES_TEMPLATE, SCREEN_MAP_SECTION, body));
  return { file: NOTES_FILE, created: existing === null, section: SCREEN_MAP_SECTION, session, crawl: which, screens, text: [`## ${SCREEN_MAP_SECTION}`, "", ...body].join("\n") };
}
