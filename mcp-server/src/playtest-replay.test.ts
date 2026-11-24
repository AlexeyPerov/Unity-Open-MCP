// Playtest replay (unity_open_mcp_playtest_replay): the scenario format and its
// files, target matching with tolerance, the replay engine against a fake game
// on a fake clock (baseline, drift, strict, screen changes, retries / flaky,
// console errors from the event stream, texts, findings, secrets), the export
// from a session's calls and from a crawl, and the whole path through the real
// ToolRouter — export, record, run, a regression, the report's Replays section.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { ToolRouter } from "./tool-router.js";
import { ToolSessionState } from "./tool-session-state.js";
import { ALL_TOOLS } from "./tools/index.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import type { BridgeEvent, BridgeEventStream } from "./event-stream.js";
import { groupFor } from "./capabilities/tool-groups.js";
import { recommendGroups } from "./capabilities/intent-groups.js";
import { RECOMMENDED_TOOL_CHAINS } from "./capabilities/cost-hints.js";
import { isLocalTool } from "./local-tools.js";
import { PLAYTEST_REPLAY_TOOL } from "./tools/playtest-replay.js";
import { PlaytestRecorder } from "./playtest/recorder.js";
import { encodePng } from "./playtest/png.js";
import type { ExploreRecorder } from "./playtest/explore.js";
import { ConsoleErrorFeed, Replayer, matchTarget, type ReplayOptions } from "./playtest/replay.js";
import { exportScenario } from "./playtest/export.js";
import {
  SCENARIO_FORMAT, fillVars, parseScenario, readScenarioFile, resolveScenarioPath, usedVars, writeScenarioFile, type Scenario,
} from "./playtest/scenario.js";
import { prepareReplay } from "./playtest/replay-job.js";

const body = (result: CallToolResult) => JSON.parse((result.content.find(c => c.type === "text") as { text: string }).text);
const text = (value: unknown): CallToolResult => ({ content: [{ type: "text", text: JSON.stringify(value) }] });
const ok = (output: unknown, logs: unknown[] = []): CallToolResult =>
  text({ mutation: { success: true, output, error: null }, effectiveReadOnly: true, logs });
const err = (code: string, message = code): CallToolResult => text({ mutation: { success: false, output: { error: { code, message } }, error: null }, logs: [] });

// ---------------------------------------------------------------------------
// Scenario format and files
// ---------------------------------------------------------------------------

function scenario(steps: unknown[], extra: Record<string, unknown> = {}): Scenario {
  const { scenario: parsed, problems } = parseScenario({ format: SCENARIO_FORMAT, name: "t", start: { restart: "command", command: "project.demo.playtest.boot" }, steps, ...extra });
  assert.deepEqual(problems, []);
  return parsed!;
}

test("scenario: validation, defaults, variables", () => {
  const good = scenario([{ do: "tap", target: { path: "Canvas/Menu/ShopButton", text: "Shop" } }, { do: "type", target: { path: "Canvas/F" }, text: "${nick}" }], { vars: { nick: {} } });
  assert.equal(good.settings.same_screen, 0.8);
  assert.equal(good.settings.step_timeout_ms, 5000);
  assert.equal(good.steps[1].mode, "replace");
  assert.equal(good.steps[1].end, "end_edit");
  assert.deepEqual(usedVars(good), ["nick"]);
  assert.deepEqual(fillVars({ a: "hi ${nick}", b: ["${nick}", 3] }, { nick: "Bob" }), { a: "hi Bob", b: ["Bob", 3] });

  const { problems } = parseScenario({
    format: "nope", start: { restart: "command" }, settings: { same_screen: 2, ignore_errors: ["("] },
    steps: [
      { do: "fly" }, { do: "tap", target: { object_id: "123" } }, { do: "type", target: { path: "A" } },
      { do: "wait", args: { condition: { ms: 5 }, timeout_ms: 90_000 } }, { do: "command", command: "Debug.Log" },
      { do: "input", tool: "unity_open_mcp_execute_csharp", args: {} }, { do: "type", target: { path: "B" }, text: "${missing}" },
    ],
  });
  for (const part of ["format must be", "start.restart command needs", "same_screen", "ignore_errors", "do must be one of", "object_id is not allowed",
    "type needs text", "timeout_ms must be", "project.* command", "tool must be one of", "${missing} is used but not declared"])
    assert.ok(problems.some(p => p.includes(part)), `${part} in ${problems.join(" | ")}`);
});

test("scenario: files by name or project path; writes stay inside the project", t => {
  const root = mkdtempSync(join(tmpdir(), "uom-scenario-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  assert.equal(resolveScenarioPath(root, "smoke"), join(root, "Playtests", "Scenarios", "smoke.json"));
  assert.equal(resolveScenarioPath(root, "QA/flows/login.json"), join(root, "QA", "flows", "login.json"));
  assert.throws(() => resolveScenarioPath(root, "../outside.json", true), /inside the project/);
  assert.throws(() => resolveScenarioPath(root, "bad name!"), /not a scenario name/);
  assert.throws(() => resolveScenarioPath(root, "dir/file.txt"), /not a \.json file/);
  const file = resolveScenarioPath(root, "smoke", true);
  writeScenarioFile(file, scenario([{ do: "check" }]));
  const read = readScenarioFile(file);
  assert.equal(read.scenario.steps[0].do, "check");
  assert.ok(readFileSync(file, "utf8").endsWith("}\n"));
  writeFileSync(join(root, "broken.json"), "{");
  assert.throws(() => readScenarioFile(join(root, "broken.json")), /not valid JSON/);
});

test("matchTarget: exact, label / path drift, indexed siblings, blocked", () => {
  const nodes = [
    { id: 1, path: "C/Menu/ShopButton", role: "button", text: "Store", visible: true, interactable: true },
    { id: 2, path: "C/Menu/Play", role: "button", text: "Play", visible: true, interactable: true },
    { id: 3, path: "C/List/Item (1)", role: "button", text: "Get", visible: true, interactable: true },
    { id: 4, path: "C/List/Item (2)", role: "button", text: "Get", visible: true, interactable: true },
    { id: 5, path: "C/Menu/Settings", role: "button", text: "Settings", visible: true, interactable: true, occluded_by: "C/Popup/Dimmer" },
    { id: 6, path: "C/Form/Name", role: "input", text: "Enter name", visible: true, interactable: true },
  ];
  assert.equal(matchTarget(nodes, { path: "C/Menu/Play", text: "Play" }, "tap").node?.id, 2);
  const renamed = matchTarget(nodes, { path: "C/Menu/ShopButton", text: "Shop" }, "tap");
  assert.equal(renamed.node?.id, 1);
  assert.equal(renamed.drift[0].kind, "label_changed");
  const moved = matchTarget(nodes, { path: "C/Footer/Play", text: "Play" }, "tap");
  assert.equal(moved.node?.id, 2);
  assert.equal(moved.drift[0].kind, "path_changed");
  assert.equal(matchTarget(nodes, { path: "C/List/Item (2)", text: "Get" }, "tap").node?.id, 4, "the exact path wins among siblings that mask alike");
  assert.equal(matchTarget(nodes, { text: "Get", index: 1 }, "tap").node?.id, 4);
  const blocked = matchTarget(nodes, { path: "C/Menu/Settings", text: "Settings" }, "tap");
  assert.match(blocked.blocked ?? "", /covered by C\/Popup\/Dimmer/);
  const none = matchTarget(nodes, { path: "C/Menu/Shop2", text: "Shop" }, "tap");
  assert.equal(none.node, null);
  assert.ok(none.candidates.some(c => c.includes("'Play'")));
  assert.equal(matchTarget(nodes, { path: "C/Form/Name", role: "input" }, "type").node?.id, 6, "fields are known by path, not by their text");
});

// ---------------------------------------------------------------------------
// A fake game on a fake clock
// ---------------------------------------------------------------------------

interface Button { label: string; to?: string; role?: string; path?: string; logs?: unknown[]; requires?: string; hidden?: boolean }
interface Screen { title: string; buttons: Button[]; diagnostics?: Record<string, unknown>; texts?: string[] }

const SCREENS: Record<string, Screen> = {
  Menu: {
    title: "Main Menu", buttons: [{ label: "Play", to: "Game" }, { label: "Shop", to: "Shop" }, { label: "Profile", to: "Profile" }],
    texts: ["Coins: 120", "Gems: 4", "Level 3", "Welcome back", "Daily quests", "Events"],
  },
  Game: { title: "Level 1", buttons: [{ label: "Back", to: "Menu" }], texts: ["Score: 0"] },
  Shop: { title: "Shop", buttons: [{ label: "Close", to: "Menu" }] },
  Profile: { title: "Profile", buttons: [{ label: "Nickname", role: "input" }, { label: "Save", to: "Menu", requires: "Nickname" }, { label: "Back", to: "Menu" }] },
};

class Game {
  screen = "Menu";
  mode = "play";
  calls: Array<{ tool: string; args: Record<string, unknown> }> = [];
  values: Record<string, string> = {};
  boots = 0;
  shots = 0;
  /** Hook run on every project-command boot (a test changes the game here). */
  onBoot?: (game: Game) => void;
  constructor(public screens: Record<string, Screen> = JSON.parse(JSON.stringify(SCREENS)), readonly frames?: string) {}

  pathOf(name: string, b: Button) { return b.path ?? `Canvas/${name}/${b.label.replace(/\W/g, "")}Button`; }

  nodes(): Array<Record<string, unknown>> {
    const s = this.screens[this.screen];
    return [
      { id: `${this.screen}#title`, path: `Canvas/${this.screen}/Title`, role: "text", text: s.title, visible: true },
      ...(s.texts ?? []).map((t, i) => ({ id: `${this.screen}#t${i}`, path: `Canvas/${this.screen}/Text${i}`, role: "text", text: t, visible: true })),
      ...s.buttons.filter(b => !b.hidden).map(b => ({
        id: `${this.screen}#${s.buttons.indexOf(b)}`, path: this.pathOf(this.screen, b), role: b.role ?? "button",
        text: b.role === "input" ? this.values[b.label] || b.label : b.label, visible: true, interactable: true,
      })),
    ];
  }

  async call(tool: string, args: Record<string, unknown>): Promise<CallToolResult> {
    this.calls.push({ tool, args });
    switch (tool) {
      case "unity_open_mcp_ui_snapshot":
        return ok({ status: "ok", mode: this.mode, screen: { dpi: 320 }, nodes: this.mode === "play" ? this.nodes() : [], diagnostics: this.screens[this.screen].diagnostics ?? {} });
      case "unity_open_mcp_inputsim_pointer": {
        const [name, index] = String(args.object_id).split("#");
        if (name !== this.screen) return err("target_not_found");
        const b = this.screens[name].buttons[Number(index)];
        if (b.to && (!b.requires || this.values[b.requires])) this.screen = b.to;
        return ok({ status: "ok", action: "click", target: this.pathOf(name, b) }, b.logs ?? []);
      }
      case "unity_open_mcp_inputsim_text": {
        const [name, index] = String(args.object_id).split("#");
        const field = this.screens[name].buttons[Number(index)];
        this.values[field.label] = String(args.text ?? "");
        return ok({ status: "ok", textAfter: args.text, field: { path: this.pathOf(name, field) } });
      }
      case "unity_open_mcp_playtest_wait":
        return (args.condition as Record<string, unknown>)?.ms === 1 ? ok({ status: "ok", fired: { type: "ms" } }) : err("timed_out", "the condition did not hold");
      case "unity_open_mcp_project_commands":
        this.boots++;
        this.screen = "Menu";
        this.values = {};
        this.onBoot?.(this);
        return ok({ result: { booted: true } });
      case "unity_senses_screenshot": {
        const file = this.frames ? join(this.frames, `shot-${++this.shots}.png`) : `/tmp/none-${++this.shots}.png`;
        if (this.frames) writeFileSync(file, encodePng({ width: 8, height: 8, data: new Uint8Array(8 * 8 * 4).fill(90) }));
        return ok({ status: "ok", filePath: file });
      }
      default:
        return ok({ status: "ok" });
    }
  }
}

function recorder(): ExploreRecorder & { labels: string[]; outputs: Array<Record<string, any>>; args: Array<Record<string, unknown>> } {
  const labels: string[] = [], outputs: Array<Record<string, any>> = [], args: Array<Record<string, unknown>> = [];
  let seq = 0;
  return { labels, outputs, args, begin(label, a) { labels.push(label); args.push(a); const mine = ++seq; return { seq: mine, finish: o => { outputs.push(o); } }; } };
}

function options(overrides: Partial<ReplayOptions> = {}): ReplayOptions {
  return {
    from: "start", retries: 1, record: false, strict: false, capture: "failures", vars: {}, ignoreErrors: [],
    placeholderPattern: null, minTouchDp: null, settleMs: 250, maxSettleMs: 1000, stepTimeoutMs: 2000, sameScreen: 0.8, maxMinutes: 20,
    visual: null, ...overrides,
  };
}

async function replay(game: Game, sc: Scenario, overrides: Partial<ReplayOptions> = {}, consoleFeed?: ConsoleErrorFeed) {
  let t = 1_000_000;
  const rec = recorder();
  const out = await new Replayer(sc, options(overrides), {
    call: (tool, args) => game.call(tool, args), recorder: rec, signal: new AbortController().signal,
    console: consoleFeed, now: () => t, sleep: async ms => { t += ms; },
  }).run();
  return { ...out, rec, result: out.result as Record<string, any> };
}

const FLOW = [
  { do: "tap", target: { path: "Canvas/Menu/ShopButton", text: "Shop" } },
  { do: "tap", target: { path: "Canvas/Shop/CloseButton", text: "Close" } },
  { do: "tap", target: { path: "Canvas/Menu/ProfileButton", text: "Profile" } },
  { do: "type", target: { path: "Canvas/Profile/NicknameButton", role: "input" }, text: "${nick}" },
  { do: "tap", target: { path: "Canvas/Profile/SaveButton", text: "Save" } },
];

test("replay: unbaselined run passes on execution, record writes the baseline, the next run compares", async () => {
  const sc = scenario(FLOW, { vars: { nick: { secret: true } } });
  const game = new Game();
  const first = await replay(game, sc, { vars: { nick: "Hero" } });
  assert.equal(first.result.verdict, "passed", JSON.stringify(first.result.failure));
  assert.equal(first.result.unbaselined, 5);
  assert.equal(game.boots, 1, "from start: the boot command ran once");
  assert.equal(game.screen, "Menu", "Save needed the nickname and went back to the menu");
  // The typed secret is never in the recorded arguments.
  assert.ok(first.rec.args.some(a => a.text === "[redacted 4 chars]"), JSON.stringify(first.rec.args));
  assert.ok(!JSON.stringify(first.rec.args).includes("Hero"));

  const recorded = await replay(game, sc, { record: true, vars: { nick: "Hero" } });
  assert.equal(recorded.result.verdict, "recorded");
  const baseline = recorded.recorded!;
  assert.ok(baseline.start.screen?.signature.includes("c|button|play|Canvas/Menu/PlayButton"));
  assert.equal(baseline.steps[0].expect?.screen?.title, "Shop");
  assert.deepEqual(baseline.steps[0].expect?.findings, []);
  assert.equal(baseline.steps[3].target?.role, "input");
  assert.equal(baseline.steps[4].expect?.screen?.title, "Main Menu");

  const again = await replay(game, baseline, { vars: { nick: "Hero" } });
  assert.equal(again.result.verdict, "passed");
  assert.equal(again.result.unbaselined, 0);
  assert.equal(again.result.steps[1].similarity, 1);
});

test("replay: a renamed label is drift (strict fails it); a different screen fails with a diff and a frame", async () => {
  const game = new Game();
  const baseline = (await replay(game, scenario(FLOW, { vars: { nick: {} } }), { record: true, vars: { nick: "A" } })).recorded!;

  game.screens.Menu.buttons[1].label = "Store";
  game.screens.Menu.buttons[1].path = "Canvas/Menu/ShopButton";
  const drift = await replay(game, baseline, { vars: { nick: "A" } });
  // The menu's signature changed by one control: still the menu (similar), the tap passes with drift.
  assert.equal(drift.result.verdict, "passed", JSON.stringify(drift.result.failure));
  assert.ok(drift.result.drift.some((d: any) => d.kind === "label_changed" && d.detail.includes("'Shop' is now 'Store'")), JSON.stringify(drift.result.drift));
  assert.ok(drift.result.drift.some((d: any) => d.kind === "screen_drift"));

  // On a small screen one renamed control is under 80% with labels, the same screen without them.
  const small = new Game();
  const shopBaseline = (await replay(small, scenario(FLOW.slice(0, 2)), { record: true })).recorded!;
  small.screens.Shop.buttons[0].label = "Done";
  small.screens.Shop.buttons[0].path = "Canvas/Shop/CloseButton";
  const relabelled = await replay(small, shopBaseline, { retries: 0 });
  assert.equal(relabelled.result.verdict, "passed", JSON.stringify(relabelled.result.failure));
  assert.ok(relabelled.result.drift.some((d: any) => d.kind === "labels_changed"), JSON.stringify(relabelled.result.drift));
  small.screens.Shop.title = "Store";
  const retitled = await replay(small, shopBaseline, { retries: 0 });
  assert.equal(retitled.result.failure?.reason, "screen_changed", "a changed text is a changed screen");

  const strict = await replay(game, baseline, { vars: { nick: "A" }, strict: true, retries: 0 });
  assert.equal(strict.result.verdict, "failed");
  assert.equal(strict.result.failure.reason, "drift");

  // Shop now leads to the game screen: a regression.
  game.screens.Menu.buttons[1].to = "Game";
  const broken = await replay(game, baseline, { vars: { nick: "A" } });
  assert.equal(broken.result.verdict, "failed");
  assert.equal(broken.result.consistent, true, "both attempts failed at the same step for the same reason");
  assert.equal(broken.result.failure.n, 1);
  assert.equal(broken.result.failure.reason, "screen_changed");
  assert.equal(broken.result.failure.expected_title, "Shop");
  assert.equal(broken.result.failure.title, "Level 1");
  assert.ok(broken.result.failure.diff.added.some((t: string) => t.includes("'back'")));
  assert.ok(broken.result.failure.frame, "the failing step has a frame");
  assert.deepEqual(broken.result.steps.slice(2).map((s: any) => s.status), ["skipped", "skipped", "skipped", "skipped"]);
  assert.equal(broken.result.attempts.length, 2);
});

test("replay: a missing target names what is on screen; a timed-out wait and a missing text fail", async () => {
  const game = new Game();
  game.screens.Menu.buttons[1].hidden = true;
  const missing = await replay(game, scenario(FLOW.slice(0, 1)), { retries: 0 });
  assert.equal(missing.result.failure.reason, "target_not_found");
  assert.ok(missing.result.failure.candidates.some((c: string) => c.includes("'Play'")));

  const wait = await replay(new Game(), scenario([{ do: "wait", args: { condition: { ms: 2 }, timeout_ms: 100 } }]), { retries: 0 });
  assert.equal(wait.result.failure.reason, "wait_timed_out");
  const waited = await replay(new Game(), scenario([{ do: "wait", args: { condition: { ms: 1 } } }]), { retries: 0 });
  assert.equal(waited.result.verdict, "passed");

  const texts = await replay(new Game(), scenario([{ do: "tap", target: { path: "Canvas/Menu/PlayButton", text: "Play" }, expect: { texts: ["Score", "Lives"] } }]), { retries: 0 });
  assert.equal(texts.result.failure.reason, "text_missing");
  assert.match(texts.result.failure.message, /'Lives'/);
});

test("replay: a failure that does not repeat is flaky; console errors from the stream fail a step unless ignored", async () => {
  const game = new Game();
  const baseline = (await replay(game, scenario(FLOW.slice(0, 2)), { record: true })).recorded!;
  // The first boot shows the menu without its Shop button (a slow load); later boots are fine.
  game.onBoot = g => { g.screens.Menu.buttons[1].hidden = g.boots === 2; };
  const flaky = await replay(game, baseline);
  assert.equal(flaky.result.verdict, "flaky", JSON.stringify(flaky.result.attempts));
  assert.equal(flaky.result.earlier_failures[0].reason, "target_not_found");
  game.onBoot = undefined;
  game.screens.Menu.buttons[1].hidden = false;

  // An exception logged between calls reaches the replay only through the event stream.
  const feed = new ConsoleErrorFeed();
  const event = (message: string): BridgeEvent => ({ seq: 1, ts: new Date().toISOString(), type: "log", logType: "Exception", message } as BridgeEvent);
  const original = game.call.bind(game);
  game.call = async (tool, args) => {
    const result = await original(tool, args);
    if (tool === "unity_open_mcp_inputsim_pointer" && game.screen === "Shop") feed.onEvent(event("NullReferenceException: shop\n at Shop.Open()"));
    return result;
  };
  const errored = await replay(game, baseline, { retries: 0 }, feed);
  assert.equal(errored.result.failure.reason, "console_error");
  assert.equal(errored.result.failure.n, 1);
  assert.deepEqual(errored.result.failure.errors, ["NullReferenceException: shop"]);
  const ignored = await replay(game, baseline, { retries: 0, ignoreErrors: [/NullReference/] }, feed);
  assert.equal(ignored.result.verdict, "passed");
});

test("replay: new detector findings against the baseline — bug fails, suspicious is drift, gone is fixed", async () => {
  const game = new Game();
  game.screens.Shop.diagnostics = { placeholder: { count: 1, items: [{ path: "Canvas/Shop/Label", text: "shop_title_key" }], truncated: 0 } };
  const baseline = (await replay(game, scenario(FLOW.slice(0, 1)), { record: true })).recorded!;
  assert.deepEqual(baseline.steps[0].expect?.findings, ["placeholder_text|Canvas/Shop/Label|"]);
  game.screens.Shop.diagnostics = {
    overflow: { count: 1, items: [{ path: "Canvas/Shop/Price", text: "Mega", size: [10, 10], content: [30, 10] }], truncated: 0 },
    small_touch_targets: { count: 1, min_touch_size: 88, items: [{ path: "Canvas/Shop/X", role: "button", size: [20, 20] }], truncated: 0 },
  };
  const run = await replay(game, baseline, { retries: 0 });
  assert.equal(run.result.failure.reason, "new_finding");
  assert.deepEqual(run.result.findings.new.map((f: any) => f.kind).sort(), ["small_touch_target", "text_overflow"]);
  assert.deepEqual(run.result.findings.fixed, ["placeholder_text|Canvas/Shop/Label|"]);
  assert.ok(run.result.drift.some((d: any) => d.kind === "new_suspicious"));
});

test("replay: edit mode refuses unless the run restarts play mode; from current skips the first restart", async () => {
  const game = new Game();
  game.mode = "edit";
  const sc = scenario(FLOW.slice(0, 1));
  await assert.rejects(new Replayer(sc, options(), { call: (tl, a) => game.call(tl, a), recorder: recorder(), signal: new AbortController().signal, sleep: async () => {} }).run(), /enter play mode first/);
  game.mode = "play";
  game.screen = "Shop";
  const current = await replay(game, scenario([{ do: "tap", target: { path: "Canvas/Shop/CloseButton", text: "Close" } }]), { from: "current" });
  assert.equal(current.result.verdict, "passed");
  assert.equal(game.boots, 0);
  // From the wrong screen: one attempt, no retry from the start (that pass would read as flaky).
  const baseline = (await replay(game, scenario(FLOW.slice(0, 1)), { record: true })).recorded!;
  game.screen = "Shop";
  const boots = game.boots;
  const wrong = await replay(game, baseline, { from: "current", retries: 2 });
  assert.equal(wrong.result.verdict, "failed");
  assert.equal(wrong.result.attempts.length, 1);
  assert.equal(game.boots, boots, "no restart");
  assert.match(wrong.result.failure.message, /from: current/);
  assert.ok(wrong.result.notes.some((n: string) => n.includes("No retries")));
});

// ---------------------------------------------------------------------------
// Export
// ---------------------------------------------------------------------------

test("export calls: taps by resolved path + label, typing, waits, commands, checks; secrets become vars", () => {
  let seq = 0;
  const entries: Array<Record<string, unknown>> = [];
  const call = (tool: string, kind: string, args: Record<string, unknown>, summary: Record<string, unknown> = {}, status = "ok") => {
    const s = ++seq;
    entries.push({ k: "call_started", seq: s, tool, kind, args });
    entries.push({ k: "call", seq: s, tool, kind, status, summary });
  };
  call("unity_open_mcp_editor_set_state", "control", { state: "play" });
  call("unity_open_mcp_project_commands project.demo.playtest.begin", "command", { action: "invoke", command_id: "project.demo.playtest.begin" });
  call("unity_open_mcp_project_commands project.demo.playtest.boot", "command", { action: "invoke", command_id: "project.demo.playtest.boot", args: { level: 1 } });
  call("unity_open_mcp_inputsim_pointer", "input", { action: "click", target_text: "Shop" }, { target: "Canvas/Menu/ShopButton" });
  call("unity_open_mcp_inputsim_pointer", "input", { action: "click", object_id: "-123" }, {}, "error");
  call("unity_senses_screenshot", "capture", { view: "composed" });
  entries.push({ k: "mark", step: 1, label: "Shop opens", verdict: "ok" });
  call("unity_open_mcp_inputsim_pointer", "input", { action: "click", object_id: "-555" }, { target: "Canvas/Shop/CloseButton" });
  call("unity_open_mcp_inputsim_text", "input", { target_text: "Password", text: "[redacted 8 chars]" }, { field: { path: "Canvas/Login/Password" } });
  call("unity_open_mcp_playtest_wait", "wait", { condition: { ui: { text_contains: "Welcome" } }, timeout_ms: 90_000 });
  call("unity_open_mcp_inputsim_key", "input", { key: "escape" });
  call("unity_open_mcp_playtest_session check", "check", { label: "Login layout" });
  entries.push({ k: "explore", screens: [] });
  const { scenario: sc, warnings, stats } = exportScenario(entries as any, { device: { view: { mode: "simulator", device: "iPhone 13" } } }, { source: "calls", name: "login", session: "s1" });
  assert.deepEqual(sc.start, { restart: "command", command: "project.demo.playtest.boot", args: { level: 1 } });
  assert.deepEqual(sc.steps.map(s => s.do), ["tap", "tap", "type", "wait", "input", "check"]);
  assert.deepEqual(sc.steps[0].target, { path: "Canvas/Menu/ShopButton", text: "Shop" });
  assert.equal(sc.steps[0].checkpoint, "Shop opens");
  assert.deepEqual(sc.steps[1].target, { path: "Canvas/Shop/CloseButton" }, "an object_id click is exported by the path the bridge reported");
  assert.equal(sc.steps[2].text, "${secret_1}");
  assert.equal(sc.vars.secret_1.secret, true);
  assert.equal(sc.steps[3].args?.timeout_ms, 40_000);
  assert.equal(stats.skipped_failed, 1);
  assert.equal(stats.skipped_captures, 1);
  assert.equal((sc.source as any).device.device, "iPhone 13");
  for (const w of ["secret_1", "cut to 40000", "record:true"]) assert.ok(warnings.some(x => x.includes(w)), w);
  assert.deepEqual(parseScenario(JSON.parse(JSON.stringify(sc))).problems, [], "an export is a valid scenario");
});

// ---------------------------------------------------------------------------
// Through the router: session, export of a crawl, record, run, regression, report
// ---------------------------------------------------------------------------

function routerHarness(t: { after(fn: () => void): void }) {
  const root = mkdtempSync(join(tmpdir(), "uom-replay-"));
  const project = join(root, "project");
  mkdirSync(join(project, "ProjectSettings"), { recursive: true });
  writeFileSync(join(project, "ProjectSettings", "ProjectSettings.asset"), "PlayerSettings:\n  companyName: TestCo\n  productName: Replay\n");
  const frames = join(root, "captures");
  mkdirSync(frames);
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const game = new Game(undefined, frames);
  const live = {
    isLiveAvailable: async () => true,
    projectCommands: async (a: Record<string, unknown>) => ({
      command: { id: a.id, available: true, invocationSupported: true, schemaVersion: "1", lifecycle: "none", inputSchema: { type: "object", properties: {}, additionalProperties: false } },
    }),
    route: async (name: string, args: Record<string, unknown>) => {
      if (name === "unity_open_mcp_player_state") {
        if (args.action === "list") return ok({ status: "ok", action: "list", company: "TestCo", product: "Replay" });
        if (args.action === "snapshot") return ok({ status: "ok", action: "snapshot", name: args.name });
        if (args.action === "diff") return ok({ status: "ok", action: "diff", identical: true });
      }
      if (name === "unity_open_mcp_game_view") return ok({ status: "ok", action: "get", mode: "game", playing: true });
      return game.call(name, args);
    },
  } as unknown as LiveClient;
  const stream = { addListener: () => () => {}, ensureSubscription: () => false, reconnectNow: () => {} };
  const router = new ToolRouter(live, { isBatchTool: () => false } as unknown as BatchSpawn, project, stream as unknown as BridgeEventStream, new ToolSessionState());
  router.usePlaytestRecorder(new PlaytestRecorder({ root: join(root, "sessions"), projectPath: project, pid: 4242, isAlive: () => false }));
  t.after(() => router.closeJobs());
  return { game, router, project };
}

const ME = { agent: "agent-me" };

async function waitJob(router: ToolRouter, jobId: string): Promise<Record<string, any>> {
  for (let i = 0; i < 200; i++) {
    const job = body(await router.route("unity_open_mcp_jobs", { action: "wait", job_id: jobId, timeout_ms: 5000 }, ME));
    if (["succeeded", "failed", "cancelled", "orphaned"].includes(job.state)) return job;
  }
  throw new Error("job did not finish");
}

test("playtest_replay: schema, playtest group, server-local, intent and chain", () => {
  const tool = ALL_TOOLS.find(t => t.name === PLAYTEST_REPLAY_TOOL)!;
  assert.ok(tool, "registered");
  assert.equal(groupFor(PLAYTEST_REPLAY_TOOL), "playtest");
  assert.ok(isLocalTool(PLAYTEST_REPLAY_TOOL));
  assert.ok(recommendGroups({ intent: "replay the login scenario as a ui regression check" }).groups.map(g => g.id).includes("playtest"));
  assert.ok(RECOMMENDED_TOOL_CHAINS.some(c => c.name === "playtest-replay-regression"));
  const props = tool.inputSchema.properties as Record<string, unknown>;
  for (const key of ["action", "scenario", "source", "to_screen", "record", "retries", "vars", "strict"]) assert.ok(key in props, key);
});

test("router: crawl → export → run passes; a regression fails at its step; the report has a Replays section", async t => {
  const { game, router, project } = routerHarness(t);
  let answer = body(await router.route(PLAYTEST_REPLAY_TOOL, { scenario: "nope" }, ME));
  assert.equal(answer.error.code, "no_open_session");
  answer = body(await router.route(PLAYTEST_REPLAY_TOOL, { action: "list" }, ME));
  assert.equal(answer.total, 0);
  answer = body(await router.route(PLAYTEST_REPLAY_TOOL, { action: "list", scenario: "x" }, ME));
  assert.equal(answer.error.code, "invalid_arguments");

  assert.equal(body(await router.route("unity_open_mcp_playtest_session", { action: "start", name: "Replay", snapshot: true }, ME)).status, "ok");
  const crawl = body(await router.route("unity_open_mcp_playtest_explore", { settle_ms: 50, max_settle_ms: 200, restart: "command", restart_command: "project.demo.playtest.boot", fill_text: "Hero" }, ME));
  const crawled = await waitJob(router, crawl.job_id);
  assert.equal(crawled.state, "succeeded", JSON.stringify(crawled.error));
  assert.ok(!("signatures" in crawled.result), "screen signatures stay in the session log");

  answer = body(await router.route(PLAYTEST_REPLAY_TOOL, { action: "export", source: "crawl", scenario: "tour" }, ME));
  assert.equal(answer.status, "ok", JSON.stringify(answer));
  assert.equal(answer.path, "Playtests/Scenarios/tour.json");
  assert.equal(answer.unbaselined, 0, "a crawl export carries its baseline");
  assert.ok(existsSync(join(project, "Playtests", "Scenarios", "tour.json")));
  assert.equal(body(await router.route(PLAYTEST_REPLAY_TOOL, { action: "export", source: "crawl", scenario: "tour" }, ME)).error.code, "scenario_exists");
  const path = body(await router.route(PLAYTEST_REPLAY_TOOL, { action: "export", source: "crawl", scenario: "to-shop", to_screen: "S3" }, ME));
  assert.equal(path.status, "ok", JSON.stringify(path));
  assert.equal(body(await router.route(PLAYTEST_REPLAY_TOOL, { action: "export", source: "crawl", scenario: "x", to_screen: "S99" }, ME)).error.code, "invalid_arguments");
  const shown = body(await router.route(PLAYTEST_REPLAY_TOOL, { action: "show", scenario: "to-shop" }, ME));
  assert.ok(shown.steps.length >= 1 && shown.steps.every((s: any) => s.baseline), JSON.stringify(shown.steps));
  const listed = body(await router.route(PLAYTEST_REPLAY_TOOL, { action: "list" }, ME));
  assert.deepEqual(listed.scenarios.map((s: any) => s.name), ["to-shop", "tour"]);

  assert.equal(body(await router.route("unity_open_mcp_jobs", { action: "start", tool_or_command: PLAYTEST_REPLAY_TOOL, args: {} }, ME)).error.code, "job_operation_unsupported");
  let run = body(await router.route(PLAYTEST_REPLAY_TOOL, { scenario: "tour", retries: 0 }, ME));
  assert.equal(run.status, "running", JSON.stringify(run));
  let job = await waitJob(router, run.job_id);
  assert.equal(job.state, "succeeded", JSON.stringify(job.error));
  assert.equal(job.result.verdict, "passed", JSON.stringify(job.result.failure ?? job.result.steps));

  // A regression: the Profile screen loses its Save button.
  game.screens.Profile.buttons = game.screens.Profile.buttons.filter(b => b.label !== "Save");
  run = body(await router.route(PLAYTEST_REPLAY_TOOL, { scenario: "tour", retries: 1, step_timeout_ms: 500 }, ME));
  job = await waitJob(router, run.job_id);
  assert.equal(job.result.verdict, "failed");
  assert.equal(job.result.consistent, true);
  assert.ok(["target_not_found", "screen_changed"].includes(job.result.failure.reason), job.result.failure.reason);
  assert.ok(job.result.agentNextSteps.some((s: string) => s.includes("record:true")));

  const ended = body(await router.route("unity_open_mcp_playtest_session", { action: "end" }, ME));
  assert.deepEqual(ended.summary.replays.map((r: any) => r.verdict), ["passed", "failed"]);
  assert.ok(ended.agentNextSteps.some((s: string) => s.includes("replay results")));
  const report = readFileSync(ended.report, "utf8");
  for (const phrase of ["## Replays", "### Replay 1: `tour` — passed", "### Replay 2: `tour` — failed (the same step failed every attempt)", "Attempts: 1 failed", "Replay tour: failed — Step"])
    assert.ok(report.includes(phrase), phrase);
  assert.match(report, /\[!\[step \d+\]\(thumbs\/[^)]+\.thumb\.png\)\]\(frames\/[^)]+\.png\)/, "the failing step links its frame");
  const log = readFileSync(join(ended.folder, "log.jsonl"), "utf8").trim().split("\n").map(l => JSON.parse(l));
  assert.ok(log.some(e => e.k === "call" && e.kind === "replay"));
});

test("router: record run writes the baseline into the file; missing vars and invalid scenarios refuse before the job", async t => {
  const { router, project } = routerHarness(t);
  const file = join(project, "Playtests", "Scenarios", "profile.json");
  writeScenarioFile(file, scenario(FLOW, { vars: { nick: { secret: true, description: "a nickname" } } }));
  assert.throws(() => prepareReplay(project, { scenario: "profile" }), /needs vars: nick/);
  writeFileSync(join(project, "Playtests", "Scenarios", "bad.json"), JSON.stringify({ format: SCENARIO_FORMAT, steps: [{ do: "tap", target: {} }] }));
  assert.equal(body(await router.route("unity_open_mcp_playtest_session", { action: "start", name: "Rec", snapshot: true }, ME)).status, "ok");
  assert.equal(body(await router.route(PLAYTEST_REPLAY_TOOL, { scenario: "bad" }, ME)).error.code, "scenario_invalid");
  assert.equal(body(await router.route(PLAYTEST_REPLAY_TOOL, { scenario: "profile" }, ME)).error.code, "missing_vars");
  assert.equal(body(await router.route(PLAYTEST_REPLAY_TOOL, { scenario: "profile", record: true, strict: true, vars: { nick: "A" } }, ME)).error.code, "invalid_arguments");
  const run = body(await router.route(PLAYTEST_REPLAY_TOOL, { scenario: "profile", record: true, vars: { nick: "Hero" } }, ME));
  const job = await waitJob(router, run.job_id);
  assert.equal(job.result.verdict, "recorded", JSON.stringify(job.result.failure));
  assert.equal(job.result.baseline.file, "Playtests/Scenarios/profile.json");
  const written = readScenarioFile(file).scenario;
  assert.ok(written.steps.every(s => s.expect?.screen), "every step has its screen now");
  const ended = body(await router.route("unity_open_mcp_playtest_session", { action: "end" }, ME));
  const log = readFileSync(join(ended.folder, "log.jsonl"), "utf8");
  assert.ok(!log.includes("Hero"), "a secret variable never reaches the session log");
  assert.ok(readFileSync(ended.report, "utf8").includes("Baseline written to `Playtests/Scenarios/profile.json`"));
});
