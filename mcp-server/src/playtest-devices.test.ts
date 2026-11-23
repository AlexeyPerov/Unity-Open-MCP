// Playtest device matrix (unity_open_mcp_playtest_devices): presets and device
// resolution over the Unity 6 Device Simulator catalog, the engine against a fake
// Simulator (current screen per device, a crawl per device with restarts,
// orientation not applied, not settled, cancel, the view put back, findings
// grouped by device) and the path through the router into the session report.
import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { ToolRouter } from "./tool-router.js";
import { ToolSessionState } from "./tool-session-state.js";
import { ALL_TOOLS, PLAYTEST_TOOLS } from "./tools/index.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import type { BridgeEventStream } from "./event-stream.js";
import { groupFor } from "./capabilities/tool-groups.js";
import { recommendGroups } from "./capabilities/intent-groups.js";
import { RECOMMENDED_TOOL_CHAINS } from "./capabilities/cost-hints.js";
import { isLocalTool } from "./local-tools.js";
import { PLAYTEST_DEVICES_TOOL } from "./tools/playtest-devices.js";
import { PlaytestRecorder } from "./playtest/recorder.js";
import { encodePng } from "./playtest/png.js";
import {
  DeviceMatrix, diagonalInches, groupAcrossDevices, isTablet, normalizeOrientations, resolveDevices, selectPreset, type DeviceInfo,
} from "./playtest/devices.js";
import { parseDevicesOptions } from "./playtest/explore-job.js";

const body = (result: CallToolResult) => JSON.parse((result.content.find(c => c.type === "text") as { text: string }).text);
const ok = (output: unknown, logs: unknown[] = []): CallToolResult =>
  ({ content: [{ type: "text", text: JSON.stringify({ mutation: { success: true, output, error: null }, effectiveReadOnly: true, logs }) }] });
const refused = (code: string, extra: Record<string, unknown> = {}): CallToolResult =>
  ({ content: [{ type: "text", text: JSON.stringify({ mutation: { success: false, output: extra, error: { code, message: code } } }) }], isError: true });

/** The Unity 6 Device Simulator catalog, as list_devices reports it. */
const CATALOG: DeviceInfo[] = [
  { id: "Foldable Device (960x2658)", screen: [960, 2658], dpi: 480, insets: { top: 92 }, notch: true },
  { id: "Punch Hole Center (1440x3088)", screen: [1440, 3088], dpi: 560, insets: { top: 89 }, notch: true },
  { id: "Punch Hole Left (1080x2340)", screen: [1080, 2340], dpi: 440, insets: { top: 136 }, notch: true },
  { id: "Punch Hole Right (1080x2280)", screen: [1080, 2280], dpi: 480, insets: { top: 116 }, notch: true },
  { id: "Tablet Large (2048x2732)", screen: [2048, 2732], dpi: 264, insets: { bottom: 40 }, notch: false },
  { id: "Tablet Small (1536x2048)", screen: [1536, 2048], dpi: 326, insets: {}, notch: false },
  { id: "Wide Pill Cutout (1200x2640)", screen: [1200, 2640], dpi: 530, insets: { top: 141 }, notch: true },
  { id: "iOS Classic (750x1334)", screen: [750, 1334], dpi: 326, insets: {}, notch: false },
  { id: "iOS Notch Device (1170x2532)", screen: [1170, 2532], dpi: 460, insets: { top: 141, bottom: 102 }, notch: true },
  { id: "iOS Notch Device Large (1284x2778)", screen: [1284, 2778], dpi: 458, insets: { top: 141, bottom: 102 }, notch: true },
  { id: "iOS Notch Device Small (1080x2338)", screen: [1080, 2338], dpi: 476, insets: { top: 145, bottom: 97 }, notch: true },
].map(d => ({ ...d, orientations: ["portrait", "portrait_upside_down", "landscape_left", "landscape_right"] }));

test("devices: presets over the Simulator catalog, resolution by id or unique substring, orientations", () => {
  assert.ok(Math.abs(diagonalInches(CATALOG[7]) - 4.7) < 0.05);
  assert.ok(isTablet(CATALOG[5]) && !isTablet(CATALOG[8]));
  assert.deepEqual(selectPreset(CATALOG, "common").map(d => d.id), [
    "Foldable Device (960x2658)", // tallest phone
    "Tablet Small (1536x2048)", // smallest tablet
    "iOS Classic (750x1334)", // smallest phone
    "iOS Notch Device Small (1080x2338)", // deepest top inset
  ]);
  assert.equal(selectPreset(CATALOG, "phones").length, 9);
  assert.equal(selectPreset(CATALOG, "tablets").length, 2);
  assert.equal(selectPreset(CATALOG, "notch").length, 8);
  assert.equal(selectPreset(CATALOG, "all").length, 11);

  const { matched, errors } = resolveDevices(CATALOG, ["ios classic", "Tablet Large", "iOS Notch Device (1170x2532)", "iOS Notch", "Pixel"]);
  assert.deepEqual(matched.map(d => d.id), ["iOS Classic (750x1334)", "Tablet Large (2048x2732)", "iOS Notch Device (1170x2532)"]);
  assert.equal(errors.length, 2);
  assert.match(errors[0], /'iOS Notch' matches 3 devices/);
  assert.match(errors[1], /no device matches 'Pixel'/);

  assert.deepEqual(normalizeOrientations(undefined), ["portrait"]);
  assert.deepEqual(normalizeOrientations(["portrait", "landscape", "landscape_left"]), ["portrait", "landscape_left"]);
  assert.throws(() => normalizeOrientations(["sideways"]), /Unknown orientation/);

  const grouped = groupAcrossDevices([
    { kind: "outside_safe_area", severity: "bug", path: "C/Top", detail: "top", device: "A", orientation: "portrait" },
    { kind: "outside_safe_area", severity: "bug", path: "C/Top", detail: "top", device: "B", orientation: "portrait" },
    { kind: "small_touch_target", severity: "suspicious", path: "C/I", detail: "small", device: "A", orientation: "portrait" },
  ], 4);
  assert.deepEqual(grouped.map(g => [g.kind, g.count, g.of, g.on]), [
    ["outside_safe_area", 2, 4, ["A (portrait)", "B (portrait)"]],
    ["small_touch_target", 1, 4, ["A (portrait)"]],
  ]);
});

test("devices: options — devices or preset, screens and explore options, limits", () => {
  const o = parseDevicesOptions({});
  assert.equal(o.preset, "common");
  assert.equal(o.screens, "current");
  assert.deepEqual(o.orientations, ["portrait"]);
  assert.equal(o.maxDevices, 8);
  assert.equal(o.minTouchDp, 44);
  assert.throws(() => parseDevicesOptions({ devices: ["a"], preset: "all" }), /devices or preset/);
  assert.throws(() => parseDevicesOptions({ preset: "watches" }), /Unknown preset/);
  assert.throws(() => parseDevicesOptions({ explore: { max_steps: 5 } }), /only with screens/);
  const crawl = parseDevicesOptions({ screens: "explore", explore: { max_steps: 5, restart: "none" }, min_touch_dp: 48 });
  assert.equal(crawl.explore.maxSteps, 5);
  assert.equal(crawl.explore.restart, "none");
  assert.equal(crawl.explore.minTouchDp, 48, "the crawl follows the matrix's touch size unless it sets its own");
  assert.equal(parseDevicesOptions({ max_devices: 99 }).maxDevices, 20);
});

// ---------------------------------------------------------------------------
// A fake Simulator: a screen whose top bar sits under any notch
// ---------------------------------------------------------------------------

class FakeSimulator {
  view: { mode: string; device?: string; orientation?: string; size?: string } = { mode: "game", size: "Free Aspect" };
  mode = "play";
  calls: Array<{ tool: string; args: Record<string, unknown> }> = [];
  /** set_device answers: per device id. */
  behaviour: Record<string, "not_settled" | "no_rotation"> = {};
  restarts = 0;
  screen = "Menu";
  private shots = 0;
  constructor(readonly frames?: string) {}

  device(): DeviceInfo | undefined { return CATALOG.find(d => d.id === this.view.device); }

  async call(tool: string, args: Record<string, unknown>): Promise<CallToolResult> {
    this.calls.push({ tool, args });
    if (tool === "unity_open_mcp_game_view") {
      if (args.action === "get") return ok({ status: "ok", action: "get", mode: this.view.mode, size: { label: this.view.size }, screen: { width: 1600, height: 900, orientation: "landscape" } });
      if (args.action === "list_devices") return ok({ status: "ok", action: "list_devices", total: CATALOG.length, devices: CATALOG, pagination: { next_cursor: null } });
      if (args.action === "set_resolution") { this.view = { mode: "game", size: String(args.size_label) }; return ok({ status: "ok", action: "set_resolution", settled: true }); }
      if (args.action === "set_device") {
        const d = CATALOG.find(x => x.id === args.device)!;
        if (this.behaviour[d.id] === "not_settled") return refused("not_settled");
        const rotated = args.orientation !== "portrait" && this.behaviour[d.id] !== "no_rotation";
        this.view = { mode: "simulator", device: d.id, orientation: String(args.orientation) };
        const [w, h] = rotated ? [d.screen[1], d.screen[0]] : d.screen;
        return ok({ status: "ok", action: "set_device", mode: "simulator", settled: true, orientation_applied: args.orientation === "portrait" || rotated,
          device: { id: d.id }, screen: { width: w, height: h, dpi: d.dpi, insets: d.insets, safe_area: [0, 0, w, h - (d.insets?.top ?? 0)] } });
      }
    }
    if (tool === "unity_open_mcp_ui_snapshot") {
      const d = this.device();
      const notch = (d?.insets?.top ?? 0) > 0 && this.view.orientation === "portrait";
      return ok({
        status: "ok", mode: this.mode, screen: { dpi: d?.dpi ?? 160 },
        nodes: [{ id: "1", path: "C/Top", role: "text", text: this.screen === "Menu" ? "Coins: 5" : "Shop", visible: true },
          ...(this.screen === "Menu" ? [{ id: "2", path: "C/Shop", role: "button", text: "Shop", visible: true, interactable: true }]
            : [{ id: "3", path: "C/Back", role: "button", text: "Back", visible: true, interactable: true }])],
        diagnostics: {
          ...(notch ? { outside_safe_area: { count: 1, items: [{ id: "1", path: "C/Top", role: "text", text: "Coins: 5", edges: ["top"] }], truncated: 0 } } : {}),
          ...(d && isTablet(d) ? { overflow: { count: 1, items: [{ id: "4", path: "C/Popup/Body", text: "Long", size: [10, 10], content: [20, 10] }], truncated: 0 } } : {}),
        },
      });
    }
    if (tool === "unity_open_mcp_inputsim_pointer") {
      this.screen = args.object_id === "2" ? "Shop" : "Menu";
      return ok({ status: "ok", clicked: true });
    }
    if (tool === "unity_senses_screenshot") {
      const file = this.frames ? join(this.frames, `device-${++this.shots}.png`) : `/tmp/device-${++this.shots}.png`;
      if (this.frames) writeFileSync(file, encodePng({ width: 8, height: 8, data: new Uint8Array(256).fill(90) }));
      return ok({ status: "ok", filePath: file });
    }
    if (tool === "unity_open_mcp_editor_set_state") {
      if (args.state === "play") { this.restarts++; this.screen = "Menu"; }
      return ok({ status: "ok" });
    }
    return ok({ status: "ok" });
  }
}

function deps(sim: FakeSimulator, signal = new AbortController().signal) {
  const labels: string[] = [];
  const crawls: Array<{ device: string; result: Record<string, any> }> = [];
  const recorder = { begin: (label: string) => { labels.push(label); return { seq: labels.length, finish() {} }; } };
  return {
    labels, crawls,
    deps: { call: (t: string, a: Record<string, unknown>) => sim.call(t, a), check: recorder, explore: recorder, signal,
      onCrawl: (result: Record<string, unknown>, device: string) => { crawls.push({ device, result }); } },
  };
}

test("devices engine: the current screen on each device, findings grouped by device, the view put back", async () => {
  const sim = new FakeSimulator();
  sim.behaviour["iOS Classic (750x1334)"] = "no_rotation";
  sim.behaviour["Tablet Small (1536x2048)"] = "not_settled";
  const d = deps(sim);
  const result = await new DeviceMatrix(parseDevicesOptions({ orientations: ["portrait", "landscape"] }), d.deps).run() as Record<string, any>;
  assert.equal(result.stop_reason, "done");
  assert.deepEqual(result.entries.map((e: any) => `${e.device} ${e.orientation} ${e.size}`), [
    "Foldable Device (960x2658) portrait 960x2658",
    "Foldable Device (960x2658) landscape_left 2658x960",
    "iOS Classic (750x1334) portrait 750x1334",
    "iOS Notch Device Small (1080x2338) portrait 1080x2338",
    "iOS Notch Device Small (1080x2338) landscape_left 2338x1080",
  ]);
  assert.deepEqual(result.skipped.map((s: any) => `${s.device} ${s.orientation} ${s.reason}`), [
    "Tablet Small (1536x2048) portrait not_settled",
    "Tablet Small (1536x2048) landscape_left not_settled",
    "iOS Classic (750x1334) landscape_left orientation_not_applied",
  ]);
  // The notch bar is a finding on the notch phones in portrait only, grouped across devices.
  const top = result.matrix.find((g: any) => g.kind === "outside_safe_area");
  assert.deepEqual(top.on, ["Foldable Device (960x2658) (portrait)", "iOS Notch Device Small (1080x2338) (portrait)"]);
  assert.equal(top.of, 5);
  assert.deepEqual(result.finding_counts, { bug: 2, suspicious: 0 });
  // Touch size follows each device's dpi; one frame per check; the Game view goes back to Free Aspect.
  const snapshots = sim.calls.filter(c => c.tool === "unity_open_mcp_ui_snapshot");
  assert.deepEqual(snapshots.map(c => c.args.min_touch_size), [132, 132, 90, 131, 131]);
  assert.equal(sim.calls.filter(c => c.tool === "unity_senses_screenshot").length, 5);
  assert.deepEqual(sim.calls.at(-1), { tool: "unity_open_mcp_game_view", args: { action: "set_resolution", size_label: "Free Aspect", settle_timeout_ms: 10000 } });
  assert.deepEqual(result.restored, { restored: true, to: { action: "set_resolution", size_label: "Free Aspect" } });
  assert.ok(d.labels.includes("Check on iOS Classic (750x1334) (portrait)"));
  assert.ok(result.entries.every((e: any) => typeof e.frame === "string"));
});

test("devices engine: a crawl per device restarts the game between devices; cancel still puts the view back; no Simulator fails", async () => {
  const sim = new FakeSimulator();
  const d = deps(sim);
  const options = parseDevicesOptions({ devices: ["iOS Classic", "iOS Notch Device (1170x2532)"], screens: "explore", explore: { settle_ms: 50, max_settle_ms: 100 } });
  const result = await new DeviceMatrix(options, d.deps).run() as Record<string, any>;
  assert.equal(result.stop_reason, "done");
  assert.deepEqual(d.crawls.map(c => [c.device, c.result.stop_reason, (c.result.coverage as any).screens]), [
    ["iOS Classic (750x1334)", "explored", 2], ["iOS Notch Device (1170x2532)", "explored", 2],
  ]);
  assert.equal(sim.restarts, 1, "the game restarts once, before the second device");
  assert.ok(d.labels.includes("Restart the game for iOS Notch Device (1170x2532) (portrait)"));
  assert.ok(result.matrix.some((g: any) => g.kind === "outside_safe_area" && g.count === 1), JSON.stringify(result.matrix));
  assert.equal(result.entries[0].screens, 2);

  const cancelled = new FakeSimulator();
  const controller = new AbortController();
  const original = cancelled.call.bind(cancelled);
  cancelled.call = async (tool, args) => {
    if (tool === "unity_senses_screenshot") controller.abort();
    return original(tool, args);
  };
  const c = await new DeviceMatrix(parseDevicesOptions({}), deps(cancelled, controller.signal).deps).run() as Record<string, any>;
  assert.equal(c.stop_reason, "cancelled");
  assert.equal(c.entries.length, 1);
  assert.equal(c.restored.restored, true);

  const old = new FakeSimulator();
  const base = old.call.bind(old);
  old.call = async (tool, args) => (tool === "unity_open_mcp_game_view" && args.action === "set_device" ? refused("unsupported_editor_version") : base(tool, args));
  await assert.rejects(new DeviceMatrix(parseDevicesOptions({}), deps(old).deps).run(), (e: any) => e.code === "unsupported_editor_version");
  assert.equal(old.view.size, "Free Aspect", "put back even when the run fails");
  await assert.rejects(new DeviceMatrix(parseDevicesOptions({ devices: ["Pixel"] }), deps(new FakeSimulator()).deps).run(), (e: any) => e.code === "device_not_found");
});

test("playtest_devices: schema, playtest group, server-local, intent and chain", () => {
  const tool = ALL_TOOLS.find(t => t.name === PLAYTEST_DEVICES_TOOL)!;
  assert.ok(tool && PLAYTEST_TOOLS.includes(tool));
  assert.equal(groupFor(PLAYTEST_DEVICES_TOOL), "playtest");
  assert.ok(isLocalTool(PLAYTEST_DEVICES_TOOL));
  const props = tool.inputSchema.properties as Record<string, any>;
  const explore = ALL_TOOLS.find(t => t.name === "unity_open_mcp_playtest_explore")!.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props.explore.properties).sort(), Object.keys(explore).filter(k => k !== "allow_unsandboxed").sort());
  assert.deepEqual(props.preset.enum, ["common", "phones", "tablets", "notch", "all"]);
  for (const [key, value] of Object.entries(props)) assert.equal((value as any).default, undefined, key);
  for (const phrase of ["jobs job", "preset", "notch", "orientation", "matrix[]", "no_open_session", "put back"])
    assert.ok(tool.description!.includes(phrase), phrase);
  for (const intent of ["check the layout on several devices", "test the shop screen on phones and tablets with a notch"]) {
    const ids = recommendGroups({ intent }).groups.map(g => g.id);
    assert.ok(ids.includes("playtest"), `${intent}: ${JSON.stringify(ids)}`);
  }
  assert.ok(RECOMMENDED_TOOL_CHAINS.some(c => c.name === "playtest-device-matrix"));
});

test("router: playtest_devices records each device into the session and the report gets a Devices section", async t => {
  const root = mkdtempSync(join(tmpdir(), "uom-devices-"));
  const project = join(root, "project");
  mkdirSync(join(project, "ProjectSettings"), { recursive: true });
  writeFileSync(join(project, "ProjectSettings", "ProjectSettings.asset"), "PlayerSettings:\n  companyName: TestCo\n  productName: Devices\n");
  const frames = join(root, "captures");
  mkdirSync(frames);
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const sim = new FakeSimulator(frames);
  const live = {
    isLiveAvailable: async () => true,
    route: async (name: string, args: Record<string, unknown>) => {
      if (name === "unity_open_mcp_player_state") return ok({ status: "ok", action: args.action, company: "TestCo", product: "Devices", identical: true, name: args.name });
      return sim.call(name, args);
    },
  } as unknown as LiveClient;
  const stream = { addListener: () => () => {}, ensureSubscription: () => false, reconnectNow: () => {} };
  const router = new ToolRouter(live, { isBatchTool: () => false } as unknown as BatchSpawn, project, stream as unknown as BridgeEventStream, new ToolSessionState());
  router.usePlaytestRecorder(new PlaytestRecorder({ root: join(root, "sessions"), projectPath: project, pid: 4242, isAlive: () => false }));
  t.after(() => router.closeJobs());
  const me = { agent: "agent-devices" };

  assert.equal(body(await router.route(PLAYTEST_DEVICES_TOOL, {}, me)).error.code, "no_open_session");
  assert.equal(body(await router.route("unity_open_mcp_playtest_session", { action: "start", name: "Devices" }, me)).status, "ok");
  assert.equal(body(await router.route(PLAYTEST_DEVICES_TOOL, { screens: "explore" }, me)).error.code, "not_isolated", "a crawl per device needs isolation");
  const started = body(await router.route(PLAYTEST_DEVICES_TOOL, { devices: ["iOS Classic", "iOS Notch Device Small"] }, me));
  assert.equal(started.status, "running", JSON.stringify(started));
  let job: Record<string, any> = {};
  for (let i = 0; i < 50 && !["succeeded", "failed", "cancelled", "orphaned"].includes(job.state); i++)
    job = body(await router.route("unity_open_mcp_jobs", { action: "wait", job_id: started.job_id, timeout_ms: 5000 }, me));
  assert.equal(job.state, "succeeded", JSON.stringify(job.error));
  assert.equal(job.result.checked, 2);
  assert.ok(job.result.agentNextSteps.some((s: string) => s.includes("put back")));

  const ended = body(await router.route("unity_open_mcp_playtest_session", { action: "end" }, me));
  assert.deepEqual(ended.summary.device_runs.map((r: any) => [r.screens, r.checked, r.restored]), [["current", 2, true]]);
  const report = readFileSync(ended.report, "utf8");
  for (const phrase of ["## Devices", "### Devices run 1 — the current screen — done", "| iOS Notch Device Small (1080x2338) | portrait | 1080×2338 | top 145, bottom 97 | bug 1",
    "Findings by element", "on 1 of 2: iOS Notch Device Small (1080x2338) (portrait)", "on iOS Notch Device Small (1080x2338) (portrait)", "Devices: 2 checked (portrait)"])
    assert.ok(report.includes(phrase), phrase);
  assert.match(report, /\[!\[iOS Classic \(750x1334\)\]\(thumbs\/[^)]+\)\]\(frames\/[^)]+\.png\)/);
});
