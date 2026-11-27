// Playtest detectors — findings from a `ui_snapshot` and the screen model the
// UI crawler (unity_open_mcp_playtest_explore) walks.
//
// Pure functions only: the crawler and `playtest_session check` feed them the
// snapshot output they already read. A finding is one problem a human should
// look at — `bug` for what is wrong as seen (a console error, overflowing
// text, a control under the notch), `suspicious` for what needs a look (a
// placeholder-looking text, overlapping or tiny controls, a button that does
// nothing, a screen with no way out). Findings are deduplicated per
// (kind, screen, element).

export type FindingSeverity = "bug" | "suspicious";
export type FindingKind =
  | "console_error" | "text_overflow" | "outside_safe_area" | "overlapping_controls" | "placeholder_text"
  | "small_touch_target" | "zero_size_control" | "dead_button" | "dead_end" | "navigation_failed";

export const FINDING_SEVERITY: Record<FindingKind, FindingSeverity> = {
  console_error: "bug",
  text_overflow: "bug",
  outside_safe_area: "bug",
  overlapping_controls: "suspicious",
  placeholder_text: "suspicious",
  small_touch_target: "suspicious",
  zero_size_control: "suspicious",
  dead_button: "suspicious",
  dead_end: "suspicious",
  navigation_failed: "suspicious",
};

export interface Finding {
  kind: FindingKind;
  severity: FindingSeverity;
  /** Crawler screen id (S1, S2…) — absent for a one-off check. */
  screen?: string;
  /** Hierarchy path of the element. */
  path?: string;
  /** Its text / label, when it has one. */
  label?: string;
  /** Crawler step that found it. */
  step?: number;
  detail: string;
  /** Device Simulator device and orientation it was seen on (device matrix). */
  device?: string;
  orientation?: string;
}

/**
 * Text that looks like an unresolved localisation key or format placeholder:
 * #KEY#, {key}, [KEY.part], snake_case_with_three_parts, UPPER_SNAKE_THREE_PARTS,
 * dotted.key.three (each part starting with a letter, so "v1.2.3" is not one).
 * .NET and JavaScript read it alike; the bridge matches it against display text.
 */
export const DEFAULT_PLACEHOLDER_PATTERN =
  "^(?:#[A-Za-z0-9_.]+#|\\{[A-Za-z0-9_.]+\\}|\\[[A-Za-z0-9]+(?:[_.][A-Za-z0-9]+)+\\]" +
  "|[a-z][a-z0-9]*(?:_[a-z0-9]+){2,}|[A-Z][A-Z0-9]*(?:_[A-Z0-9]+){2,}|[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*){2,})$";

/** Minimum touch target the crawler checks by default (Apple HIG 44 pt; Material asks 48 dp). */
export const DEFAULT_MIN_TOUCH_DP = 44;

/** dp → screen pixels at the snapshot's dpi (Android's 160-dpi baseline); null when the dpi is unknown. */
export function minTouchPx(dp: number | null | undefined, dpi: unknown): number | null {
  if (dp === null || dp === undefined || !(dp > 0)) return null;
  const d = typeof dpi === "number" && Number.isFinite(dpi) ? dpi : 0;
  if (d <= 0) return null;
  return Math.round(dp * d / 160);
}

// ---------------------------------------------------------------------------
// Snapshot nodes and screens
// ---------------------------------------------------------------------------

/** The `ui_snapshot` node fields the crawler reads. */
export interface SnapshotNode {
  id?: string | number;
  path: string;
  role: string;
  text?: string | null;
  rect?: number[];
  visible?: boolean;
  visibility?: string;
  interactable?: boolean;
  occluded_by?: string | null;
}

const CONTROL_ROLES = new Set(["button", "toggle", "slider", "input"]);
/** Roles the crawler taps in v1. */
export const TAPPABLE_ROLES = new Set(["button", "toggle"]);

/** Lower-case, collapse whitespace, mask numbers so a ticking counter is the same screen. */
export function maskText(value: string | null | undefined): string {
  return String(value ?? "")
    .toLowerCase()
    .replace(/[+-]?\d+(?:[.,:/]\d+)*/g, "#")
    .replace(/\s+/g, " ")
    .trim();
}

/** A path with numbers masked ("Item (Clone)/Row 3" and "Row 4" alike). */
export function maskPath(path: string): string {
  return path.replace(/\d+/g, "#");
}

/**
 * The screen's identity: its visible controls (role + label + masked path) and
 * visible non-control texts, numbers masked. Images and containers are
 * decoration, not identity; a control covered by something in front of it (a
 * popup's dimmer) is behind the screen the player sees, so a popup over a menu
 * is a screen of its own.
 */
export function screenSignature(nodes: SnapshotNode[]): Set<string> {
  const tokens = new Set<string>();
  for (const n of nodes) {
    if (n.visible === false) continue;
    if (CONTROL_ROLES.has(n.role) || n.role === "scroll") {
      // A text field is known by its place, not by what is typed into it.
      if (!n.occluded_by) tokens.add(`c|${n.role}|${n.role === "input" ? "" : maskText(n.text)}|${maskPath(n.path)}`);
    }
    else if (n.role === "text" && n.text) tokens.add(`t|${maskText(n.text)}`);
  }
  return tokens;
}

/** Jaccard similarity of two signatures (two empty screens are the same). */
export function similarity(a: Set<string>, b: Set<string>): number {
  if (a.size === 0 && b.size === 0) return 1;
  let both = 0;
  for (const token of a) if (b.has(token)) both++;
  return both / (a.size + b.size - both);
}

/**
 * A short human title for a screen: its first plain text, else its first control
 * label. Over covered controls (a popup over a menu) the popup's own content —
 * what comes after the last covered control in hierarchy order — goes first.
 */
export function screenTitle(nodes: SnapshotNode[]): string {
  let lastCovered = -1;
  nodes.forEach((n, i) => { if (n.occluded_by) lastCovered = i; });
  const ordered = lastCovered >= 0 ? [...nodes.slice(lastCovered + 1), ...nodes.slice(0, lastCovered + 1)] : nodes;
  const visible = ordered.filter(n => n.visible !== false && n.text && n.text.trim());
  // A counter or a score ("6", "1 0") names no screen: prefer texts with words.
  const worded = visible.filter(n => /[a-z]{2}/.test(maskText(n.text)));
  const text = worded.find(n => n.role === "text") ?? visible.find(n => n.role === "text" && !worded.length);
  const pick = text ?? worded[0] ?? visible[0];
  return pick ? clip(pick.text!.trim(), 60) : "(no text)";
}

// ---------------------------------------------------------------------------
// Which controls the crawler may tap
// ---------------------------------------------------------------------------

/** Labels the crawler never taps by default: money, destructive or leaving actions. */
export const DEFAULT_DENY_LABEL = /\b(buy|purchase|pay|subscribe|subscription|delete|remove|reset|log ?out|sign ?out|rate|review|share|restore|quit|exit|unlock now|watch (?:an? )?(?:ad|video))\b/i;
/** A price: a currency symbol or code next to a number. */
export const PRICE_LABEL = /(?:[$€£¥₽₹₩]|\b(?:usd|eur|gbp|rub|jpy|cny|inr|krw)\b)\s?\d|\d(?:[.,]\d+)?\s?(?:[$€£¥₽₹₩]|\b(?:usd|eur|gbp|rub|jpy|cny|inr|krw)\b)/i;
/** Labels (or object names) that leave a screen: tried last so the rest of the screen is explored first. */
export const BACK_LABEL = /^(?:back|close|cancel|no|not now|later|maybe later|skip|done|ok|x|×|✕|✖|<|‹|←)$/i;
const BACK_NAME = /(?:^|[^a-z])(?:back|close|cancel|exit|dismiss)(?:[^a-z]|$)|(?:back|close|cancel|dismiss)(?:button|btn)/i;

export interface ActionPolicy {
  deny: RegExp[];
  /** Exact labels or paths exempt from every deny rule. */
  allow: Set<string>;
  /** false = the default deny-list is off (only `deny` applies). */
  defaultDeny: boolean;
}

export function compilePolicy(deny: unknown, allow: unknown, defaultDeny = true): ActionPolicy {
  const patterns: RegExp[] = [];
  for (const source of Array.isArray(deny) ? deny : []) {
    if (typeof source !== "string" || !source) continue;
    try { patterns.push(new RegExp(source, "i")); }
    catch (error) { throw new Error(`deny pattern '${source}' is not a valid regular expression: ${error instanceof Error ? error.message : String(error)}`); }
  }
  const allowed = new Set<string>();
  for (const value of Array.isArray(allow) ? allow : []) if (typeof value === "string" && value) allowed.add(value.trim().toLowerCase());
  return { deny: patterns, allow: allowed, defaultDeny };
}

/** Why the crawler will not tap this control, or null. */
export function denyReason(node: SnapshotNode, policy: ActionPolicy): string | null {
  const label = String(node.text ?? "").trim();
  if (policy.allow.has(label.toLowerCase()) || policy.allow.has(node.path.toLowerCase())) return null;
  if (policy.defaultDeny) {
    const words = DEFAULT_DENY_LABEL.exec(label);
    if (words) return `label matches the default deny-list ('${words[0]}')`;
    if (PRICE_LABEL.test(label)) return "label shows a price";
  }
  for (const pattern of policy.deny)
    if (pattern.test(label) || pattern.test(node.path)) return `matches deny pattern /${pattern.source}/`;
  return null;
}

/** Whether this control likely leaves the screen (tried last); `extra`: lowercased project labels (the profile's back_labels). */
export function isBackLike(node: SnapshotNode, extra?: ReadonlySet<string>): boolean {
  const label = String(node.text ?? "").trim();
  if (label && (BACK_LABEL.test(label) || extra?.has(label.toLowerCase()))) return true;
  const name = node.path.split("/").pop() ?? "";
  return !label && BACK_NAME.test(name);
}

/** A control the crawler can tap right now (before the deny-list). */
export function isTappable(node: SnapshotNode): boolean {
  return TAPPABLE_ROLES.has(node.role) && node.visible !== false && node.interactable === true && !node.occluded_by;
}

/** A text field the crawler can type into right now (before the deny-list). */
export function isFillable(node: SnapshotNode): boolean {
  return node.role === "input" && node.visible !== false && node.interactable === true && !node.occluded_by;
}

/** Stable key of an action across visits: masked path + masked label (object ids change; a field's text is what we typed). */
export function actionKey(node: SnapshotNode): string {
  return `${maskPath(node.path)}|${node.role === "input" ? "" : maskText(node.text)}`;
}

/** How a step names a control: its label, else its object name. */
export function controlLabel(node: SnapshotNode): string {
  const label = String(node.text ?? "").trim();
  return label ? clip(label, 60) : (node.path.split("/").pop() ?? node.path);
}

// ---------------------------------------------------------------------------
// Findings from a snapshot's diagnostics
// ---------------------------------------------------------------------------

interface DiagnosticList { count?: number; items?: Array<Record<string, any>>; truncated?: number }

const clip = (value: string, max: number) => (value.length > max ? `${value.slice(0, max - 1)}…` : value);
const size = (value: unknown) => Array.isArray(value) && value.length >= 2 ? `${Math.round(Number(value[0]))}×${Math.round(Number(value[1]))}` : "?";

function list(diagnostics: Record<string, unknown>, key: string): Array<Record<string, any>> {
  const value = diagnostics[key] as DiagnosticList | undefined;
  return Array.isArray(value?.items) ? value!.items! : [];
}

/**
 * Findings the snapshot's diagnostics block reports (it is on the first page
 * only). A control covered by something in front of it (a popup's dimmer) is
 * behind this screen and was checked on its own screen: left out.
 */
export function findingsFromSnapshot(output: Record<string, any>, at: { screen?: string; step?: number } = {}): Finding[] {
  const diagnostics = (output?.diagnostics ?? {}) as Record<string, unknown>;
  const covered = new Set<string>();
  for (const node of Array.isArray(output?.nodes) ? output.nodes as SnapshotNode[] : []) if (node?.occluded_by) covered.add(node.path);
  const out: Finding[] = [];
  const add = (kind: FindingKind, item: Record<string, any>, detail: string, label?: unknown) => {
    if (covered.has(String(item.path ?? ""))) return;
    const text = typeof label === "string" && label.trim() ? clip(label.trim(), 80) : undefined;
    out.push({ kind, severity: FINDING_SEVERITY[kind], ...at, path: String(item.path ?? ""), ...(text ? { label: text } : {}), detail });
  };
  for (const item of list(diagnostics, "overflow"))
    add("text_overflow", item, `text needs ${size(item.content)} px but its box is ${size(item.size)}`, item.text);
  for (const item of list(diagnostics, "outside_safe_area"))
    add("outside_safe_area", item, `${item.role ?? "element"} reaches outside the safe area (${Array.isArray(item.edges) && item.edges.length ? item.edges.join(", ") : "edge"})`, item.text);
  for (const item of list(diagnostics, "overlapping_controls"))
    add("overlapping_controls", item, `overlaps ${item.with_text ? `'${clip(String(item.with_text), 40)}' ` : ""}${item.with} by ${Math.round(Number(item.overlap ?? 0) * 100)}%`, item.text);
  for (const item of list(diagnostics, "placeholder"))
    add("placeholder_text", item, `text looks like an unresolved key or placeholder`, item.text);
  const small = diagnostics.small_touch_targets as (DiagnosticList & { min_touch_size?: number }) | undefined;
  for (const item of list(diagnostics, "small_touch_targets"))
    add("small_touch_target", item, `${item.role ?? "control"} is ${size(item.size)} px, under the ${small?.min_touch_size ?? "?"} px minimum`, item.text);
  for (const item of list(diagnostics, "zero_size_interactables"))
    add("zero_size_control", item, `${item.role ?? "control"} has a zero-size rect`, item.text);
  return out;
}

/** How many items each diagnostic list left out (beyond its first 20). */
export function truncatedDiagnostics(output: Record<string, any>): Record<string, number> {
  const out: Record<string, number> = {};
  for (const [key, value] of Object.entries((output?.diagnostics ?? {}) as Record<string, DiagnosticList>))
    if (value && typeof value.truncated === "number" && value.truncated > 0) out[key] = value.truncated;
  return out;
}

/**
 * The identity a finding is deduplicated by: the element it is about (a HUD
 * label that overflows on every screen is one finding), the console message,
 * or — for a finding about a whole screen — the screen.
 */
export function findingKey(finding: Pick<Finding, "kind" | "screen" | "path" | "label" | "detail" | "device" | "orientation">): string {
  // On a device matrix the same element on two devices is two findings.
  const on = finding.device ? `${finding.device}/${finding.orientation ?? ""}#` : "";
  if (finding.kind === "console_error") return `${on}${finding.kind}|${finding.label ?? finding.detail}`;
  const extra = finding.kind === "overlapping_controls" ? finding.detail : "";
  return finding.path ? `${on}${finding.kind}|${finding.path}|${extra}` : `${on}${finding.kind}|@${finding.screen ?? ""}|${extra}`;
}

/** Deduplicated findings: one per element (or console message, or screen) and kind. */
export class FindingSet {
  private readonly byKey = new Map<string, Finding>();
  constructor(private readonly max = 500) {}
  /** Adds the finding unless it is already known; returns true when it is new. */
  add(finding: Finding): boolean {
    const key = findingKey(finding);
    if (this.byKey.has(key) || this.byKey.size >= this.max) return false;
    this.byKey.set(key, finding);
    return true;
  }
  addAll(findings: Finding[]): Finding[] { return findings.filter(f => this.add(f)); }
  get size(): number { return this.byKey.size; }
  get all(): Finding[] { return [...this.byKey.values()]; }
  counts(): { bug: number; suspicious: number } {
    let bug = 0, suspicious = 0;
    for (const f of this.byKey.values()) if (f.severity === "bug") bug++; else suspicious++;
    return { bug, suspicious };
  }
}

/** Bug findings first, then suspicious; stable otherwise. */
export function sortFindings(findings: Finding[]): Finding[] {
  return [...findings].sort((a, b) => (a.severity === b.severity ? 0 : a.severity === "bug" ? -1 : 1));
}
