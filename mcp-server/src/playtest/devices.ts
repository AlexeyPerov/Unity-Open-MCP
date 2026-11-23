// Playtest device matrix — the engine behind unity_open_mcp_playtest_devices.
//
// Puts the running game on each device (× orientation) of a Device Simulator
// list in turn and checks it there: the screen it shows (`current`: the
// snapshot detectors at that device's dpi and one composed frame), or a whole UI
// crawl (`explore`, the game restarted between devices). Findings are per
// device; the result groups identical findings across devices so a problem that
// only notch phones have stands out. The view the run found is always put back.
//
// Like the crawler, the engine reaches the bridge only through the ToolCaller it
// is given (normal dispatch as the session owner) and records through the
// recorder interfaces it is given.
import { toolOutput } from "../jobs/adapters.js";
import {
  findingsFromSnapshot, minTouchPx, sortFindings, truncatedDiagnostics, type Finding,
} from "./detectors.js";
import {
  Explorer, ExploreError, collectLogs, restartGame,
  type ExploreOptions, type ExploreRecorder, type RecordedStep, type ToolCaller,
} from "./explore.js";

import { DEVICE_ORIENTATIONS, DEVICE_PRESETS } from "../tools/playtest-devices.js";

export { DEVICE_ORIENTATIONS, DEVICE_PRESETS };
export type DevicePreset = typeof DEVICE_PRESETS[number];
/** Diagonal (inches) from which a device counts as a tablet. */
export const TABLET_INCHES = 7;
export const DEVICE_LIMITS = { max_devices: 20 };

/** A Device Simulator catalog entry (game_view list_devices). */
export interface DeviceInfo {
  id: string;
  screen: number[];
  dpi: number;
  insets?: { top?: number; bottom?: number; left?: number; right?: number };
  notch?: boolean;
  orientations?: string[];
}

export function diagonalInches(d: DeviceInfo): number {
  const [w, h] = d.screen ?? [0, 0];
  return d.dpi > 0 ? Math.hypot(w, h) / d.dpi : 0;
}

export function isTablet(d: DeviceInfo): boolean {
  return diagonalInches(d) >= TABLET_INCHES;
}

const aspect = (d: DeviceInfo) => {
  const [w, h] = d.screen ?? [1, 1];
  return Math.max(w, h) / Math.max(1, Math.min(w, h));
};

/**
 * A preset's devices: `common` = the smallest phone, the tallest phone, the
 * phone with the deepest top inset, the smallest tablet (deduplicated);
 * `phones` / `tablets` by diagonal; `notch`; `all`. Catalog order is kept.
 */
export function selectPreset(devices: DeviceInfo[], preset: DevicePreset): DeviceInfo[] {
  const phones = devices.filter(d => !isTablet(d));
  const tablets = devices.filter(isTablet);
  switch (preset) {
    case "phones": return phones;
    case "tablets": return tablets;
    case "notch": return devices.filter(d => d.notch);
    case "all": return [...devices];
    default: {
      const pick = new Set<DeviceInfo>();
      const by = (list: DeviceInfo[], score: (d: DeviceInfo) => number) =>
        list.reduce<DeviceInfo | null>((best, d) => (best === null || score(d) > score(best) ? d : best), null);
      const smallest = by(phones, d => -diagonalInches(d));
      const tallest = by(phones, aspect);
      const notched = by(phones.filter(d => (d.insets?.top ?? 0) > 0), d => d.insets?.top ?? 0);
      const tablet = by(tablets, d => -diagonalInches(d));
      for (const d of [smallest, tallest, notched, tablet]) if (d) pick.add(d);
      return devices.filter(d => pick.has(d));
    }
  }
}

/** The catalog entries named by `names` (exact id or a substring matching exactly one, case-insensitive). */
export function resolveDevices(devices: DeviceInfo[], names: string[]): { matched: DeviceInfo[]; errors: string[] } {
  const matched: DeviceInfo[] = [];
  const errors: string[] = [];
  for (const name of names) {
    const lower = name.toLowerCase();
    const exact = devices.find(d => d.id.toLowerCase() === lower);
    const hits = exact ? [exact] : devices.filter(d => d.id.toLowerCase().includes(lower));
    if (hits.length === 1) { if (!matched.includes(hits[0])) matched.push(hits[0]); }
    else if (hits.length === 0) errors.push(`no device matches '${name}'`);
    else errors.push(`'${name}' matches ${hits.length} devices (${hits.slice(0, 5).map(d => d.id).join(", ")}${hits.length > 5 ? ", …" : ""})`);
  }
  return { matched, errors };
}

/** `landscape` is landscape_left; anything else must be a Simulator orientation. */
export function normalizeOrientations(values: unknown): string[] {
  const list = Array.isArray(values) && values.length ? values : ["portrait"];
  const out: string[] = [];
  for (const raw of list) {
    const value = String(raw) === "landscape" ? "landscape_left" : String(raw);
    if (!(DEVICE_ORIENTATIONS as readonly string[]).includes(value))
      throw new ExploreError("invalid_arguments", `Unknown orientation '${raw}'. Valid: ${DEVICE_ORIENTATIONS.join(", ")}, landscape.`);
    if (!out.includes(value)) out.push(value);
  }
  return out;
}

export type DeviceFinding = Finding & { device: string; orientation: string };

/** Identical findings across devices: kind + element → the device / orientation pairs that show it. */
export function groupAcrossDevices(findings: DeviceFinding[], deviceCount: number): Array<Record<string, unknown>> {
  const groups = new Map<string, { kind: string; severity: string; path?: string; label?: string; detail: string; on: string[] }>();
  for (const f of findings) {
    const element = f.kind === "console_error" ? f.label ?? f.detail : f.path ?? `@${f.screen ?? ""}`;
    const key = `${f.kind}|${element}`;
    const where = `${f.device} (${f.orientation})`;
    const group = groups.get(key) ?? { kind: f.kind, severity: f.severity, ...(f.path ? { path: f.path } : {}), ...(f.label ? { label: f.label } : {}), detail: f.detail, on: [] };
    if (!group.on.includes(where)) group.on.push(where);
    if (f.severity === "bug") group.severity = "bug";
    groups.set(key, group);
  }
  return [...groups.values()]
    .sort((a, b) => (a.severity === b.severity ? b.on.length - a.on.length : a.severity === "bug" ? -1 : 1))
    .map(g => ({ ...g, count: g.on.length, of: deviceCount }));
}

export interface DeviceMatrixOptions {
  devices: string[] | null;
  preset: DevicePreset;
  maxDevices: number;
  orientations: string[];
  screens: "current" | "explore";
  /** The crawl's options (screens: explore). */
  explore: ExploreOptions;
  placeholderPattern: string | null;
  minTouchDp: number | null;
  capture: boolean;
  settleTimeoutMs: number;
}

export interface DeviceMatrixDeps {
  call: ToolCaller;
  /** Records a device check (kind check) — null when the session is gone. */
  check: { begin(label: string, args: Record<string, unknown>): RecordedStep | null };
  /** The crawler's recorder (screens: explore). */
  explore: ExploreRecorder;
  /** A finished crawl on one device (the job writes its explore block and mark). */
  onCrawl?: (result: Record<string, unknown>, device: string, orientation: string) => void;
  signal: AbortSignal;
  progress?: (phase: string, fraction: number, detail: Record<string, unknown>) => void;
  now?: () => number;
}

interface ViewState { mode: string; sizeLabel?: string; device?: string; orientation?: string }

class Stop { constructor(readonly reason: string, readonly note?: string) {} }

export class DeviceMatrix {
  private readonly now: () => number;
  private readonly entries: Array<Record<string, unknown>> = [];
  private readonly skipped: Array<Record<string, unknown>> = [];
  private readonly findings: DeviceFinding[] = [];
  private readonly notes: string[] = [];
  private startedAt = 0;

  constructor(private readonly options: DeviceMatrixOptions, private readonly deps: DeviceMatrixDeps) {
    this.now = deps.now ?? Date.now;
  }

  async run(): Promise<Record<string, unknown>> {
    this.startedAt = this.now();
    const original = await this.view();
    const catalog = await this.catalog();
    const chosen = this.choose(catalog);
    const pairs = chosen.flatMap(d => this.options.orientations.map(o => ({ device: d, orientation: o })));
    let reason = "done";
    let restored: Record<string, unknown> | null = null;
    try {
      let crawled = 0;
      for (let i = 0; i < pairs.length; i++) {
        if (this.deps.signal.aborted) throw new Stop("cancelled");
        const { device, orientation } = pairs[i];
        this.deps.progress?.("devices", i / pairs.length, { device: device.id, orientation, done: i, of: pairs.length, findings: this.findings.length });
        if (device.orientations && !device.orientations.includes(orientation)) {
          this.skipped.push({ device: device.id, orientation, reason: "orientation_unsupported" });
          continue;
        }
        const set = await this.deps.call("unity_open_mcp_game_view",
          { action: "set_device", device: device.id, orientation, settle_timeout_ms: this.options.settleTimeoutMs });
        const out = toolOutput(set);
        if (set.isError || out.error) {
          const code = String((out.error as { code?: string } | undefined)?.code ?? "error");
          if (code === "unsupported_editor_version" || code === "editor_internals_unavailable")
            throw new ExploreError(code, `The Device Simulator is not reachable on this Editor: ${String((out.error as { message?: string }).message ?? code)}`);
          this.skipped.push({ device: device.id, orientation, reason: code, ...(out.screen ? { screen: [out.screen.width, out.screen.height] } : {}) });
          continue;
        }
        const screen = (out.screen ?? {}) as Record<string, any>;
        const size = `${screen.width}x${screen.height}`;
        if (out.orientation_applied === false && this.entries.some(e => e.device === device.id && e.size === size)) {
          this.skipped.push({ device: device.id, orientation, reason: "orientation_not_applied", note: "the game stayed in the orientation already checked" });
          continue;
        }
        const base = {
          device: device.id, orientation, size, screen: [screen.width, screen.height], safe_area: screen.safe_area, insets: screen.insets,
          dpi: screen.dpi ?? device.dpi, ...(out.orientation_applied === false ? { orientation_applied: false } : {}),
        };
        if (this.options.screens === "explore") {
          await this.crawl(base, crawled++ > 0);
        } else {
          await this.checkCurrent(base);
        }
      }
    } catch (error) {
      if (error instanceof Stop) { reason = error.reason; if (error.note) this.notes.push(error.note); }
      else throw error;
    } finally {
      restored = await this.restore(original);
    }
    return this.result(reason, chosen, catalog.length, restored);
  }

  // -------------------------------------------------------------------------

  private async view(): Promise<ViewState> {
    const result = await this.deps.call("unity_open_mcp_game_view", { action: "get" });
    const out = toolOutput(result);
    if (result.isError || out.error) {
      const code = String((out.error as { code?: string } | undefined)?.code ?? "game_view_unavailable");
      throw new ExploreError(code, `game_view get failed: ${String((out.error as { message?: string } | undefined)?.message ?? code)}`);
    }
    const screen = (out.screen ?? {}) as Record<string, any>;
    const orientation = (DEVICE_ORIENTATIONS as readonly string[]).includes(String(screen.orientation)) ? String(screen.orientation)
      : screen.orientation === "landscape" ? "landscape_left" : "portrait";
    return {
      mode: String(out.mode ?? "none"),
      ...(out.size?.label ? { sizeLabel: String(out.size.label) } : {}),
      ...(out.device?.id ? { device: String(out.device.id), orientation } : {}),
    };
  }

  private async catalog(): Promise<DeviceInfo[]> {
    const devices: DeviceInfo[] = [];
    let cursor: string | undefined;
    for (let page = 0; page < 10; page++) {
      const result = await this.deps.call("unity_open_mcp_game_view", { action: "list_devices", page_size: 100, ...(cursor ? { cursor } : {}) });
      const out = toolOutput(result);
      if (result.isError || out.error) {
        const code = String((out.error as { code?: string } | undefined)?.code ?? "simulator_unavailable");
        throw new ExploreError(code, `game_view list_devices failed: ${String((out.error as { message?: string } | undefined)?.message ?? code)}`);
      }
      for (const d of Array.isArray(out.devices) ? out.devices : []) if (d && typeof d.id === "string") devices.push(d as DeviceInfo);
      cursor = out.pagination?.next_cursor ?? undefined;
      if (!cursor) break;
    }
    if (!devices.length) throw new ExploreError("no_devices", "The Device Simulator lists no devices.");
    return devices;
  }

  private choose(catalog: DeviceInfo[]): DeviceInfo[] {
    let chosen: DeviceInfo[];
    if (this.options.devices && this.options.devices.length) {
      const { matched, errors } = resolveDevices(catalog, this.options.devices);
      if (errors.length)
        throw new ExploreError("device_not_found", `${errors.join("; ")}. Devices: ${catalog.map(d => d.id).join(", ")}.`);
      chosen = matched;
    } else chosen = selectPreset(catalog, this.options.preset);
    if (!chosen.length) throw new ExploreError("no_devices", `The preset '${this.options.preset}' matches no device of the ${catalog.length} listed.`);
    if (chosen.length > this.options.maxDevices) {
      this.notes.push(`${chosen.length} devices matched; the first ${this.options.maxDevices} (max_devices) were checked.`);
      chosen = chosen.slice(0, this.options.maxDevices);
    }
    return chosen;
  }

  /** The screen the game shows on this device: the detectors at its dpi, one frame — one recorded call of kind check. */
  private async checkCurrent(base: Record<string, any>): Promise<void> {
    const label = `Check on ${base.device} (${base.orientation})`;
    const step = this.deps.check.begin(label, { device: base.device, orientation: base.orientation });
    if (!step) throw new Stop("session_ended", "The playtest session ended during the run; it stopped with it.");
    const logs: unknown[] = [];
    const px = minTouchPx(this.options.minTouchDp, base.dpi);
    const snapshot = await this.deps.call("unity_open_mcp_ui_snapshot", {
      profile: "compact", page_size: 1, visible_only: true,
      ...(this.options.placeholderPattern ? { placeholder_pattern: this.options.placeholderPattern } : {}),
      ...(px !== null ? { min_touch_size: px } : {}),
    });
    collectLogs(snapshot, logs);
    const out = toolOutput(snapshot);
    if (snapshot.isError || out.error) {
      step.finish({ step: { label, device: base.device, orientation: base.orientation, outcome: "snapshot_failed", error: out.error ?? null } }, logs, true);
      this.skipped.push({ device: base.device, orientation: base.orientation, reason: String((out.error as { code?: string } | undefined)?.code ?? "snapshot_failed") });
      return;
    }
    const found: DeviceFinding[] = findingsFromSnapshot(out).map(f => ({ ...f, device: base.device, orientation: base.orientation }));
    for (const line of errorMessages(logs))
      found.push({ kind: "console_error", severity: "bug", label: line, detail: "logged while checking this device", device: base.device, orientation: base.orientation });
    let frame: string | undefined;
    if (this.options.capture) {
      const shot = await this.deps.call("unity_senses_screenshot", { view: "composed", max_edge: 960 });
      collectLogs(shot, logs);
      const shotOut = toolOutput(shot);
      if (!shot.isError && !shotOut.error && typeof shotOut.filePath === "string") frame = shotOut.filePath;
      else this.notes.push(`${base.device} (${base.orientation}): the screenshot failed.`);
    }
    const truncated = truncatedDiagnostics(out);
    if (Object.keys(truncated).length)
      this.notes.push(`${base.device} (${base.orientation}): diagnostics listed only their first 20 items (${Object.entries(truncated).map(([k, v]) => `${k} +${v}`).join(", ")}).`);
    if (px === null && this.options.minTouchDp !== null) this.notes.push(`${base.device}: no dpi, the touch-size check was skipped.`);
    this.findings.push(...found);
    const counts = countOf(found);
    this.entries.push({ ...base, mode: out.mode, counts, findings: sortFindings(found).slice(0, 30), ...(frame ? { frame, frame_call: step.seq } : {}) });
    step.finish({
      step: { label, device: base.device, orientation: base.orientation, screen: base.screen, findings: sortFindings(found).slice(0, 20).map(compact), counts },
      ...(frame ? { filePath: frame } : {}),
    }, logs);
  }

  /** A whole crawl on this device, the game restarted first (unless it is the first device or restart is none). */
  private async crawl(base: Record<string, any>, restartFirst: boolean): Promise<void> {
    const explore = this.options.explore;
    if (restartFirst && explore.restart !== "none") {
      const step = this.deps.check.begin(`Restart the game for ${base.device} (${base.orientation})`, { purpose: "restart", device: base.device });
      if (!step) throw new Stop("session_ended", "The playtest session ended during the run; it stopped with it.");
      const logs: unknown[] = [];
      const failure = await restartGame(this.deps.call, explore.restart, explore.restartCommand, explore.restartArgs, logs);
      step.finish({ step: { purpose: "restart", device: base.device, outcome: failure ? "failed" : "ok", ...(failure ? { error: failure.error } : {}) } }, logs, !!failure);
      if (failure) {
        this.skipped.push({ device: base.device, orientation: base.orientation, reason: "restart_failed", note: failure.message });
        return;
      }
    }
    let result: Record<string, any>;
    try {
      result = await new Explorer(explore, {
        call: this.deps.call, recorder: this.deps.explore, signal: this.deps.signal,
        progress: (_phase, _fraction, detail) => this.deps.progress?.("devices", this.entries.length / Math.max(1, this.entries.length + 1), { device: base.device, crawl: detail }),
      }).run();
    } catch (error) {
      if (!(error instanceof ExploreError)) throw error;
      this.skipped.push({ device: base.device, orientation: base.orientation, reason: error.code, note: error.message });
      return;
    }
    const found: DeviceFinding[] = (Array.isArray(result.findings) ? result.findings as Finding[] : [])
      .map(f => ({ ...f, device: base.device, orientation: base.orientation }));
    this.findings.push(...found);
    this.deps.onCrawl?.(result, base.device, base.orientation);
    const coverage = (result.coverage ?? {}) as Record<string, number>;
    this.entries.push({
      ...base, counts: result.finding_counts, findings: found.slice(0, 30), stop_reason: result.stop_reason, steps: result.steps,
      screens: coverage.screens, actions: `${coverage.actions_tried ?? 0}/${coverage.actions_total ?? 0}`,
    });
    if (result.stop_reason === "cancelled") throw new Stop("cancelled");
    if (result.stop_reason === "session_ended") throw new Stop("session_ended");
  }

  private async restore(view: ViewState): Promise<Record<string, unknown>> {
    let args: Record<string, unknown> | null = null;
    if (view.mode === "simulator" && view.device) args = { action: "set_device", device: view.device, orientation: view.orientation ?? "portrait" };
    else if (view.mode === "game" && view.sizeLabel) args = { action: "set_resolution", size_label: view.sizeLabel };
    if (!args) return { restored: false, reason: `the view was '${view.mode}' with nothing to put back` };
    try {
      const result = await this.deps.call("unity_open_mcp_game_view", { ...args, settle_timeout_ms: this.options.settleTimeoutMs });
      const out = toolOutput(result);
      if (result.isError || out.error)
        return { restored: false, to: args, error: out.error ?? null };
      return { restored: true, to: args };
    } catch (error) {
      return { restored: false, to: args, error: error instanceof Error ? error.message : String(error) };
    }
  }

  private result(reason: string, chosen: DeviceInfo[], catalogSize: number, restored: Record<string, unknown> | null): Record<string, unknown> {
    const counts = countOf(this.findings);
    const checked = this.entries.length;
    return {
      status: "ok",
      stop_reason: reason,
      screens: this.options.screens,
      duration_ms: Math.max(0, this.now() - this.startedAt),
      devices: chosen.map(d => ({ id: d.id, screen: d.screen, dpi: d.dpi, inches: Math.round(diagonalInches(d) * 10) / 10, tablet: isTablet(d), notch: d.notch === true })),
      catalog: catalogSize,
      orientations: this.options.orientations,
      checked,
      finding_counts: counts,
      entries: this.entries,
      matrix: groupAcrossDevices(this.findings, checked).slice(0, 100),
      ...(this.skipped.length ? { skipped: this.skipped } : {}),
      restored,
      ...(this.notes.length ? { notes: [...new Set(this.notes)].slice(0, 20) } : {}),
    };
  }
}

function countOf(findings: Finding[]): { bug: number; suspicious: number } {
  let bug = 0, suspicious = 0;
  for (const f of findings) if (f.severity === "bug") bug++; else suspicious++;
  return { bug, suspicious };
}

function compact(f: Finding): Record<string, unknown> {
  return { kind: f.kind, severity: f.severity, ...(f.path ? { path: f.path } : {}), ...(f.label ? { label: f.label } : {}), detail: f.detail };
}

function errorMessages(logs: unknown[]): string[] {
  const out = new Set<string>();
  for (const log of logs as Array<Record<string, unknown>>)
    if (["error", "exception", "assert"].includes(String(log.severity ?? log.type ?? "").toLowerCase()) && typeof log.message === "string")
      out.add(log.message.split("\n")[0].trim().slice(0, 160));
  return [...out];
}
