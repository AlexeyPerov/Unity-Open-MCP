// Playtest visual baselines (playtest_replay with settings.visual): the frame
// comparison (noise floor, masks, regions, the controls a region covers), the
// baseline store next to the scenario, the scenario setting, the replay engine
// against a fake game that draws its screens (record, pass, a changed colour, a
// masked area, a counter, no baseline for the key) and the router path — record
// writes <scenario>.visual/<key>/, a regression fails with the diff in the report.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { ToolRouter } from "./tool-router.js";
import { ToolSessionState } from "./tool-session-state.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import type { BridgeEventStream } from "./event-stream.js";
import { PLAYTEST_REPLAY_TOOL } from "./tools/playtest-replay.js";
import { PlaytestRecorder } from "./playtest/recorder.js";
import { decodePng, encodePng, type RgbaImage } from "./playtest/png.js";
import type { ExploreRecorder } from "./playtest/explore.js";
import { Replayer, type ReplayOptions, type ReplayVisualDeps } from "./playtest/replay.js";
import { SCENARIO_FORMAT, VISUAL_SETTING_DEFAULTS, parseScenario, readScenarioFile, writeScenarioFile, type Scenario } from "./playtest/scenario.js";
import {
  compareImages, describeRegion, diffImage, masksFor, nodesInRegion, screenRectToBox, visualKey, type VisualResult,
} from "./playtest/visual.js";
import { VisualBaselines, visualDirFor, visualKeys, writeVisualBaseline } from "./playtest/visual-store.js";

const body = (result: CallToolResult) => JSON.parse((result.content.find(c => c.type === "text") as { text: string }).text);
const text = (value: unknown): CallToolResult => ({ content: [{ type: "text", text: JSON.stringify(value) }] });
const ok = (output: unknown, logs: unknown[] = []): CallToolResult =>
  text({ mutation: { success: true, output, error: null }, effectiveReadOnly: true, logs });

function blank(width: number, height: number, rgb: [number, number, number] = [20, 30, 40]): RgbaImage {
  const data = new Uint8Array(width * height * 4);
  for (let i = 0; i < data.length; i += 4) { data[i] = rgb[0]; data[i + 1] = rgb[1]; data[i + 2] = rgb[2]; data[i + 3] = 255; }
  return { width, height, data };
}
function fill(image: RgbaImage, x0: number, y0: number, w: number, h: number, rgb: [number, number, number]): RgbaImage {
  for (let y = y0; y < y0 + h; y++) for (let x = x0; x < x0 + w; x++) {
    const i = (y * image.width + x) * 4;
    image.data[i] = rgb[0]; image.data[i + 1] = rgb[1]; image.data[i + 2] = rgb[2];
  }
  return image;
}
const opts = (masks: Array<[number, number, number, number]> = []) => ({ pixelTolerance: 24, threshold: 0.0002, masks });

// ---------------------------------------------------------------------------
// Comparison
// ---------------------------------------------------------------------------

test("visual: identical frames pass; a changed block fails with its region; stray pixels are noise", () => {
  const a = blank(200, 120), b = blank(200, 120);
  const same = compareImages(a, b, opts()) as VisualResult;
  assert.equal(same.ratio, 0);
  assert.ok(same.pass);

  fill(b, 100, 40, 20, 20, [250, 200, 0]);
  const changed = compareImages(a, b, opts()) as VisualResult;
  assert.equal(changed.mismatched, 400);
  assert.ok(!changed.pass);
  assert.equal(changed.regions.length, 1);
  const [x, y, w, h] = changed.regions[0];
  assert.ok(x <= 100 && y <= 40 && x + w >= 120 && y + h >= 60, JSON.stringify(changed.regions));
  assert.equal(describeRegion(changed.regions[0], b), "x 48–64%, y 27–53%");

  // One differing pixel per block (anti-aliased edges): under the noise floor.
  const noisy = blank(200, 120);
  for (let i = 0; i < 12; i++) fill(noisy, i * 16 + 3, 5, 1, 1, [255, 255, 255]);
  const noise = compareImages(a, noisy, opts()) as VisualResult;
  assert.equal(noise.mismatched, 12);
  assert.equal(noise.changed, 0);
  assert.ok(noise.pass);
  // Under the per-channel tolerance: not a difference at all.
  assert.equal((compareImages(a, fill(blank(200, 120), 0, 0, 200, 120, [30, 40, 50]), opts()) as VisualResult).mismatched, 0);

  const masked = compareImages(a, b, opts([[96, 36, 30, 30]])) as VisualResult;
  assert.ok(masked.pass, "a masked change is not compared");
  assert.equal(masked.compared, 200 * 120 - 30 * 30);

  assert.deepEqual(compareImages(a, blank(100, 60), opts()), { sizeMismatch: { baseline: [200, 120], current: [100, 60] } });
  assert.equal(describeRegion([0, 0, 200, 100], b), "across the whole screen");
  const diff = diffImage(b, changed);
  assert.equal(diff.width, 200);
  assert.deepEqual([...diff.data.slice((50 * 200 + 110) * 4, (50 * 200 + 110) * 4 + 3)], [255, 32, 32], "changed pixels are red");
});

test("visual: snapshot rects as image boxes, masks by path and digits, the controls a region covers, keys", () => {
  const screen = { w: 400, h: 300 }, image = { width: 200, height: 150 };
  assert.deepEqual(screenRectToBox([20, 250, 100, 40], screen, image), [10, 5, 50, 20], "bottom-left origin → top-left");
  const nodes = [
    { path: "Canvas/Menu/Title", role: "text", text: "Main Menu", visible: true, rect: [20, 250, 200, 40] },
    { path: "Canvas/Menu/Coins", role: "text", text: "Coins: 120", visible: true, rect: [260, 250, 120, 40] },
    { path: "Canvas/Menu/ShopButton", role: "button", text: "Shop", visible: true, rect: [20, 150, 160, 40] },
    { path: "Canvas/Menu/Ad/Banner", role: "image", visible: true, rect: [200, 20, 180, 60] },
    { path: "Canvas/Menu", role: "container", visible: true, rect: [0, 0, 400, 300] },
  ];
  const masks = masksFor(nodes, screen, image, ["Ad", [0, 0, 0.1, 0.1]], true);
  assert.deepEqual(masks, [[0, 0, 20, 15], [130, 5, 60, 20], [100, 110, 90, 30]], "normalized box, the digit text, the Ad subtree");
  assert.equal(masksFor(nodes, screen, image, [], false).length, 0);
  // The region over the Shop button names it (the full-screen container does not count).
  assert.deepEqual(nodesInRegion([16, 48, 48, 16], nodes, screen, image), ["button 'Shop' (Menu/ShopButton)"]);
  assert.equal(visualKey({ mode: "simulator", device: "iOS Notch Device Small (1080x2338)", orientation: "landscape_left", screen: [2338, 1080] }, "darwin"),
    "darwin-ios-notch-device-small-1080x2338-landscape-left");
  assert.equal(visualKey({ mode: "game", size: "Free Aspect", screen: [2422, 1483] }, "win32"), "win32-game-2422x1483");
});

test("visual store: a key's folder is replaced as a whole; frames of other steps are not used", t => {
  const root = mkdtempSync(join(tmpdir(), "uom-visual-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const scenarioFile = join(root, "Playtests", "Scenarios", "menu.json");
  const dir = visualDirFor(scenarioFile);
  assert.equal(dir, join(root, "Playtests", "Scenarios", "menu.visual"));
  const frame = (name: string, rgb: [number, number, number]) => { const f = join(root, `${name}.png`); writeFileSync(f, encodePng(blank(8, 8, rgb))); return f; };
  writeVisualBaseline(dir, "darwin-game-400x300", [{ n: 0, label: "start", source: frame("a", [1, 2, 3]) }, { n: 1, label: "Tap 'Shop'", source: frame("b", [9, 9, 9]) }], { max_edge: 640 });
  assert.deepEqual(readdirSync(join(dir, "darwin-game-400x300")).sort(), ["00.png", "01.png", "index.json"]);
  writeVisualBaseline(dir, "darwin-game-400x300", [{ n: 0, label: "start", source: frame("c", [4, 5, 6]) }], { max_edge: 640 });
  assert.deepEqual(readdirSync(join(dir, "darwin-game-400x300")).sort(), ["00.png", "index.json"], "stale frames are gone");
  assert.deepEqual(visualKeys(dir), ["darwin-game-400x300"]);
  const store = new VisualBaselines(dir, "darwin-game-400x300");
  const start = store.frame(0, "start");
  assert.ok("image" in start && start.image.data[0] === 4);
  assert.deepEqual(store.frame(1, "Tap 'Shop'"), { missing: "no_step" });
  assert.equal((new VisualBaselines(dir, "win32-game-400x300").frame(0, "start") as { missing: string }).missing, "no_key");
  writeVisualBaseline(dir, "k", [{ n: 2, label: "Tap 'Play'", source: frame("d", [1, 1, 1]) }], { max_edge: 640 });
  assert.equal((new VisualBaselines(dir, "k").frame(2, "Tap 'Settings'") as { missing: string }).missing, "other_step");
});

test("scenario: settings.visual true / object / invalid; a step's visual:false", () => {
  const base = { format: SCENARIO_FORMAT, start: { restart: "play_mode" }, steps: [{ do: "check", visual: false }] };
  const on = parseScenario({ ...base, settings: { visual: true } });
  assert.deepEqual(on.scenario?.settings.visual, VISUAL_SETTING_DEFAULTS);
  assert.equal(on.scenario?.steps[0].visual, false);
  const custom = parseScenario({ ...base, settings: { visual: { threshold: 0.01, mask: ["HUD/Coins", [0, 0, 1, 0.1]], mask_numbers: false } } });
  assert.deepEqual(custom.problems, []);
  assert.equal(custom.scenario?.settings.visual?.threshold, 0.01);
  assert.equal(custom.scenario?.settings.visual?.mask_numbers, false);
  const bad = parseScenario({ ...base, settings: { visual: { threshold: 2, max_edge: 10, mask: [[0, 0, 2, 1]] } }, steps: [{ do: "check", visual: true }] });
  for (const part of ["threshold must be", "max_edge must be", "neither a path", "visual may only be false"]) assert.ok(bad.problems.some(p => p.includes(part)), part);
  assert.equal(parseScenario(base).scenario?.settings.visual, undefined);
});

// ---------------------------------------------------------------------------
// A fake game that draws its screens
// ---------------------------------------------------------------------------

interface Button { label: string; to?: string; color?: [number, number, number] }
interface Screen { title: string; buttons: Button[]; coins?: number; ad?: boolean }
const SCREEN = { w: 400, h: 300 };

const hue = (s: string): [number, number, number] => {
  let h = 7;
  for (const c of s) h = (h * 31 + c.charCodeAt(0)) >>> 0;
  return [60 + (h % 180), 60 + ((h >> 8) % 180), 60 + ((h >> 16) % 180)];
};

/** An animated area: a different picture every capture. */
const pulse = (n: number): [number, number, number] => [(n * 97) % 256, (n * 53) % 256, (n * 151) % 256];

class DrawnGame {
  screen = "Menu";
  boots = 0;
  shots = 0;
  /** Frames change each capture here (an animation that never stops). */
  flicker = false;
  constructor(readonly dir: string, public screens: Record<string, Screen> = {
    Menu: { title: "Main Menu", coins: 120, ad: true, buttons: [{ label: "Play", to: "Game" }, { label: "Shop", to: "Shop" }] },
    Game: { title: "Level", buttons: [{ label: "Back", to: "Menu" }] },
    Shop: { title: "Shop", buttons: [{ label: "Close", to: "Menu" }] },
  }) {}

  nodes(): Array<Record<string, any>> {
    const s = this.screens[this.screen];
    const out: Array<Record<string, any>> = [
      { id: `${this.screen}#title`, path: `Canvas/${this.screen}/Title`, role: "text", text: s.title, visible: true, rect: [20, 250, 200, 40] },
    ];
    if (s.coins !== undefined) out.push({ id: `${this.screen}#coins`, path: `Canvas/${this.screen}/Coins`, role: "text", text: `Coins: ${s.coins}`, visible: true, rect: [260, 250, 120, 40] });
    if (s.ad) out.push({ id: `${this.screen}#ad`, path: `Canvas/${this.screen}/Ad`, role: "image", visible: true, rect: [200, 20, 180, 60] });
    s.buttons.forEach((b, i) => out.push({
      id: `${this.screen}#${i}`, path: `Canvas/${this.screen}/${b.label}Button`, role: "button", text: b.label, visible: true, interactable: true,
      rect: [20, 190 - i * 60, 160, 40],
    }));
    return out;
  }

  draw(): RgbaImage {
    const image = blank(200, 150, hue(this.screen));
    for (const node of this.nodes()) {
      const [x, y, w, h] = screenRectToBox(node.rect, SCREEN, image)!.map(Math.round);
      const button = this.screens[this.screen].buttons.find(b => node.text === b.label);
      const color = button?.color ?? hue(`${node.path}|${node.text ?? ""}`);
      fill(image, x, y, w, h, node.path.endsWith("/Ad") ? pulse(this.shots) : color);
    }
    if (this.flicker) fill(image, 0, 0, 40, 40, pulse(this.shots));
    return image;
  }

  async call(tool: string, args: Record<string, unknown>): Promise<CallToolResult> {
    switch (tool) {
      case "unity_open_mcp_ui_snapshot":
        return ok({ status: "ok", mode: "play", screen: { ...SCREEN, dpi: 160 }, nodes: this.nodes(), diagnostics: {} });
      case "unity_open_mcp_inputsim_pointer": {
        const [name, index] = String(args.object_id).split("#");
        const b = this.screens[name].buttons[Number(index)];
        if (b.to) this.screen = b.to;
        return ok({ status: "ok", target: `Canvas/${name}/${b.label}Button` });
      }
      case "unity_open_mcp_project_commands":
        this.boots++;
        this.screen = "Menu";
        return ok({ result: { booted: true } });
      case "unity_senses_screenshot": {
        this.shots++;
        const file = join(this.dir, `shot-${this.shots}.png`);
        writeFileSync(file, encodePng(this.draw()));
        return ok({ status: "ok", filePath: file, capturedAt: { mode: "game", size: "Free Aspect", screen: [SCREEN.w, SCREEN.h], playing: true } });
      }
      default:
        return ok({ status: "ok" });
    }
  }
}

function scenario(extra: Record<string, unknown> = {}): Scenario {
  const { scenario: parsed, problems } = parseScenario({
    format: SCENARIO_FORMAT, name: "menu", start: { restart: "command", command: "project.demo.playtest.boot" },
    settings: { visual: { mask: ["Ad"] } },
    steps: [{ do: "tap", target: { path: "Canvas/Menu/ShopButton", text: "Shop" } }, { do: "tap", target: { path: "Canvas/Shop/CloseButton", text: "Close" } }],
    ...extra,
  });
  assert.deepEqual(problems, []);
  return parsed!;
}

function memoryVisual(frames: Map<string, { file: string; label: string }>): ReplayVisualDeps & { diffs: string[] } {
  const diffs: string[] = [];
  return {
    diffs,
    baseline(key, n, label) {
      const f = frames.get(`${key}|${n}`);
      if (!f) return { missing: "no_key" };
      if (f.label !== label) return { missing: "other_step" };
      return { file: f.file, image: decodePng(readFileSync(f.file))! };
    },
    read: file => decodePng(readFileSync(file)),
    writeDiff(image, name) { const file = `/tmp/${name}-${diffs.length}.png`; diffs.push(file); return file; },
  };
}

async function run(game: DrawnGame, sc: Scenario, visual: ReplayVisualDeps, overrides: Partial<ReplayOptions> = {}) {
  let t = 0;
  const steps: Array<Record<string, any>> = [];
  const rec: ExploreRecorder = { begin() { return { seq: steps.length + 1, finish: o => { steps.push(o); } }; } };
  const out = await new Replayer(sc, {
    from: "start", retries: 0, record: false, strict: false, capture: "failures", vars: {}, ignoreErrors: [], placeholderPattern: null,
    minTouchDp: null, settleMs: 250, maxSettleMs: 1000, stepTimeoutMs: 3000, sameScreen: 0.8, maxMinutes: 20,
    visual: sc.settings.visual ?? null, ...overrides,
  }, { call: (tool, args) => game.call(tool, args), recorder: rec, signal: new AbortController().signal, now: () => t, sleep: async ms => { t += ms; }, visual }).run();
  return { ...out, result: out.result as Record<string, any>, outputs: steps };
}

test("replay visual: record keeps settled frames; a run compares them; a colour change fails with the region; masks and counters do not", async t => {
  const dir = mkdtempSync(join(tmpdir(), "uom-visual-game-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const game = new DrawnGame(dir);
  const frames = new Map<string, { file: string; label: string }>();
  const store = memoryVisual(frames);

  const recorded = await run(game, scenario(), store, { record: true });
  assert.equal(recorded.result.verdict, "recorded");
  const key = `${process.platform}-game-400x300`;
  assert.deepEqual(recorded.frames.map(f => [f.n, f.label, f.key]), [[0, "start", key], [1, "Tap 'Shop'", key], [2, "Tap 'Close'", key]]);
  assert.equal(recorded.result.visual.recorded, 3);
  assert.ok(recorded.recorded?.settings.visual, "the recorded scenario keeps its visual settings");
  for (const f of recorded.frames) frames.set(`${f.key}|${f.n}`, { file: f.source, label: f.label });

  // The ad banner animates (masked) and the coin counter ticks (a digit text): still the same picture.
  game.screens.Menu.coins = 999;
  const same = await run(game, scenario(), store);
  assert.equal(same.result.verdict, "passed", JSON.stringify(same.result.failure));
  assert.equal(same.result.visual.passed, 3);
  assert.equal(same.result.steps[0].visual.ratio, 0);

  // The Shop button turns red: same controls and texts, another picture.
  game.screens.Menu.buttons[1].color = [220, 20, 20];
  const red = await run(game, scenario(), store);
  assert.equal(red.result.verdict, "failed");
  assert.equal(red.result.failure.n, 0, "the menu is the start screen");
  assert.equal(red.result.failure.reason, "visual_changed");
  assert.match(red.result.failure.message, /around button 'Shop' \(Menu\/ShopButton\)/);
  assert.ok(red.result.failure.visual.baseline && red.result.failure.visual.current && red.result.failure.visual.diff);
  assert.equal(red.result.failure.frame, red.result.failure.visual.current, "the compared frame is the step's frame");
  assert.ok(red.outputs[0].visual_files.length === 2, "the baseline and the diff go to the report");

  // Without the masks the banner alone fails it; a step opted out is not compared.
  game.screens.Menu.buttons[1].color = undefined;
  const unmasked = await run(game, scenario({ settings: { visual: { mask: [] } } }), store);
  assert.equal(unmasked.result.failure?.reason, "visual_changed");
  const optedOut = await run(game, scenario({ settings: { visual: { mask: ["Ad"] } }, steps: [{ do: "tap", target: { path: "Canvas/Menu/ShopButton", text: "Shop" }, visual: false }] }), store);
  assert.equal(optedOut.result.steps[1].visual, undefined);
});

test("replay visual: no baseline for this key is reported, not failed; a never-still picture is recorded with a note", async t => {
  const dir = mkdtempSync(join(tmpdir(), "uom-visual-game-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const game = new DrawnGame(dir);
  const none = await run(game, scenario(), memoryVisual(new Map()));
  assert.equal(none.result.verdict, "passed");
  assert.equal(none.result.visual.unbaselined, 3);
  assert.match(none.result.visual.notes[0], /no visual baseline for/);

  game.flicker = true;
  const moving = await run(game, scenario(), memoryVisual(new Map()), { record: true });
  assert.equal(moving.result.verdict, "recorded");
  assert.match(moving.result.steps[0].visual.note, /kept changing/);
});

// ---------------------------------------------------------------------------
// Through the router
// ---------------------------------------------------------------------------

test("router: record with visual:true writes the frames beside the scenario; a regression shows baseline, now and diff in the report", async t => {
  const root = mkdtempSync(join(tmpdir(), "uom-visual-router-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const project = join(root, "project");
  mkdirSync(join(project, "ProjectSettings"), { recursive: true });
  writeFileSync(join(project, "ProjectSettings", "ProjectSettings.asset"), "PlayerSettings:\n  companyName: TestCo\n  productName: Visual\n");
  const shots = join(root, "captures");
  mkdirSync(shots);
  const game = new DrawnGame(shots);
  game.screens.Menu.ad = false; // visual:true uses the default settings: no mask for an animated banner
  const live = {
    isLiveAvailable: async () => true,
    projectCommands: async (a: Record<string, unknown>) => ({ command: { id: a.id, available: true, invocationSupported: true, schemaVersion: "1", lifecycle: "none", inputSchema: { type: "object", properties: {}, additionalProperties: false } } }),
    route: async (name: string, args: Record<string, unknown>) => {
      if (name === "unity_open_mcp_player_state") {
        if (args.action === "list") return ok({ status: "ok", company: "TestCo", product: "Visual" });
        return ok({ status: "ok", action: args.action, identical: true });
      }
      if (name === "unity_open_mcp_game_view") return ok({ status: "ok", action: "get", mode: "game" });
      return game.call(name, args);
    },
  } as unknown as LiveClient;
  const stream = { addListener: () => () => {}, ensureSubscription: () => false, reconnectNow: () => {} };
  const router = new ToolRouter(live, { isBatchTool: () => false } as unknown as BatchSpawn, project, stream as unknown as BridgeEventStream, new ToolSessionState());
  router.usePlaytestRecorder(new PlaytestRecorder({ root: join(root, "sessions"), projectPath: project, pid: 4242, isAlive: () => false }));
  t.after(() => router.closeJobs());
  const ME = { agent: "agent-visual" };
  const waitJob = async (jobId: string) => {
    for (let i = 0; i < 200; i++) {
      const job = body(await router.route("unity_open_mcp_jobs", { action: "wait", job_id: jobId, timeout_ms: 5000 }, ME));
      if (["succeeded", "failed", "cancelled", "orphaned"].includes(job.state)) return job;
    }
    throw new Error("job did not finish");
  };
  const file = join(project, "Playtests", "Scenarios", "menu.json");
  const plain = scenario();
  delete plain.settings.visual;
  writeScenarioFile(file, plain);
  assert.equal(body(await router.route("unity_open_mcp_playtest_session", { action: "start", name: "Visual", snapshot: true }, ME)).status, "ok");

  let job = await waitJob(body(await router.route(PLAYTEST_REPLAY_TOOL, { scenario: "menu", record: true, visual: true }, ME)).job_id);
  assert.equal(job.result.verdict, "recorded", JSON.stringify(job.result.failure));
  const key = `${process.platform}-game-400x300`;
  assert.deepEqual(job.result.baseline.visual, { key, folder: `Playtests/Scenarios/menu.visual/${key}`, frames: 3 });
  assert.ok(existsSync(join(project, "Playtests", "Scenarios", "menu.visual", key, "02.png")));
  assert.ok(readScenarioFile(file).scenario.settings.visual, "visual is now on in the file");
  assert.deepEqual(body(await router.route(PLAYTEST_REPLAY_TOOL, { action: "show", scenario: "menu" }, ME)).visual_keys, [key]);
  assert.deepEqual(body(await router.route(PLAYTEST_REPLAY_TOOL, { action: "list" }, ME)).scenarios[0].visual_keys, [key]);

  job = await waitJob(body(await router.route(PLAYTEST_REPLAY_TOOL, { scenario: "menu", retries: 0 }, ME)).job_id);
  assert.equal(job.result.verdict, "passed", JSON.stringify(job.result.failure));
  assert.equal(job.result.visual.passed, 3);
  game.screens.Shop.buttons[0].color = [10, 200, 10];
  job = await waitJob(body(await router.route(PLAYTEST_REPLAY_TOOL, { scenario: "menu", retries: 0 }, ME)).job_id);
  assert.equal(job.result.verdict, "failed");
  assert.equal(job.result.failure.reason, "visual_changed");
  assert.ok(job.result.agentNextSteps.some((s: string) => s.includes("visual baseline")));

  const ended = body(await router.route("unity_open_mcp_playtest_session", { action: "end" }, ME));
  const report = readFileSync(ended.report, "utf8");
  assert.ok(report.includes("Visual differences"), "a Visual differences block");
  assert.match(report, /\[!\[baseline\]\(thumbs\/[^)]+\)\]\(frames\/[^)]+\) \[!\[now\]\(thumbs\/[^)]+\)\]\(frames\/[^)]+\) \[!\[diff\]\(thumbs\/[^)]+\)\]\(frames\/[^)]+\)/);
  assert.ok(report.includes("visual 0%"));
  assert.match(report, /frames\/s\d+-01-01\.png/, "the baseline frame is copied into the report");
  assert.match(report, /Pictures \(`[^`]+`\): 3 passed/);
});
