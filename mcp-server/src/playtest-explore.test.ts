// Playtest UI crawler (unity_open_mcp_playtest_explore) and detectors
// (playtest_session check): pure detector functions, the crawl engine against a
// fake game (screen graph, deny-list, dead button / dead end / console error,
// navigation, restart, budgets, cancel, play-mode exit) and the whole path
// through the real ToolRouter — session and isolation checks, the job, the
// recorded steps and the report's Exploration / Screen checks sections.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
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
import { PLAYTEST_EXPLORE_TOOL } from "./tools/playtest-explore.js";
import { PlaytestRecorder } from "./playtest/recorder.js";
import { encodePng } from "./playtest/png.js";
import {
  DEFAULT_PLACEHOLDER_PATTERN, FindingSet, compilePolicy, denyReason, findingsFromSnapshot, isBackLike, maskText, minTouchPx,
  screenSignature, screenTitle, similarity, type SnapshotNode,
} from "./playtest/detectors.js";
import { Explorer, restartGame, type ExploreOptions, type ExploreRecorder } from "./playtest/explore.js";
import { parseExploreOptions } from "./playtest/explore-job.js";
import { memoryOptions, type PlaytestProfile } from "./playtest/profile.js";

const body = (result: CallToolResult) => JSON.parse((result.content.find(c => c.type === "text") as { text: string }).text);
const text = (value: unknown): CallToolResult => ({ content: [{ type: "text", text: JSON.stringify(value) }] });
const ok = (output: unknown, logs: unknown[] = []): CallToolResult =>
  text({ mutation: { success: true, output, error: null }, effectiveReadOnly: true, logs });

// ---------------------------------------------------------------------------
// Detectors
// ---------------------------------------------------------------------------

test("detectors: masked signatures, similarity, placeholder default, touch size", () => {
  assert.equal(maskText("  Coins:  1,234 "), "coins: #");
  assert.equal(maskText("00:12"), "#");
  const a = screenSignature([
    { path: "C/Title", role: "text", text: "Coins: 5", visible: true },
    { path: "C/Play", role: "button", text: "Play", visible: true },
    { path: "C/Bg", role: "image", visible: true },
    { path: "C/Hidden", role: "button", text: "Hidden", visible: false },
  ]);
  assert.deepEqual([...a].sort(), ["c|button|play|C/Play", "t|coins: #"]);
  const b = screenSignature([
    { path: "C/Title", role: "text", text: "Coins: 99", visible: true },
    { path: "C/Play", role: "button", text: "Play", visible: true },
  ]);
  assert.equal(similarity(a, b), 1, "a ticking counter is the same screen");
  assert.equal(similarity(new Set(), new Set()), 1);
  // A control covered by a popup's dimmer is behind the screen: not part of its identity.
  const covered = screenSignature([
    { path: "C/Title", role: "text", text: "Coins: 5", visible: true },
    { path: "C/Play", role: "button", text: "Play", visible: true, occluded_by: "C/Popup/Dimmer" },
    { path: "C/Popup/Claim", role: "button", text: "Claim", visible: true },
  ]);
  assert.deepEqual([...covered].sort(), ["c|button|claim|C/Popup/Claim", "t|coins: #"]);
  assert.equal(screenTitle([
    { path: "C/Title", role: "text", text: "Main Menu", visible: true },
    { path: "C/Play", role: "button", text: "Play", visible: true, occluded_by: "C/Popup/Dimmer" },
    { path: "C/Popup/Title", role: "text", text: "Daily reward", visible: true },
  ]), "Daily reward", "a popup's own text names it");
  assert.equal(similarity(new Set(["x"]), new Set(["y"])), 0);

  const placeholder = new RegExp(DEFAULT_PLACEHOLDER_PATTERN);
  for (const yes of ["#MENU_PLAY#", "{player_name}", "[UI.Play]", "menu_play_button", "MENU_PLAY_BUTTON", "ui.button.play"])
    assert.ok(placeholder.test(yes), yes);
  for (const no of ["Play", "v1.2.3", "Coins: 5", "GAME_OVER", "OK", "[X]", "1.2.3", "Hello world"])
    assert.ok(!placeholder.test(no), no);

  assert.equal(minTouchPx(44, 320), 88);
  assert.equal(minTouchPx(44, 0), null);
  assert.equal(minTouchPx(null, 320), null);
});

test("detectors: deny-list, allow, back-like controls", () => {
  const policy = compilePolicy(["^Danger"], ["Remove ads"]);
  const node = (text: string, path = "Canvas/Btn"): SnapshotNode => ({ path, role: "button", text, visible: true, interactable: true });
  assert.match(denyReason(node("Buy Coins"), policy)!, /deny-list/);
  assert.match(denyReason(node("$0.99"), policy)!, /price/);
  assert.match(denyReason(node("Pack 4,99 €"), policy)!, /price/);
  assert.match(denyReason(node("Quit"), policy)!, /deny-list/);
  assert.match(denyReason(node("Danger zone"), policy)!, /deny pattern/);
  assert.match(denyReason(node("Open", "Canvas/DangerPanel/Open"), compilePolicy(["dangerpanel"], [])) ?? "", /deny pattern/);
  assert.equal(denyReason(node("Remove ads"), policy), null, "allow wins over the default list");
  assert.equal(denyReason(node("Play"), policy), null);
  assert.equal(denyReason(node("Buy"), compilePolicy([], [], false)), null, "default_deny false");
  assert.throws(() => compilePolicy(["("], []), /not a valid regular expression/);

  assert.ok(isBackLike(node("Close")));
  assert.ok(isBackLike(node("×")));
  assert.ok(isBackLike(node("", "Canvas/Popup/CloseButton")));
  assert.ok(isBackLike(node("", "Canvas/Popup/Btn_Back")));
  assert.ok(!isBackLike(node("", "Canvas/Background")));
  assert.ok(!isBackLike(node("Play")));
});

test("detectors: findings from ui_snapshot diagnostics, deduplicated", () => {
  const output = {
    diagnostics: {
      overflow: { count: 1, items: [{ id: "1", path: "C/Shop/Price", text: "Mega pack", size: [100, 20], content: [180, 20] }], truncated: 0 },
      zero_size_interactables: { count: 0, items: [], truncated: 0 },
      outside_safe_area: { count: 1, items: [{ id: "2", path: "C/Top/Coins", role: "text", text: "Coins", edges: ["top"] }], truncated: 0 },
      overlapping_controls: { count: 1, items: [{ id: "3", path: "C/A", text: "A", with: "C/B", with_text: "B", overlap: 0.4 }], truncated: 0 },
      placeholder: { count: 1, items: [{ id: "4", path: "C/L", text: "menu_play_label" }], truncated: 0 },
      small_touch_targets: { count: 1, min_touch_size: 88, items: [{ id: "5", path: "C/I", role: "button", size: [40, 40] }], truncated: 2 },
    },
  };
  const findings = findingsFromSnapshot(output, { screen: "S1", step: 3 });
  assert.deepEqual(findings.map(f => [f.kind, f.severity]), [
    ["text_overflow", "bug"], ["outside_safe_area", "bug"], ["overlapping_controls", "suspicious"],
    ["placeholder_text", "suspicious"], ["small_touch_target", "suspicious"],
  ]);
  assert.match(findings[0].detail, /180×20 px but its box is 100×20/);
  assert.match(findings[1].detail, /top/);
  assert.match(findings[2].detail, /'B' C\/B by 40%/);
  assert.match(findings[4].detail, /40×40 px, under the 88 px/);
  assert.equal(findings[0].screen, "S1");
  assert.equal(findings[0].step, 3);

  const set = new FindingSet();
  assert.equal(set.addAll(findings).length, 5);
  assert.equal(set.addAll(findingsFromSnapshot(output, { screen: "S1", step: 9 })).length, 0, "same screen, same element");
  assert.equal(set.addAll(findingsFromSnapshot(output, { screen: "S2" })).length, 0, "the same element on another screen is one finding");
  assert.ok(set.add({ kind: "dead_end", severity: "suspicious", screen: "S3", detail: "x" }));
  assert.ok(set.add({ kind: "dead_end", severity: "suspicious", screen: "S4", detail: "x" }), "a screen-wide finding is per screen");
  assert.ok(set.add({ kind: "console_error", severity: "bug", screen: "S1", label: "A", detail: "x" }));
  assert.ok(set.add({ kind: "console_error", severity: "bug", screen: "S1", label: "B", detail: "x" }), "distinct messages");
  assert.ok(!set.add({ kind: "console_error", severity: "bug", screen: "S1", label: "A", detail: "y" }));
  assert.deepEqual(set.counts(), { bug: 4, suspicious: 5 });
  // Covered controls (behind a popup's dimmer) are not this screen's findings.
  const behind = { ...output, nodes: [{ path: "C/I", role: "button", visible: true, occluded_by: "C/Popup/Dimmer" }] };
  assert.ok(!findingsFromSnapshot(behind).some(f => f.path === "C/I"));
});

// ---------------------------------------------------------------------------
// A fake game: screens of buttons; a tap moves to another screen
// ---------------------------------------------------------------------------

interface FakeButton { label: string; role?: string; to?: string; logs?: unknown[]; exit?: boolean; path?: string; requires?: string }
interface FakeScreen { title: string; buttons: FakeButton[]; diagnostics?: Record<string, unknown>; over?: string }

const GAME: Record<string, FakeScreen> = {
  Menu: { title: "Main Menu", buttons: [{ label: "Play", to: "Game" }, { label: "Shop", to: "Shop" }, { label: "Settings", to: "Settings" }, { label: "Buy Coins", to: "Shop" }, { label: "Daily", to: "Daily" }] },
  // A popup over the menu: the menu's controls stay visible but covered by its dimmer.
  Daily: { title: "Daily reward", over: "Menu", buttons: [{ label: "Claim", to: "Menu" }] },
  Game: { title: "Level 1", buttons: [{ label: "Back", to: "Menu" }, { label: "Pause", to: "Pause" }] },
  Pause: { title: "Paused", buttons: [{ label: "Resume", to: "Game" }, { label: "Quit", to: "Menu" }] },
  Shop: {
    title: "Shop",
    buttons: [{ label: "$0.99 Pack" }, { label: "Close", to: "Menu" }],
    diagnostics: { overflow: { count: 1, items: [{ id: "9", path: "Canvas/Shop/Price", text: "Mega pack", size: [100, 20], content: [180, 20] }], truncated: 0 } },
  },
  Settings: {
    title: "Settings",
    buttons: [
      { label: "Sound", role: "toggle" },
      { label: "Report", logs: [{ severity: "error", message: "NullReferenceException: boom\n  at Settings.Report ()" }] },
      { label: "Help" },
      { label: "Credits", to: "Credits" },
      { label: "Back", to: "Menu" },
    ],
  },
  Credits: { title: "Credits", buttons: [] },
};

class FakeGame {
  screen: string;
  mode = "play";
  clicks: string[] = [];
  calls: Array<{ tool: string; args: Record<string, unknown> }> = [];
  /** What was typed into each text field (by label). */
  values: Record<string, string> = {};
  private shots = 0;
  constructor(readonly screens: Record<string, FakeScreen>, readonly start = "Menu", readonly frames?: string) { this.screen = start; }

  nodes(name = this.screen, coveredBy?: string): Array<Record<string, unknown>> {
    const s = this.screens[name];
    const below = s.over ? this.nodes(s.over, `Canvas/${name}/Dimmer`) : [];
    return [
      ...below,
      { id: `${name}#title`, path: `Canvas/${name}/Title`, role: "text", text: s.title, visible: true, visibility: "on_screen" },
      ...s.buttons.map((b, i) => ({
        id: `${name}#${i}`, path: b.path ?? `Canvas/${name}/${b.label.replace(/\W/g, "") || "Icon"}Button`,
        role: b.role ?? "button", text: b.role === "input" ? this.values[b.label] || b.label : b.label,
        visible: true, visibility: "on_screen", interactable: true,
        ...(coveredBy ? { occluded_by: coveredBy } : {}),
      })),
    ];
  }

  async call(tool: string, args: Record<string, unknown>): Promise<CallToolResult> {
    this.calls.push({ tool, args });
    switch (tool) {
      case "unity_open_mcp_ui_snapshot":
        return ok({
          status: "ok", mode: this.mode, screen: { w: 1080, h: 1920, safe_area: [0, 0, 1080, 1920], dpi: 320 },
          nodes: this.mode === "play" ? this.nodes().slice(0, args.page_size === 1 ? 1 : undefined) : [],
          diagnostics: this.mode === "play" ? this.screens[this.screen].diagnostics ?? {} : {},
        });
      case "unity_open_mcp_inputsim_pointer": {
        const [screen, index] = String(args.object_id).split("#");
        if (screen !== this.screen) return ok({ status: "error", error: { code: "target_not_found" } });
        const button = this.screens[screen].buttons[Number(index)];
        this.clicks.push(`${screen}:${button.label}`);
        if (button.exit) this.mode = "edit";
        if (button.to && (!button.requires || this.values[button.requires])) this.screen = button.to;
        return ok({ status: "ok", clicked: true }, button.logs ?? []);
      }
      case "unity_open_mcp_inputsim_text": {
        const [screen, index] = String(args.object_id).split("#");
        const field = this.screens[screen].buttons[Number(index)];
        this.clicks.push(`${screen}:type ${field.label}=${args.text}`);
        this.values[field.label] = String(args.text);
        return ok({ status: "ok", action: args.mode, typed: { requested: String(args.text).length, accepted: String(args.text).length, rejected: 0 }, textAfter: args.text });
      }
      case "unity_senses_screenshot": {
        const file = this.frames ? join(this.frames, `composed-${++this.shots}.png`) : `/tmp/none-${++this.shots}.png`;
        if (this.frames) writeFileSync(file, encodePng({ width: 8, height: 8, data: new Uint8Array(8 * 8 * 4).fill(160) }));
        return ok({ status: "ok", view: "composed", filePath: file });
      }
      case "unity_open_mcp_editor_set_state":
        if (args.state === "stop") this.mode = "edit";
        if (args.state === "play") { this.mode = "play"; this.screen = this.start; }
        return ok({ status: "ok", transition: args.state });
      case "unity_open_mcp_editor_status":
        return ok({ status: "ok", state: this.mode === "play" ? "playing" : "idle" });
      case "unity_open_mcp_project_commands":
        this.screen = this.start;
        return ok({ result: { booted: true } });
      default:
        return ok({ status: "ok" });
    }
  }
}

function options(overrides: Record<string, unknown> = {}): ExploreOptions {
  return { ...parseExploreOptions({ settle_ms: 50, max_settle_ms: 200, ...overrides }) };
}

function recorder(): ExploreRecorder & { labels: string[]; outputs: Array<Record<string, unknown>> } {
  const labels: string[] = [];
  const outputs: Array<Record<string, unknown>> = [];
  let seq = 0;
  return {
    labels, outputs,
    begin(label) {
      labels.push(label);
      const mine = ++seq;
      return { seq: mine, finish: output => { outputs.push(output); } };
    },
  };
}

async function crawl(game: FakeGame, overrides: Record<string, unknown> = {}, signal = new AbortController().signal) {
  const rec = recorder();
  const explorer = new Explorer(options(overrides), { call: (t, a) => game.call(t, a), recorder: rec, signal, sleep: async () => {} });
  return { result: await explorer.run() as Record<string, any>, rec };
}

test("explore: walks every screen, skips denied controls, finds dead buttons, dead ends, console errors and overflow", async () => {
  const game = new FakeGame(GAME);
  const { result, rec } = await crawl(game);
  assert.equal(result.stop_reason, "explored", JSON.stringify(result.notes));
  assert.deepEqual(result.screens.map((s: any) => s.title), ["Main Menu", "Level 1", "Paused", "Shop", "Settings", "Credits", "Daily reward"]);
  // Never tapped: money, leaving the game.
  for (const denied of ["Menu:Buy Coins", "Shop:$0.99 Pack", "Pause:Quit"]) assert.ok(!game.clicks.includes(denied), denied);
  assert.deepEqual(result.denied.map((d: any) => d.label).sort(), ["$0.99 Pack", "Buy Coins", "Quit"]);
  // Back-like controls go last on their screen: Pause before Back on Game.
  assert.ok(game.clicks.indexOf("Game:Pause") < game.clicks.indexOf("Game:Back"), game.clicks.join(" "));
  // The dead end forced a restart; the crawler then walked back to Settings for its last control.
  assert.equal(result.restarts, 1);
  assert.ok(rec.labels.some(l => l.startsWith("Restart the game (play mode)")));
  assert.ok(rec.labels.some(l => l.startsWith("Navigate: tap 'Settings' on S1")), rec.labels.join("\n"));
  assert.equal(game.clicks.at(-1), "Settings:Back");
  // The popup's menu controls were covered, so they were neither tapped there nor given up.
  assert.ok(game.clicks.indexOf("Menu:Daily") > game.clicks.indexOf("Settings:Credits"), game.clicks.join(" "));
  assert.equal(game.clicks[game.clicks.indexOf("Menu:Daily") + 1], "Daily:Claim");
  const kinds = result.findings.map((f: any) => `${f.kind}:${f.screen}:${f.label ?? f.path ?? ""}`);
  assert.ok(kinds.includes("dead_button:S5:Help"), kinds.join("\n"));
  assert.ok(!kinds.some((k: string) => k.startsWith("dead_button:S5:Sound")), "a toggle is not a dead button");
  assert.ok(!kinds.some((k: string) => k.startsWith("dead_button:S5:Report")), "it logged something");
  assert.ok(kinds.some((k: string) => k.startsWith("console_error:S5:NullReferenceException: boom")), kinds.join("\n"));
  assert.ok(kinds.includes("dead_end:S6:"), kinds.join("\n"));
  assert.ok(kinds.includes("text_overflow:S4:Mega pack"), kinds.join("\n"));
  assert.deepEqual(result.finding_counts, { bug: 2, suspicious: 2 });
  assert.equal(result.findings[0].severity, "bug", "bug findings first");
  assert.deepEqual(result.coverage, { screens: 7, actions_total: 14, actions_tried: 14, untried: 0 });
  assert.equal(result.steps, 15, "14 controls + one navigation tap back to Settings");
  // Every new screen was captured once and the steps say where each tap led.
  assert.equal(game.calls.filter(c => c.tool === "unity_senses_screenshot").length, 7);
  assert.ok(result.screens.every((s: any) => typeof s.frame === "string" && typeof s.frame_call === "number"));
  const credits = rec.outputs.find(o => (o.step as any)?.target === "Credits") as any;
  assert.equal(credits.step.outcome, "new_screen");
  assert.equal(credits.step.to, "S6");
  assert.ok(typeof credits.filePath === "string");
  // The detector arguments went to every full snapshot: default placeholder pattern, 44 dp at 320 dpi.
  const full = game.calls.find(c => c.tool === "unity_open_mcp_ui_snapshot" && c.args.page_size === 200)!;
  assert.equal(full.args.placeholder_pattern, DEFAULT_PLACEHOLDER_PATTERN);
  assert.equal(full.args.min_touch_size, 88);
});

test("explore: a play-mode restart runs the boot command and waits for the game to be ready; a refusing command fails it", async () => {
  const game = new FakeGame(GAME);
  const ready = { condition: { project_command: { id: "project.demo.playtest.is_ready", field: "met" } }, timeout_ms: 20_000 };
  const { result } = await crawl(game, { restart_command: "project.demo.playtest.boot", restart_args: { fast: true }, restart_wait: ready });
  assert.equal(result.stop_reason, "explored", JSON.stringify(result.notes));
  assert.equal(result.restarts, 1);
  const order = game.calls.map(c => c.tool === "unity_open_mcp_editor_set_state" ? `state:${c.args.state}` : c.tool.replace("unity_open_mcp_", ""))
    .filter(t => /^state:|^project_commands|^playtest_wait/.test(t));
  assert.deepEqual(order, ["state:stop", "state:play", "project_commands", "playtest_wait"]);
  const boot = game.calls.find(c => c.tool === "unity_open_mcp_project_commands")!;
  assert.deepEqual(boot.args, { action: "invoke", command_id: "project.demo.playtest.boot", args: { fast: true } });
  assert.deepEqual(game.calls.find(c => c.tool === "unity_open_mcp_playtest_wait")!.args, ready);

  // A command that answers {ok:false} (the project's refusal) is a failed restart, not a silent one.
  const refusing = async (tool: string) => tool === "unity_open_mcp_project_commands"
    ? ok({ result: { ok: false, code: "root_already_loaded" } }) : ok({ status: "ok" });
  const failure = await restartGame(refusing, { mode: "command", command: "project.x.boot" }, []);
  assert.match(failure!.message, /restart command project\.x\.boot failed: .*root_already_loaded/);
  const waitFails = async (tool: string) => tool === "unity_open_mcp_playtest_wait"
    ? text({ error: { code: "timed_out", message: "t" } }) : ok({ status: "ok" });
  assert.match((await restartGame(waitFails, { mode: "play_mode", wait: ready }, []))!.message, /wait after it did not hold/);
});

test("detectors: a screen title skips counters and scores", () => {
  assert.equal(screenTitle([
    { path: "C/Top/Coins", role: "text", text: "1 000" }, { path: "C/Win/Level", role: "text", text: "6" },
    { path: "C/Win/Title", role: "text", text: "Profile" }, { path: "C/Win/Close", role: "button", text: "Close" },
  ] as SnapshotNode[]), "Profile");
  assert.equal(screenTitle([{ path: "C/A", role: "text", text: "42" }, { path: "C/B", role: "button", text: "Go on" }] as SnapshotNode[]), "Go on");
  assert.equal(screenTitle([{ path: "C/A", role: "text", text: "42" }] as SnapshotNode[]), "42", "nothing better");
});

test("explore: restart none stops at the dead end; budgets and cancel stop early with the result so far", async () => {
  const stuck = await crawl(new FakeGame(GAME), { restart: "none" });
  assert.equal(stuck.result.stop_reason, "no_way_back");
  assert.ok(stuck.result.untried.some((u: any) => u.screen === "S5" && u.label === "Back"));

  const short = await crawl(new FakeGame(GAME), { max_steps: 2 });
  assert.equal(short.result.stop_reason, "max_steps");
  assert.equal(short.result.steps, 2);

  const game = new FakeGame(GAME);
  const controller = new AbortController();
  const original = game.call.bind(game);
  game.call = async (tool, args) => {
    if (tool === "unity_open_mcp_inputsim_pointer" && game.clicks.length >= 2) controller.abort();
    return original(tool, args);
  };
  const cancelled = await crawl(game, {}, controller.signal);
  assert.equal(cancelled.result.stop_reason, "cancelled");
  assert.ok(cancelled.result.steps >= 2 && cancelled.result.steps <= 3);

  const command = new FakeGame(GAME);
  const booted = await crawl(command, { restart: "command", restart_command: "project.demo.playtest.boot" });
  assert.equal(booted.result.stop_reason, "explored");
  assert.ok(command.calls.some(c => c.tool === "unity_open_mcp_project_commands" && c.args.command_id === "project.demo.playtest.boot"));
  assert.ok(!command.calls.some(c => c.tool === "unity_open_mcp_editor_set_state"));
});

test("explore: text fields are filled before the screen's buttons; without fill_inputs the form's Save is a dead button", async () => {
  const form = {
    Form: { title: "Profile", buttons: [{ label: "Save", to: "Done", requires: "Nickname" }, { label: "Nickname", role: "input" }] },
    Done: { title: "Saved", buttons: [] },
  };
  const filled = new FakeGame(form, "Form");
  const run = await crawl(filled, { restart: "none" });
  assert.deepEqual(filled.clicks, ["Form:type Nickname=Test", "Form:Save"], "the field first, then the button that needs it");
  assert.equal(run.result.stop_reason, "explored");
  assert.deepEqual(run.result.screens.map((x: any) => x.title), ["Profile", "Saved"]);
  assert.ok(run.rec.labels.includes("Type 'Test' into 'Nickname' on S1"), run.rec.labels.join("\n"));
  const typed = run.rec.outputs.find(o => (o.step as any)?.target === "Nickname") as any;
  assert.equal(typed.step.outcome, "same_screen", "typing keeps the screen: a field is known by its place, not its text");
  assert.equal(typed.step.typed.accepted, 4);
  assert.ok(!run.result.findings.some((f: any) => f.kind === "dead_button"));
  assert.ok(run.result.findings.some((f: any) => f.kind === "dead_end" && f.screen === "S2"), "a dead end found last is still reported");

  const custom = new FakeGame(form, "Form");
  await crawl(custom, { restart: "none", fill_text: "Alex" });
  assert.equal(custom.values.Nickname, "Alex");

  const untouched = new FakeGame(form, "Form");
  const skipped = await crawl(untouched, { restart: "none", fill_inputs: false });
  assert.deepEqual(untouched.clicks, ["Form:Save"]);
  assert.ok(skipped.result.findings.some((f: any) => f.kind === "dead_button" && f.label === "Save"));
});

test("explore: play mode ending mid-crawl stops it; edit mode at start refuses", async () => {
  const screens = { Menu: { title: "Menu", buttons: [{ label: "Play", to: "Menu", exit: true }] } };
  const exited = await crawl(new FakeGame(screens));
  assert.equal(exited.result.stop_reason, "play_mode_exited");

  const idle = new FakeGame(GAME);
  idle.mode = "edit";
  await assert.rejects(crawl(idle), (error: any) => error.code === "play_mode_required");

  assert.throws(() => parseExploreOptions({ restart: "command" }), /restart_command/);
  assert.equal(parseExploreOptions({ restart_command: "project.x.boot" }).restartCommand, "project.x.boot", "play mode, then the boot");
  assert.throws(() => parseExploreOptions({ restart: "none", restart_command: "project.x.boot" }), /need a restart/);
  assert.throws(() => parseExploreOptions({ restart_args: { a: 1 } }), /pass restart_command too/);
  assert.throws(() => parseExploreOptions({ restart_wait: { ms: 5 } }), /restart_wait is playtest_wait arguments/);
  assert.throws(() => parseExploreOptions({ deny: ["("] }), /not a valid regular expression/);
  assert.equal(parseExploreOptions({ placeholder_pattern: "" }).placeholderPattern, null);
  assert.equal(parseExploreOptions({ min_touch_dp: 0 }).minTouchDp, null);
});

test("explore: the project profile — known findings listed, not counted; ignored console noise; extra back labels go last", async () => {
  const profile: PlaytestProfile = {
    known: [{ kind: "dead_button", path: "Settings/HelpButton", reason: "opens the browser outside the game" }],
    ignore_errors: ["^NullReferenceException: boom"],
    back_labels: ["Sound"],
  };
  const game = new FakeGame(GAME);
  const explorer = new Explorer(parseExploreOptions({ settle_ms: 50, max_settle_ms: 200 }, memoryOptions(profile)),
    { call: (t, a) => game.call(t, a), recorder: recorder(), signal: new AbortController().signal, sleep: async () => {} });
  const result = await explorer.run() as Record<string, any>;
  assert.equal(result.stop_reason, "explored", JSON.stringify(result.notes));
  const kinds = result.findings.map((f: any) => `${f.kind}:${f.label ?? f.path ?? ""}`);
  assert.ok(!kinds.some((k: string) => k.startsWith("dead_button:Help")), kinds.join("\n"));
  assert.ok(!kinds.some((k: string) => k.startsWith("console_error")), "the profile's console noise is no finding");
  assert.deepEqual(result.finding_counts, { bug: 1, suspicious: 1 }, "overflow + the dead end only");
  assert.deepEqual(result.known.map((k: any) => [k.kind, k.path, k.reason]), [["dead_button", "Canvas/Settings/HelpButton", "opens the browser outside the game"]]);
  // "Sound" leaves the screen for this project: tried after the other Settings controls.
  assert.ok(game.clicks.indexOf("Settings:Sound") > game.clicks.indexOf("Settings:Help"), game.clicks.join(" "));
});

// ---------------------------------------------------------------------------
// Catalog
// ---------------------------------------------------------------------------

test("playtest_explore: schema, playtest group, server-local, intent and chain", () => {
  const tool = ALL_TOOLS.find(t => t.name === PLAYTEST_EXPLORE_TOOL)!;
  assert.ok(tool);
  assert.ok(PLAYTEST_TOOLS.includes(tool));
  assert.equal(groupFor(PLAYTEST_EXPLORE_TOOL), "playtest");
  assert.ok(isLocalTool(PLAYTEST_EXPLORE_TOOL));
  assert.equal(tool.inputSchema.additionalProperties, false);
  for (const [key, value] of Object.entries(tool.inputSchema.properties as Record<string, any>)) assert.equal(value.default, undefined, key);
  for (const phrase of ["jobs job", "deny-list", "no_open_session", "not_isolated", "play_mode_required", "dead_button", "dead_end", "allow_unsandboxed"])
    assert.ok(tool.description!.includes(phrase), phrase);
  for (const intent of ["explore the game UI on its own and find ui bugs", "crawl every screen and button"]) {
    const ids = recommendGroups({ intent }).groups.map(g => g.id);
    assert.ok(ids.includes("playtest"), `${intent}: ${JSON.stringify(ids)}`);
  }
  assert.ok(RECOMMENDED_TOOL_CHAINS.some(c => c.name === "playtest-explore-ui" && c.steps.some(s => s.includes(PLAYTEST_EXPLORE_TOOL))));
});

// ---------------------------------------------------------------------------
// Through the router: session, isolation, play mode, the job and the report
// ---------------------------------------------------------------------------

function routerHarness(t: { after(fn: () => void): void }) {
  const root = mkdtempSync(join(tmpdir(), "uom-explore-"));
  const project = join(root, "project");
  mkdirSync(join(project, "ProjectSettings"), { recursive: true });
  writeFileSync(join(project, "ProjectSettings", "ProjectSettings.asset"), "PlayerSettings:\n  companyName: TestCo\n  productName: Explore\n");
  const frames = join(root, "captures");
  mkdirSync(frames);
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const game = new FakeGame(GAME, "Menu", frames);
  const live = {
    isLiveAvailable: async () => true,
    projectCommands: async (a: Record<string, unknown>) => ({
      command: { id: a.id, available: true, invocationSupported: true, schemaVersion: "1", lifecycle: "none",
        inputSchema: { type: "object", properties: {}, additionalProperties: false } },
    }),
    route: async (name: string, args: Record<string, unknown>) => {
      if (name === "unity_open_mcp_player_state") {
        if (args.action === "list") return ok({ status: "ok", action: "list", company: "TestCo", product: "Explore" });
        if (args.action === "snapshot") return ok({ status: "ok", action: "snapshot", name: args.name });
        if (args.action === "diff") return ok({ status: "ok", action: "diff", identical: true });
      }
      if (name === "unity_open_mcp_game_view")
        return ok({ status: "ok", action: "get", mode: "game", playing: true, screen: { width: 1080, height: 1920, orientation: "portrait", safe_area: [0, 0, 1080, 1920] } });
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
  for (let i = 0; i < 50; i++) {
    const job = body(await router.route("unity_open_mcp_jobs", { action: "wait", job_id: jobId, timeout_ms: 5000 }, ME));
    if (["succeeded", "failed", "cancelled", "orphaned"].includes(job.state)) return job;
  }
  throw new Error("job did not finish");
}

test("router: explore needs an isolated session and play mode, runs as a job, records steps and reports the crawl", async t => {
  const { game, router } = routerHarness(t);

  let answer = body(await router.route(PLAYTEST_EXPLORE_TOOL, {}, ME));
  assert.equal(answer.error.code, "no_open_session");

  assert.equal(body(await router.route("unity_open_mcp_playtest_session", { action: "start", name: "Unsandboxed" }, ME)).status, "ok");
  answer = body(await router.route(PLAYTEST_EXPLORE_TOOL, {}, ME));
  assert.equal(answer.error.code, "not_isolated");
  // The project's isolation hook counts as isolation.
  await router.route("unity_open_mcp_project_commands", { action: "invoke", command_id: "project.demo.playtest.begin", args: {} }, ME);
  game.screen = "Menu";
  game.mode = "edit";
  answer = body(await router.route(PLAYTEST_EXPLORE_TOOL, {}, ME));
  assert.equal(answer.error.code, "play_mode_required", JSON.stringify(answer));
  await router.route("unity_open_mcp_playtest_session", { action: "end" }, ME);

  assert.equal(body(await router.route("unity_open_mcp_playtest_session", { action: "start", name: "Explore", snapshot: true }, ME)).status, "ok");
  game.mode = "play";
  assert.equal(body(await router.route("unity_open_mcp_jobs", { action: "start", tool_or_command: PLAYTEST_EXPLORE_TOOL, args: {} }, ME)).error.code,
    "job_operation_unsupported", "jobs start cannot bypass the session checks");
  // (The play-mode restart is covered by the engine tests; its editor_status wait needs a real instance lock.)
  answer = body(await router.route(PLAYTEST_EXPLORE_TOOL, { settle_ms: 50, max_settle_ms: 200, restart: "command", restart_command: "project.demo.playtest.boot" }, ME));
  assert.equal(answer.status, "running", JSON.stringify(answer));
  assert.equal(answer.isolation.snapshot !== null, true);
  const job = await waitJob(router, answer.job_id);
  assert.equal(job.state, "succeeded", JSON.stringify(job.error ?? job.result?.notes));
  assert.equal(job.result.stop_reason, "explored", JSON.stringify(job.result.notes));
  assert.equal(job.result.coverage.screens, 7);
  assert.ok(job.result.agentNextSteps.some((s: string) => s.includes("playtest_session")));

  // The current screen's own check: Shop overflows.
  game.screen = "Shop";
  const check = body(await router.route("unity_open_mcp_playtest_session", { action: "check", label: "Shop layout" }, ME));
  assert.equal(check.status, "ok", JSON.stringify(check));
  assert.deepEqual(check.findings.map((f: any) => f.kind), ["text_overflow"]);
  assert.equal(check.min_touch_px, 88);

  const ended = body(await router.route("unity_open_mcp_playtest_session", { action: "end" }, ME));
  assert.equal(ended.status, "ok");
  assert.ok(ended.summary.detector_findings.bug >= 2, JSON.stringify(ended.summary.detector_findings));
  assert.deepEqual(ended.summary.explorations.map((e: any) => [e.stop_reason, e.screens]), [["explored", 7]]);
  assert.ok(ended.agentNextSteps.some((s: string) => s.includes("detector finding")));
  const report = readFileSync(ended.report, "utf8");
  for (const phrase of ["## Exploration", "### Run 1 — explored", "| S6 | Credits", "dead_end", "dead_button", "text_overflow",
    "Never tapped (deny-list)", "Buy Coins", "## Screen checks", "### Check 1: Shop layout", "Explore: 15 taps, 7 screens (explored)"])
    assert.ok(report.includes(phrase), phrase);
  assert.match(report, /\[!\[S1\]\(thumbs\/[^)]+\.thumb\.png\)\]\(frames\/[^)]+\.png\)/, "screens link their frame thumbnails");
  // Each tap is a recorded call of kind explore.
  const log = readFileSync(join(ended.folder, "log.jsonl"), "utf8").trim().split("\n").map(l => JSON.parse(l));
  assert.ok(log.filter(e => e.k === "call" && e.kind === "explore").length >= 15);
  assert.ok(log.some(e => e.k === "call" && e.kind === "check"));
  assert.ok(existsSync(join(ended.folder, "report", "frames")));
});

test("router: the project profile reaches the crawl and the check; known findings go to the report; an invalid profile refuses", async t => {
  const { game, router, project } = routerHarness(t);
  mkdirSync(join(project, "Playtests"), { recursive: true });
  const profile: PlaytestProfile = {
    format: "unity-open-mcp.playtest-profile/1",
    explore: { restart: "command", restart_command: "project.demo.playtest.boot", settle_ms: 50, max_settle_ms: 200, deny: ["^Daily$"] },
    detectors: { min_touch_dp: 48 },
    known: [
      { kind: "dead_button", path: "Settings/HelpButton", reason: "opens the browser" },
      { kind: "text_overflow", text: "Mega pack", reason: "the shop art is being redone" },
    ],
  };
  writeFileSync(join(project, "Playtests", "profile.json"), JSON.stringify(profile));
  writeFileSync(join(project, "Playtests", "NOTES.md"), "# Playtest notes\n\n## Boot\n\n- 2026-11-20 — boot with project.demo.playtest.boot\n");

  const started = body(await router.route("unity_open_mcp_playtest_session", { action: "start", name: "Explore", snapshot: true }, ME));
  assert.match(started.memory.notes.text, /boot with project\.demo\.playtest\.boot/);
  assert.ok(started.memory.profile.summary.some((l: string) => l.startsWith("explore: restart")), JSON.stringify(started.memory));
  assert.ok(started.agentNextSteps[0].startsWith("Read memory first"));

  const answer = body(await router.route(PLAYTEST_EXPLORE_TOOL, {}, ME));
  assert.equal(answer.status, "running", JSON.stringify(answer));
  for (const applied of ["explore.restart", "explore.restart_command", "explore.deny (1)", "detectors.min_touch_dp", "known (2)"])
    assert.ok(answer.profile.applied.includes(applied), `${applied}: ${answer.profile.applied}`);
  const job = await waitJob(router, answer.job_id);
  assert.equal(job.state, "succeeded", JSON.stringify(job.error));
  assert.ok(!game.clicks.includes("Menu:Daily"), "the profile's deny pattern");
  assert.ok(game.calls.some(c => c.tool === "unity_open_mcp_project_commands"), "restarted by the profile's command");
  assert.equal(game.calls.find(c => c.tool === "unity_open_mcp_ui_snapshot" && c.args.page_size === 200)!.args.min_touch_size, 96, "48 dp at 320 dpi");
  assert.deepEqual(job.result.known.map((k: any) => k.kind).sort(), ["dead_button", "text_overflow"]);
  assert.deepEqual(job.result.finding_counts, { bug: 1, suspicious: 1 });
  assert.ok(job.result.agentNextSteps.some((s: string) => s.includes("playtest_memory")));

  // The session check takes the profile's detectors and known list too.
  game.screen = "Shop";
  const check = body(await router.route("unity_open_mcp_playtest_session", { action: "check", label: "Shop" }, ME));
  assert.deepEqual(check.findings, []);
  assert.deepEqual(check.known.map((k: any) => k.kind), ["text_overflow"]);
  assert.equal(check.min_touch_px, 96);

  const ended = body(await router.route("unity_open_mcp_playtest_session", { action: "end" }, ME));
  assert.ok(ended.agentNextSteps.some((s: string) => s.includes("playtest_memory note")));
  const report = readFileSync(ended.report, "utf8");
  for (const phrase of ["Project profile `Playtests/profile.json`", "Known findings (accepted in the profile, not counted): 2", "the shop art is being redone", "· known 2"])
    assert.ok(report.includes(phrase), phrase);

  // A broken profile: the crawl and the check refuse rather than run without its deny-list.
  writeFileSync(join(project, "Playtests", "profile.json"), JSON.stringify({ explore: { deny: ["("] } }));
  assert.equal(body(await router.route("unity_open_mcp_playtest_session", { action: "start", name: "Broken", snapshot: true }, ME)).memory.profile.problems.length, 1);
  const refused = body(await router.route(PLAYTEST_EXPLORE_TOOL, {}, ME));
  assert.equal(refused.error.code, "invalid_profile");
  assert.match(refused.error.message, /not a valid regular expression/);
  assert.equal(body(await router.route("unity_open_mcp_playtest_session", { action: "check" }, ME)).error.code, "invalid_profile");
  await router.route("unity_open_mcp_playtest_session", { action: "end" }, ME);
});
