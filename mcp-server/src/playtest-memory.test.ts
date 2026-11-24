// Project playtest memory (unity_open_mcp_playtest_memory): the profile's schema
// and validation, how it fills a call's arguments (precedence, lists that add
// up, restart rules), known findings, the replay options it feeds, the notes
// file (dated bullets, sections, the generated screen map) and the tool through
// the real ToolRouter — show / note / map / set / add, refusals, the catalog.
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
import { PLAYTEST_MEMORY_TOOL } from "./tools/playtest-memory.js";
import { PlaytestRecorder } from "./playtest/recorder.js";
import {
  KnownFindings, NOTES_TEMPLATE, PROFILE_FORMAT, addToProfile, appendNote, changeProfile, detectorArgsWithProfile, devicesArgsWithProfile,
  exploreArgsWithProfile, memoryBlock, memoryOptions, mergePatch, readProfile, replaceSection, requireProfile, screenMapLines, validateProfile,
  type PlaytestProfile,
} from "./playtest/profile.js";
import { prepareReplay } from "./playtest/replay-job.js";
import { SCENARIO_FORMAT, writeScenarioFile } from "./playtest/scenario.js";

const body = (result: CallToolResult) => JSON.parse((result.content.find(c => c.type === "text") as { text: string }).text);
const ok = (output: unknown): CallToolResult =>
  ({ content: [{ type: "text", text: JSON.stringify({ mutation: { success: true, output, error: null }, effectiveReadOnly: true, logs: [] }) }] });

function tempProject(t: { after(fn: () => void): void }): string {
  const root = mkdtempSync(join(tmpdir(), "uom-memory-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const project = join(root, "project");
  mkdirSync(join(project, "ProjectSettings"), { recursive: true });
  writeFileSync(join(project, "ProjectSettings", "ProjectSettings.asset"), "PlayerSettings:\n  companyName: TestCo\n  productName: Memory\n");
  return project;
}

// ---------------------------------------------------------------------------
// Profile: validation and precedence
// ---------------------------------------------------------------------------

test("profile: sections are checked against the tools' own schemas", () => {
  assert.deepEqual(validateProfile({
    format: PROFILE_FORMAT,
    explore: { deny: ["Reset"], restart: "command", restart_command: "project.demo.playtest.boot", fill_text: "Tester", max_steps: 80 },
    devices: { preset: "notch", orientations: ["portrait", "landscape"] },
    detectors: { min_touch_dp: 48, placeholder_pattern: "^#.+#$" },
    back_labels: ["Назад"], ignore_errors: ["^Ads: no fill"],
    known: [{ kind: "small_touch_target", path: "Settings/Close", reason: "decorative" }],
  }), []);
  const problems = validateProfile({
    format: "other", colour: "red",
    explore: { max_steps: 1000, allow_unsandboxed: true },
  });
  assert.ok(problems.some(p => p.includes("profile.format")), problems.join("\n"));
  assert.ok(problems.some(p => p.includes("profile.colour is unknown")), problems.join("\n"));
  assert.ok(problems.some(p => p.includes("profile.explore.max_steps violates maximum")), problems.join("\n"));
  assert.ok(problems.some(p => p.includes("profile.explore.allow_unsandboxed is unknown")), "isolation stays a per-call decision");
  const semantic = (value: unknown) => validateProfile(value).join("\n");
  assert.match(semantic({ explore: { restart: "command" } }), /needs restart_command/);
  assert.match(semantic({ explore: { restart: "none", restart_command: "project.x.boot" } }), /only with restart "command"/);
  assert.match(semantic({ explore: { deny: ["("] } }), /deny\[0\] '\(' is not a valid regular expression/);
  assert.match(semantic({ ignore_errors: ["[a"] }), /ignore_errors\[0\]/);
  assert.match(semantic({ detectors: { placeholder_pattern: "(?<" } }), /placeholder_pattern/);
  assert.match(semantic({ devices: { devices: ["X"], preset: "all" } }), /devices or preset, not both/);
  assert.match(semantic({ known: [{ kind: "dead_end", reason: "fine" }] }), /needs path or text/);
  assert.match(semantic({ known: [{ kind: "nope", path: "A", reason: "x" }] }), /kind must be one of/);
  assert.match(semantic({ known: [{ kind: "dead_end", text: "Credits" }] }), /reason is required/);
  assert.match(semantic([1]), /JSON object/);
});

test("profile: call arguments win, lists add up, restart settings only where the run restarts by command", () => {
  const profile: PlaytestProfile = {
    explore: { deny: ["Reset"], allow: ["Restore Purchases"], restart: "command", restart_command: "project.demo.playtest.boot", restart_args: { fast: true }, fill_text: "Tester", same_screen: 0.85, placeholder_pattern: "^#" },
    detectors: { min_touch_dp: 48, placeholder_pattern: "^%" },
  };
  const plain = exploreArgsWithProfile({}, profile);
  assert.deepEqual(plain.args, {
    restart: "command", restart_command: "project.demo.playtest.boot", restart_args: { fast: true }, fill_text: "Tester", same_screen: 0.85,
    placeholder_pattern: "^#", deny: ["Reset"], allow: ["Restore Purchases"], min_touch_dp: 48,
  }, "the explore section beats the detectors section");
  assert.ok(plain.applied.includes("explore.deny (1)") && plain.applied.includes("detectors.min_touch_dp"), plain.applied.join(", "));

  const mine = exploreArgsWithProfile({ deny: ["Delete"], fill_text: "Me", min_touch_dp: 0 }, profile).args;
  assert.deepEqual(mine.deny, ["Reset", "Delete"]);
  assert.equal(mine.fill_text, "Me");
  assert.equal(mine.min_touch_dp, 0);
  // restart: another mode → none of the profile's restart settings; a command of its own → not the profile's.
  const playMode = exploreArgsWithProfile({ restart: "play_mode" }, profile).args;
  assert.equal(playMode.restart_command, undefined);
  assert.equal(playMode.restart_args, undefined);
  const own = exploreArgsWithProfile({ restart: "command", restart_command: "project.other.boot" }, profile).args;
  assert.equal(own.restart_command, "project.other.boot");
  assert.equal(own.restart_args, undefined);
  // Restart by command with no command of its own takes the profile's.
  assert.equal(exploreArgsWithProfile({ restart: "command" }, { explore: { restart_command: "project.x.boot" } }).args.restart_command, "project.x.boot");
  assert.deepEqual(exploreArgsWithProfile({ max_steps: 5 }, null), { args: { max_steps: 5 }, applied: [] });

  // Devices: the list or preset as a pair; the crawl per device gets the explore section, the call's detectors win.
  const devices = devicesArgsWithProfile({ screens: "explore", min_touch_dp: 30 }, { devices: { preset: "notch", orientations: ["portrait"] }, explore: { max_steps: 20, min_touch_dp: 60 }, detectors: { placeholder_pattern: "^%" } });
  assert.deepEqual(devices.args, { screens: "explore", min_touch_dp: 30, preset: "notch", orientations: ["portrait"], placeholder_pattern: "^%", explore: { max_steps: 20 } });
  assert.equal(devicesArgsWithProfile({}, { explore: { max_steps: 20 } }).args.explore, undefined, "no crawl, no explore options");

  assert.deepEqual(detectorArgsWithProfile({ label: "x" }, profile), { args: { label: "x", min_touch_dp: 48, placeholder_pattern: "^%" }, applied: ["detectors.placeholder_pattern", "detectors.min_touch_dp"] });
});

test("profile: known findings by kind and path tail, text, device; the replay takes known and console noise", t => {
  const known = new KnownFindings([
    { kind: "small_touch_target", path: "Settings/Close", reason: "decorative" },
    { kind: "dead_end", text: "credits", reason: "the credits screen closes by itself" },
    { kind: "outside_safe_area", path: "HUD/Top", device: "notch", reason: "intended" },
  ]);
  const at = (kind: string, extra: Record<string, unknown>) => known.match({ kind, detail: "", ...extra } as never);
  assert.ok(at("small_touch_target", { path: "Canvas/Settings/Close" }));
  assert.ok(at("small_touch_target", { path: "settings/close" }), "case-insensitive whole path");
  assert.equal(at("small_touch_target", { path: "Canvas/MySettings/Close" }), null, "a tail starts at a segment");
  assert.equal(at("text_overflow", { path: "Canvas/Settings/Close" }), null, "another kind");
  assert.ok(at("dead_end", { detail: "screen 'Credits' has nothing to tap" }));
  assert.ok(at("outside_safe_area", { path: "C/HUD/Top", device: "iOS Notch Device Small" }));
  assert.equal(at("outside_safe_area", { path: "C/HUD/Top", device: "Tablet" }), null);
  const split = known.split([
    { kind: "small_touch_target", severity: "suspicious", path: "C/Settings/Close", detail: "small" },
    { kind: "text_overflow", severity: "bug", path: "C/Shop/Price", detail: "long" },
  ]);
  assert.deepEqual(split.kept.map(f => f.kind), ["text_overflow"]);
  assert.equal(split.known[0].reason, "decorative");

  const project = join(mkdtempSync(join(tmpdir(), "uom-memory-replay-")), "p");
  t.after(() => rmSync(join(project, ".."), { recursive: true, force: true }));
  writeScenarioFile(join(project, "Playtests", "Scenarios", "s.json"), {
    format: SCENARIO_FORMAT, name: "s", start: { restart: "none" }, vars: {}, settings: { same_screen: 0.8, step_timeout_ms: 5000, routing: "dispatch", ignore_errors: ["^Own"] },
    steps: [{ do: "tap", target: { path: "C/B" } }],
  } as never);
  const memory = memoryOptions({ ignore_errors: ["^Ads:"], known: [{ kind: "dead_end", text: "x", reason: "y" }] });
  const prepared = prepareReplay(project, { scenario: "s", ...detectorArgsWithProfile({}, { detectors: { min_touch_dp: 0 } }).args }, memory);
  assert.deepEqual(prepared.options.ignoreErrors.map(r => r.source), ["^Own", "^Ads:"]);
  assert.equal(prepared.options.known, memory.known);
  assert.equal(prepared.options.minTouchDp, null, "the profile's detectors reach the replay");
});

// ---------------------------------------------------------------------------
// Writing the profile
// ---------------------------------------------------------------------------

test("profile: set is a merge patch, add appends without duplicates; an invalid result writes nothing", t => {
  const project = tempProject(t);
  assert.deepEqual(mergePatch({ a: { b: 1, c: 2 }, l: [1] }, { a: { b: null, d: 3 }, l: [2] }), { a: { c: 2, d: 3 }, l: [2] });

  const created = changeProfile(project, "set", { explore: { fill_text: "Tester", deny: ["Reset"] }, detectors: { min_touch_dp: 48 } });
  assert.equal(created.created, true);
  const file = join(project, "Playtests", "profile.json");
  assert.equal(JSON.parse(readFileSync(file, "utf8")).format, PROFILE_FORMAT, "written with its format first");
  assert.ok((created.summary as string[]).some(l => l.startsWith("explore: fill_text \"Tester\", deny (1)")), JSON.stringify(created.summary));

  const added = changeProfile(project, "add", { explore: { deny: ["Reset", "Delete account"] }, known: [{ kind: "dead_end", text: "Credits", reason: "closes itself" }], back_labels: ["Назад"] });
  assert.deepEqual(added.added, { "explore.deny": 1, known: 1, back_labels: 1 });
  const again = changeProfile(project, "add", { known: [{ kind: "dead_end", text: "credits", reason: "another reason" }] });
  assert.deepEqual(again.added, { known: 0 }, "the same element is one known entry");
  assert.deepEqual(readProfile(project).profile!.explore!.deny, ["Reset", "Delete account"]);

  // set replaces lists and removes with null; empty sections are dropped.
  changeProfile(project, "set", { explore: { deny: ["Wipe"], fill_text: null }, detectors: null });
  const now = readProfile(project).profile!;
  assert.deepEqual(now.explore, { deny: ["Wipe"] });
  assert.equal(now.detectors, undefined);
  changeProfile(project, "set", { explore: null });
  assert.equal(readProfile(project).profile!.explore, undefined);

  const before = readFileSync(file, "utf8");
  assert.throws(() => changeProfile(project, "set", { explore: { deny: ["("] } }), (e: any) => e.code === "invalid_profile" && /nothing was written/.test(e.message));
  assert.throws(() => changeProfile(project, "add", { detectors: { min_touch_dp: [3] } }), (e: any) => e.code === "invalid_arguments" && /use action "set"/.test(e.message));
  assert.throws(() => changeProfile(project, "add", { known: { kind: "dead_end" } }), (e: any) => /must be an array/.test(e.message));
  assert.throws(() => changeProfile(project, "set", {}), (e: any) => e.code === "invalid_arguments");
  assert.equal(readFileSync(file, "utf8"), before);
  assert.throws(() => addToProfile({}, "x"), (e: any) => e.code === "invalid_arguments");

  // A hand-broken file: requireProfile refuses, set cannot patch what it cannot parse.
  writeFileSync(file, "{ not json");
  assert.throws(() => requireProfile(project), (e: any) => e.code === "invalid_profile" && Array.isArray(e.detail.problems));
  assert.throws(() => changeProfile(project, "set", { back_labels: ["Back"] }), (e: any) => e.code === "invalid_profile" && /by hand/.test(e.message));
  assert.equal(requireProfile(undefined), null);
});

// ---------------------------------------------------------------------------
// Notes
// ---------------------------------------------------------------------------

test("notes: dated bullets under their section, new sections at the end, the generated screen map replaced whole", t => {
  const project = tempProject(t);
  const first = appendNote(project, "Boot", "Enter play mode, then invoke project.demo.playtest.boot.", "2026-11-24");
  assert.equal(first.created, true);
  appendNote(project, "dangerous controls", "Settings › Reset progress wipes the save.\nIt has no confirmation.", "2026-11-24");
  appendNote(project, "Boot", "The first launch shows a GDPR popup: tap Accept.", "2026-11-25");
  appendNote(project, "Currency", "Coins start at 100 on a fresh save.", "2026-11-25");
  const text = readFileSync(join(project, "Playtests", "NOTES.md"), "utf8");
  assert.ok(text.startsWith(NOTES_TEMPLATE.split("\n")[0]));
  assert.ok(text.includes([
    "## Boot", "", "- 2026-11-24 — Enter play mode, then invoke project.demo.playtest.boot.", "- 2026-11-25 — The first launch shows a GDPR popup: tap Accept.", "", "## Dangerous controls",
  ].join("\n")), text);
  assert.ok(text.includes("## Dangerous controls\n\n- 2026-11-24 — Settings › Reset progress wipes the save.\n  It has no confirmation.\n"), text);
  assert.ok(text.trimEnd().endsWith("## Currency\n\n- 2026-11-25 — Coins start at 100 on a fresh save."), text);
  assert.throws(() => appendNote(project, "Screen map", "x", "d"), /written by action "map"/);
  assert.throws(() => appendNote(project, "## Boot", "x", "d"), /short heading/);
  assert.throws(() => appendNote(project, "Boot", "   ", "d"), /needs text/);

  assert.equal(replaceSection("# T\n\n## A\n\nold\n\n## B\n\nb\n", "a", ["new"]), "# T\n\n## A\n\nnew\n\n## B\n\nb\n");
  assert.equal(replaceSection("# T\n", "Map", ["x"]), "# T\n\n## Map\n\nx\n");

  const block = memoryBlock(project);
  assert.match(String((block.notes as any).text), /## Boot/);
  assert.equal(block.profile, null);
  assert.deepEqual(memoryBlock(undefined), { notes: null, profile: null });
});

test("notes: the screen map of a crawl — taps from the start, denied controls, dead ends, screens reached only by a restart", () => {
  const note = {
    stop_reason: "explored",
    screens: [
      { id: "S1", title: "Main Menu" }, { id: "S2", title: "Settings" }, { id: "S3", title: "Credits", dead_end: true },
      { id: "S4", title: "Shop" }, { id: "S5", title: "Shop" }, { id: "S6", title: "Results" },
    ],
    edges: [
      { from: "S1", action: "Settings", to: "S2", outcome: "new_screen" }, { from: "S2", action: "Credits", to: "S3", outcome: "new_screen" },
      { from: "S2", action: "Back", to: "S1", outcome: "known_screen" }, { from: "S1", action: "Shop", to: "S4", outcome: "new_screen" },
      { from: "S4", action: "Gems", to: "S5", outcome: "new_screen" }, { from: "S1", action: "Help", to: "S1", outcome: "same_screen" },
    ],
    denied: [{ screen: "S1", label: "Buy Coins", path: "C/Buy", reason: "price" }, { screen: "S4", label: "$0.99", path: "C/P", reason: "price" }],
  };
  const { lines, screens } = screenMapLines(note, { session: "20261124-100000-explore-ab12", crawl: 1, date: "2026-11-24" });
  assert.equal(screens, 6);
  assert.ok(lines[0].startsWith("<!-- Generated by playtest_memory map from crawl 1 of session 20261124-100000-explore-ab12"));
  for (const line of [
    "- **Main Menu** — start; never tapped: 'Buy Coins'",
    "- **Settings** — start → 'Settings'",
    "- **Credits** — start → 'Settings' → 'Credits'; dead end",
    "- **Shop (S4)** — start → 'Shop'; never tapped: '$0.99'",
    "- **Shop (S5)** — start → 'Shop' → 'Gems'",
    "- **Results** — reached only after a restart",
  ]) assert.ok(lines.includes(line), `${line}\n${lines.join("\n")}`);
  assert.throws(() => screenMapLines({ screens: [] }, { session: "s", crawl: 1, date: "d" }), (e: any) => e.code === "nothing_to_map");
});

// ---------------------------------------------------------------------------
// The tool through the router
// ---------------------------------------------------------------------------

function harness(t: { after(fn: () => void): void }) {
  const project = tempProject(t);
  const live = {
    isLiveAvailable: async () => true,
    route: async (name: string, args: Record<string, unknown>) => {
      if (name === "unity_open_mcp_player_state" && args.action === "list") return ok({ status: "ok", action: "list", company: "TestCo", product: "Memory" });
      if (name === "unity_open_mcp_game_view") return ok({ status: "ok", action: "get", mode: "game", playing: true, screen: { width: 1080, height: 1920 } });
      return ok({ status: "ok" });
    },
  } as unknown as LiveClient;
  const stream = { addListener: () => () => {}, ensureSubscription: () => false, reconnectNow: () => {} };
  const router = new ToolRouter(live, { isBatchTool: () => false } as unknown as BatchSpawn, project, stream as unknown as BridgeEventStream, new ToolSessionState());
  const recorder = new PlaytestRecorder({ root: join(project, "..", "sessions"), projectPath: project, pid: 4242, isAlive: () => false });
  router.usePlaytestRecorder(recorder);
  t.after(() => router.closeJobs());
  return { project, router, recorder };
}

const ME = { agent: "agent-me" };

test("router: playtest_memory show / note / set / add / map, with secrets redacted and refusals", async t => {
  const { project, router, recorder } = harness(t);
  const call = async (args: Record<string, unknown>) => body(await router.route(PLAYTEST_MEMORY_TOOL, args, ME));

  const empty = await call({});
  assert.equal(empty.notes, null);
  assert.equal(empty.profile, null);
  assert.ok(empty.agentNextSteps[0].includes("no playtest memory yet"));

  const noted = await call({ action: "note", section: "Boot", text: "Log in with password=hunter2 then tap Play" });
  assert.equal(noted.status, "ok", JSON.stringify(noted));
  assert.equal(noted.file, "Playtests/NOTES.md");
  const notes = readFileSync(join(project, "Playtests", "NOTES.md"), "utf8");
  assert.ok(!notes.includes("hunter2"), "secrets never reach a versioned file");
  assert.match(notes, /- \d{4}-\d{2}-\d{2} — Log in with password=\[redacted\] then tap Play/);

  const set = await call({ action: "set", profile: { explore: { deny: ["Reset"] } } });
  assert.equal(set.status, "ok", JSON.stringify(set));
  const add = await call({ action: "add", profile: { known: [{ kind: "dead_button", path: "Settings/Help", reason: "opens the browser" }] } });
  assert.deepEqual(add.added, { known: 1 });
  const bad = await call({ action: "set", profile: { explore: { max_steps: 0 } } });
  assert.equal(bad.error.code, "invalid_profile");
  assert.ok(bad.problems.some((p: string) => p.includes("max_steps violates minimum")));

  const shown = await call({ action: "show" });
  assert.deepEqual(shown.notes.sections.map((s: any) => [s.title, s.bullets]), [["Boot", 1], ["Dangerous controls", 0], ["Known issues", 0], ["Flaky spots", 0]]);
  assert.deepEqual(shown.profile.summary, ["explore: deny (1)", "known: 1 accepted finding(s)"]);
  assert.equal((await call({ action: "show", section: "boot" })).notes.section, "Boot");
  assert.equal((await call({ action: "show", section: "Nope" })).error.code, "section_not_found");
  assert.equal((await call({ action: "note", section: "Boot" })).error.code, "invalid_arguments");
  assert.match((await call({ action: "show", text: "x" })).error.message, /Keys not used by show: text/);
  assert.match((await call({ action: "forget" })).error.message, /must be one of show, note, map, set, add/);

  // map: the open session's last crawl (an explore block as the crawler files it).
  assert.equal((await call({ action: "map" })).error.code, "no_session");
  const started = body(await router.route("unity_open_mcp_playtest_session", { action: "start", name: "Map" }, ME));
  assert.equal(started.status, "ok", JSON.stringify(started));
  assert.equal(started.memory.notes.file, "Playtests/NOTES.md");
  assert.equal((await call({ action: "map" })).error.code, "nothing_to_map");
  const owner = { project, agent: ME.agent };
  recorder.note(owner, { k: "explore", stop_reason: "explored", screens: [{ id: "S1", title: "Menu" }, { id: "S2", title: "Options" }], edges: [{ from: "S1", action: "Options", to: "S2", outcome: "new_screen" }], denied: [] });
  const mapped = await call({ action: "map" });
  assert.equal(mapped.status, "ok", JSON.stringify(mapped));
  assert.equal(mapped.screens, 2);
  assert.equal(mapped.session, started.session);
  const again = await call({ action: "map", crawl: 1 });
  assert.equal(again.status, "ok");
  const text = readFileSync(join(project, "Playtests", "NOTES.md"), "utf8");
  assert.equal(text.split("## Screen map").length, 2, "the next map replaces the section");
  assert.ok(text.includes("- **Options** — start → 'Options'"), text);
  assert.equal((await call({ action: "map", crawl: 3 })).error.code, "invalid_arguments");
  const ended = body(await router.route("unity_open_mcp_playtest_session", { action: "end" }, ME));
  assert.ok(ended.agentNextSteps.some((s: string) => s.includes("playtest_memory")));
  assert.ok(existsSync(join(project, "Playtests", "profile.json")));
});

test("router: playtest_memory needs a project path", async () => {
  const live = { isLiveAvailable: async () => true } as unknown as LiveClient;
  const stream = { addListener: () => () => {}, ensureSubscription: () => false, reconnectNow: () => {} };
  const router = new ToolRouter(live, { isBatchTool: () => false } as unknown as BatchSpawn, "", stream as unknown as BridgeEventStream, new ToolSessionState());
  assert.equal(body(await router.route(PLAYTEST_MEMORY_TOOL, {}, ME)).error.code, "project_unknown");
  router.closeJobs();
});

test("playtest_memory: schema, playtest group, server-local, intent and chain", () => {
  const tool = ALL_TOOLS.find(t => t.name === PLAYTEST_MEMORY_TOOL)!;
  assert.ok(tool);
  assert.ok(PLAYTEST_TOOLS.includes(tool));
  assert.equal(groupFor(PLAYTEST_MEMORY_TOOL), "playtest");
  assert.ok(isLocalTool(PLAYTEST_MEMORY_TOOL));
  assert.equal(tool.inputSchema.additionalProperties, false);
  for (const phrase of ["Playtests/NOTES.md", "Playtests/profile.json", "known", "back_labels", "ignore_errors", "invalid_profile", "merge patch", "Screen map"])
    assert.ok(tool.description!.includes(phrase), phrase);
  for (const intent of ["remember what the playtest learned about this project", "keep playtest notes and the screen map in the project"]) {
    const ids = recommendGroups({ intent }).groups.map(g => g.id);
    assert.ok(ids.includes("playtest"), `${intent}: ${JSON.stringify(ids)}`);
  }
  assert.ok(RECOMMENDED_TOOL_CHAINS.some(c => c.name === "playtest-project-memory" && c.steps.some(s => s.includes(PLAYTEST_MEMORY_TOOL))));
});
