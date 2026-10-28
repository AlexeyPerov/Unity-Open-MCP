// Playtest session recorder: schema / catalog contract, redaction, PNG
// thumbnails, and the recorder through the real ToolRouter with a fake bridge —
// recording only the owner's input / wait / capture / project-command calls,
// one session per owner, errors attributed to the call they happened in (or
// right after), the report (steps, thumbnails, error list, state diff) and
// survival of an MCP server restart (adoption + the lost in-flight call).
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { ToolRouter } from "./tool-router.js";
import { ToolSessionState } from "./tool-session-state.js";
import { ALL_TOOLS, PLAYTEST_TOOLS } from "./tools/index.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import type { BridgeEvent, BridgeEventStream } from "./event-stream.js";
import { groupFor } from "./capabilities/tool-groups.js";
import { recommendGroups } from "./capabilities/intent-groups.js";
import { RECOMMENDED_TOOL_CHAINS } from "./capabilities/cost-hints.js";
import { isLocalTool } from "./local-tools.js";
import { PLAYTEST_SESSION_ACTIONS, PLAYTEST_SESSION_ACTION_KEYS, PLAYTEST_VERDICTS } from "./tools/playtest-session.js";
import { PlaytestRecorder, classifyCall, RECORDING_LIMITS } from "./playtest/recorder.js";
import { Redactor, REDACTED, isSecretKey, secretEnvValues } from "./playtest/redact.js";
import { decodePng, downscale, encodePng, pngSize, thumbnail } from "./playtest/png.js";
import { appDataRoot, readProjectIdentity, safeSegment, sessionId, SESSION_ID_PATTERN } from "./playtest/session-store.js";

const NAME = "unity_open_mcp_playtest_session";
const tool = () => { const t = ALL_TOOLS.find(x => x.name === NAME); assert.ok(t, NAME); return t!; };
const props = () => tool().inputSchema.properties as Record<string, any>;
const body = (result: CallToolResult) => JSON.parse((result.content.find(c => c.type === "text") as { text: string }).text);
const text = (value: unknown): CallToolResult => ({ content: [{ type: "text", text: JSON.stringify(value) }] });
const registry = (output: unknown, logs: unknown[] = []): CallToolResult =>
  text({ mutation: { success: true, output, error: null }, effectiveReadOnly: true, logs });

// ---------------------------------------------------------------------------
// Schema and catalog
// ---------------------------------------------------------------------------

test("playtest_session schema: actions, verdicts, no materialized defaults, per-action keys", () => {
  const p = props();
  assert.deepEqual(p.action.enum, [...PLAYTEST_SESSION_ACTIONS]);
  assert.deepEqual([...PLAYTEST_SESSION_ACTIONS], ["start", "mark", "status", "end", "resume", "list"]);
  assert.deepEqual(p.verdict.enum, [...PLAYTEST_VERDICTS]);
  assert.deepEqual([...PLAYTEST_VERDICTS], ["ok", "bug", "suspicious"]);
  assert.deepEqual(tool().inputSchema.required, ["action"]);
  assert.equal(tool().inputSchema.additionalProperties, false);
  // A schema default would be filled into every action and trip the per-action check.
  for (const [key, value] of Object.entries(p)) assert.equal((value as any).default, undefined, key);
  const keys = new Set(Object.values(PLAYTEST_SESSION_ACTION_KEYS).flat());
  assert.deepEqual([...keys].sort(), Object.keys(p).filter(k => k !== "action").sort());
  assert.ok(new RegExp(p.session.pattern).test(sessionId("Smoke run", new Date("2026-10-28T10:11:12Z"), "ab12")));
  const description = tool().description!;
  for (const phrase of ["session_already_open", "secrets redacted", "Other agents' calls are never recorded", "report.md",
    "survives a domain reload and an MCP server", "session:<id>", "playtest-sessions"])
    assert.ok(description.includes(phrase), phrase);
});

test("playtest_session: playtest group, server-local route, intent and chain", () => {
  assert.ok(PLAYTEST_TOOLS.some(t => t.name === NAME));
  assert.equal(groupFor(NAME), "playtest");
  assert.ok(isLocalTool(NAME));
  for (const intent of ["record the playtest session and write a report for QA", "playtest report with verdicts"]) {
    const ids = recommendGroups({ intent }).groups.map(g => g.id);
    assert.ok(ids.includes("playtest"), `${intent}: ${JSON.stringify(ids)}`);
  }
  const chain = RECOMMENDED_TOOL_CHAINS.find(c => c.name === "playtest-session-report");
  assert.ok(chain);
  assert.ok(chain!.steps.some(s => s.includes("action end")));
});

test("classifyCall records input / wait / capture / project commands / play-state changes only", () => {
  const kind = (t: string, a: Record<string, unknown> = {}) => classifyCall(t, a)?.kind ?? null;
  assert.equal(kind("unity_open_mcp_inputsim_pointer"), "input");
  assert.equal(kind("unity_open_mcp_inputsim_gesture"), "input");
  assert.equal(kind("unity_open_mcp_playtest_wait"), "wait");
  assert.equal(kind("unity_senses_capture_series"), "capture");
  assert.equal(kind("unity_senses_screenshot"), "capture");
  assert.equal(kind("unity_senses_capture_inline"), "capture");
  assert.equal(kind("unity_open_mcp_project_commands", { action: "invoke", command_id: "project.demo.playtest.boot" }), "command");
  assert.equal(kind("unity_open_mcp_project_commands", { action: "list" }), null);
  assert.equal(kind("unity_open_mcp_jobs", { action: "start", tool_or_command: "project.demo.long_write" }), "command");
  assert.equal(kind("unity_open_mcp_jobs", { action: "start", tool_or_command: "unity_open_mcp_playtest_wait" }), "wait");
  assert.equal(kind("unity_open_mcp_jobs", { action: "start", tool_or_command: "unity_senses_run_tests" }), null);
  assert.equal(kind("unity_open_mcp_editor_set_state", { state: "play" }), "control");
  assert.equal(kind("unity_open_mcp_game_view", { action: "set_device" }), "control");
  assert.equal(kind("unity_open_mcp_game_view", { action: "get" }), null);
  for (const t of ["unity_open_mcp_ui_snapshot", "unity_open_mcp_inputsim_probe", "unity_open_mcp_player_state", "unity_open_mcp_execute_csharp", NAME])
    assert.equal(kind(t), null, t);
});

// ---------------------------------------------------------------------------
// Redaction
// ---------------------------------------------------------------------------

test("redaction: secret-named keys at any depth, secret fragments in text, known values", () => {
  assert.ok(isSecretKey("password") && isSecretKey("api_key") && isSecretKey("authToken") && isSecretKey("X-Api-Key")
    && isSecretKey("client_secret") && isSecretKey("refresh_token") && isSecretKey("session_id"));
  for (const harmless of ["key", "target_text", "author", "idempotency_key", "confirm_bypass", "keyboard", "tokenizer_mode", "monkey"])
    assert.ok(!isSecretKey(harmless), harmless);
  const r = new Redactor(["s3cr3t-bridge-token-value"]);
  const out = r.value({
    action: "invoke", command_id: "project.demo.login",
    args: { user: "qa", password: "hunter2", nested: [{ apiKey: "AKIA123", label: "Buy" }] },
    key: "space", note: "logged in with token=abc123 and Authorization: Bearer eyJhbGciOi.payload.sig",
    raw: "{\"access_token\": \"xyz-789\", \"x\": 1}", echo: "header s3cr3t-bridge-token-value seen",
  });
  const json = JSON.stringify(out);
  for (const secret of ["hunter2", "AKIA123", "abc123", "eyJhbGciOi", "xyz-789", "s3cr3t-bridge-token-value"])
    assert.ok(!json.includes(secret), secret);
  assert.equal((out as any).args.user, "qa");
  assert.equal((out as any).args.nested[0].label, "Buy");
  assert.equal((out as any).key, "space", "inputsim_key's key is not a secret");
  assert.equal((out as any).args.password, REDACTED);
  // A PlayerPrefs diff entry keyed by a secret name keeps its type, not its value.
  const diff = r.value({ changed: [{ key: "auth_token", from: { type: "string", value: "old-tok" }, to: { type: "string", value: "new-tok" } },
    { key: "coins", from: { type: "int", value: 1 }, to: { type: "int", value: 2 } }] }) as any;
  assert.deepEqual(diff.changed[0].to, { type: "string", value: REDACTED });
  assert.equal(diff.changed[1].to.value, 2);
  assert.ok(secretEnvValues({ MY_API_TOKEN: "abcdefgh12", PATH: "/usr/bin:/bin", SHORT_TOKEN: "x" }).includes("abcdefgh12"));
  assert.equal(secretEnvValues({ PATH: "/usr/bin:/bin" }).length, 0);
});

// ---------------------------------------------------------------------------
// PNG thumbnails
// ---------------------------------------------------------------------------

function gradient(width: number, height: number, alpha = 255) {
  const data = new Uint8Array(width * height * 4);
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const i = (y * width + x) * 4;
    data[i] = x % 256; data[i + 1] = y % 256; data[i + 2] = (x + y) % 256; data[i + 3] = alpha;
  }
  return { width, height, data };
}

test("png: encode → decode round-trips, downscale averages, thumbnail fits the box", () => {
  for (const alpha of [255, 128]) {
    const image = gradient(37, 23, alpha);
    const decoded = decodePng(encodePng(image))!;
    assert.equal(decoded.width, 37);
    assert.equal(decoded.height, 23);
    assert.deepEqual([...decoded.data], [...image.data]);
  }
  const flat = { width: 4, height: 2, data: new Uint8Array([10, 20, 30, 255, 30, 40, 50, 255, 10, 20, 30, 255, 30, 40, 50, 255,
    10, 20, 30, 255, 30, 40, 50, 255, 10, 20, 30, 255, 30, 40, 50, 255]) };
  const half = downscale(flat, 2, 1);
  assert.deepEqual([...half.data], [20, 30, 40, 255, 20, 30, 40, 255]);
  const big = encodePng(gradient(1600, 900));
  assert.deepEqual(pngSize(big), { width: 1600, height: 900 });
  const thumb = thumbnail(big, 320, 320)!;
  assert.equal(thumb.width, 320);
  assert.equal(thumb.height, 180);
  assert.deepEqual(thumb.source, { width: 1600, height: 900 });
  assert.equal(decodePng(thumb.png)!.width, 320);
  assert.equal(decodePng(Buffer.from("not a png")), null);
});

// ---------------------------------------------------------------------------
// Store helpers
// ---------------------------------------------------------------------------

test("session store: app-data root mirrors the player-state store, segments and ids are path-safe", () => {
  assert.equal(appDataRoot("darwin", {}, "/Users/qa"), "/Users/qa/Library/Application Support/unity-open-mcp");
  assert.equal(appDataRoot("linux", { XDG_DATA_HOME: "/data" }, "/home/qa"), "/data/unity-open-mcp");
  assert.equal(appDataRoot("linux", { XDG_DATA_HOME: "relative" }, "/home/qa"), "/home/qa/.local/share/unity-open-mcp");
  assert.equal(safeSegment("My: Game?"), "My_ Game_");
  assert.equal(safeSegment(".."), "_");
  assert.equal(safeSegment("Demo..."), "Demo");
  const id = sessionId("Smoke: boot → tap!", new Date("2026-10-28T10:11:12.345Z"), "00ff");
  assert.equal(id, "20261028-101112-smoke-boot-tap-00ff");
  assert.ok(SESSION_ID_PATTERN.test(id));
});

// ---------------------------------------------------------------------------
// Recorder through the router (fake bridge)
// ---------------------------------------------------------------------------

interface Harness {
  root: string;
  project: string;
  frames: string;
  emit(event: Partial<BridgeEvent>): void;
  router(pid?: number, alive?: (pid: number) => boolean): ToolRouter;
  calls: Array<{ name: string; args: Record<string, unknown> }>;
}

function harness(t: { after(fn: () => void): void }): Harness {
  const root = mkdtempSync(join(tmpdir(), "uom-playtest-"));
  const project = join(root, "project");
  mkdirSync(join(project, "ProjectSettings"), { recursive: true });
  writeFileSync(join(project, "ProjectSettings", "ProjectSettings.asset"), "PlayerSettings:\n  companyName: TestCo\n  productName: 'Test: Game'\n");
  const frames = join(root, "captures");
  mkdirSync(frames);
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const listeners: Array<(e: BridgeEvent) => void> = [];
  const emit = (event: Partial<BridgeEvent>) => {
    for (const l of listeners) l({ seq: 1, ts: new Date().toISOString(), type: "log", ...event } as BridgeEvent);
  };
  const stream = { addListener: (l: (e: BridgeEvent) => void) => { listeners.push(l); return () => {}; }, ensureSubscription: () => false, reconnectNow: () => {} };
  const calls: Harness["calls"] = [];
  let shot = 0;
  // The stream delivers an event logged during one call while the next one runs.
  let late: Partial<BridgeEvent> | null = null;
  const live = {
    isLiveAvailable: async () => true,
    projectCommands: async (a: Record<string, unknown>) => ({
      command: { id: a.id, available: true, invocationSupported: true, schemaVersion: "1", lifecycle: "none",
        inputSchema: { type: "object", properties: { user: { type: "string" }, password: { type: "string" } }, additionalProperties: false } },
    }),
    route: async (name: string, args: Record<string, unknown>) => {
      calls.push({ name, args });
      if (name === "unity_open_mcp_player_state") {
        if (args.action === "list") return registry({ status: "ok", action: "list", company: "TestCo", product: "Test: Game" });
        if (args.action === "snapshot") return registry({ status: "ok", action: "snapshot", name: args.name });
        if (args.action === "diff") return registry({
          status: "ok", action: "diff", identical: false, to: { current: true, mode: "play" },
          prefs: { added: [{ key: "demo.coins", to: { type: "int", value: 10 } }], removed: [], changed: [
            { key: "auth_token", from: { type: "string", value: "tok-old-123" }, to: { type: "string", value: "tok-new-456" } }],
            counts: { added: 1, removed: 0, changed: 1, unchanged: 3 }, truncated: 0 },
          editor_prefs: null,
          files: { added: [{ path: "save.json", size: 42, sha256: "ab" }], removed: [], changed: [], counts: { added: 1, removed: 0, changed: 0, unchanged: 0 }, truncated: 0 },
        });
      }
      if (name === "unity_open_mcp_game_view")
        return registry({ status: "ok", action: "get", mode: "game", playing: true, size: { label: "Free Aspect" }, screen: { width: 1600, height: 900, orientation: "landscape", safe_area: [0, 0, 1600, 900] } });
      if (name === "unity_open_mcp_inputsim_pointer") {
        if (args.target_text === "Report Bug") {
          // The click handler throws while the call runs; the stream event arrives late.
          late = { ts: new Date().toISOString(), logType: "exception", message: "InvalidOperationException: Deliberate playtest exception", stack: "Game.ReportBug () (at Assets/Game.cs:42)" };
          await new Promise(resolve => setTimeout(resolve, 5));
          return registry({ status: "ok", clicked: true }, [{ severity: "error", message: "InvalidOperationException: Deliberate playtest exception", source: "unity" }]);
        }
        return registry({ status: "ok", clicked: true, backend: ["ugui_dispatch"] });
      }
      if (name === "unity_senses_screenshot") {
        if (late) { emit(late); late = null; }
        const file = join(frames, `composed-${++shot}.png`);
        writeFileSync(file, encodePng(gradient(800, 450)));
        return registry({ status: "ok", view: "composed", filePath: file, capturedAt: { mode: "game", size: "Free Aspect", screen: [800, 450] } });
      }
      if (name === "unity_open_mcp_ui_snapshot") return registry({ status: "ok", nodes: [] });
      if (name === "unity_open_mcp_project_commands")
        return registry({ result: { booted: true, screen: "menu", echo: (args.args as Record<string, unknown>)?.user } });
      return registry({ status: "ok" });
    },
  } as unknown as LiveClient;
  const batch = { isBatchTool: () => false } as unknown as BatchSpawn;
  return {
    root, project, frames, emit, calls,
    router(pid = 4242, alive = () => false) {
      const r = new ToolRouter(live, batch, project, stream as unknown as BridgeEventStream, new ToolSessionState());
      r.usePlaytestRecorder(new PlaytestRecorder({ root: join(root, "sessions"), projectPath: project, knownSecrets: ["bridge-auth-token-xyz"], pid, isAlive: alive }));
      return r;
    },
  };
}

const ME = { agent: "agent-me" };
const OTHER = { agent: "agent-other" };

test("recorder: one session per owner, only the owner's recorded calls, redacted, errors attributed, report written", async t => {
  const h = harness(t);
  const r = h.router();
  t.after(() => r.closeJobs());

  // Nothing is recorded before a session exists.
  await r.route("unity_open_mcp_inputsim_pointer", { action: "click", target_text: "Ignored" }, ME);

  const started = body(await r.route(NAME, { action: "start", name: "Smoke run", device: "Phone portrait", snapshot: true }, ME));
  assert.equal(started.status, "ok", JSON.stringify(started));
  assert.match(started.session, /^\d{8}-\d{6}-smoke-run-[0-9a-f]{4}$/);
  assert.ok(started.folder.includes(join("sessions", "TestCo", "Test_ Game")), started.folder);
  assert.equal(started.snapshot.taken, true);
  assert.equal(started.device.summary, "Phone portrait");
  assert.equal(started.device.view.size, "Free Aspect");
  assert.ok(h.calls.some(c => c.name === "unity_open_mcp_player_state" && c.args.action === "snapshot"));

  // A second start by the same owner refuses; another owner may open its own.
  const again = await r.route(NAME, { action: "start", name: "Again" }, ME);
  assert.equal(again.isError, true);
  assert.equal(body(again).error.code, "session_already_open");
  assert.equal(body(again).session, started.session);
  const wrongKeys = await r.route(NAME, { action: "mark", label: "x", snapshot: true }, ME);
  assert.equal(body(wrongKeys).error.code, "invalid_arguments");

  // Step 1: boot through a project command (a secret in its arguments), tap by label, capture.
  const boot = body(await r.route("unity_open_mcp_project_commands", { action: "invoke", command_id: "project.test.playtest.boot", args: { user: "qa", password: "hunter2-secret" } }, ME));
  assert.equal(boot.mutation.output.result.booted, true);
  await r.route("unity_open_mcp_inputsim_pointer", { action: "click", target_text: "Start Wave" }, ME);
  await r.route("unity_senses_screenshot", { view: "composed" }, ME);
  await r.route("unity_open_mcp_ui_snapshot", {}, ME); // read-only, not recorded
  await r.route("unity_open_mcp_inputsim_pointer", { action: "click", target_text: "Not mine" }, OTHER); // other owner
  const m1 = body(await r.route(NAME, { action: "mark", label: "Start the wave", verdict: "ok" }, ME));
  assert.equal(m1.step, 1);
  assert.deepEqual(m1.since_last_mark, { calls: 3, errors: 0, captures: 1, failed: 0 });

  // Step 2: the deliberate exception during a tap, then one logged after it.
  await r.route("unity_open_mcp_inputsim_pointer", { action: "click", target_text: "Report Bug" }, ME);
  // Outside the call's window (± 50 ms tolerance): a consequence logged frames later.
  await new Promise(resolve => setTimeout(resolve, 120));
  h.emit({ logType: "error", message: "NullReferenceException: popup missing", stack: "Popup.Show ()" });
  await r.route("unity_senses_screenshot", { view: "composed" }, ME);
  const m2 = body(await r.route(NAME, { action: "mark", label: "Report a bug", verdict: "bug", note: "Exception on tap; token=abc123secret" }, ME));
  assert.equal(m2.step, 2);
  assert.equal(m2.since_last_mark.errors, 2);
  assert.match(m2.note, /treat them as findings/);

  const status = body(await r.route(NAME, { action: "status" }, ME));
  assert.equal(status.session.calls, 5);
  assert.equal(status.session.marks, 2);
  assert.equal(body(await r.route(NAME, { action: "status" }, OTHER)).session, null);

  const ended = body(await r.route(NAME, { action: "end", note: "Wave flow works; bug button throws." }, ME));
  assert.equal(ended.status, "ok", JSON.stringify(ended));
  const summary = ended.summary;
  assert.equal(summary.steps, 2);
  assert.equal(summary.calls, 5);
  assert.equal(summary.captures, 2);
  assert.equal(summary.thumbnails, 2);
  assert.deepEqual(summary.verdicts, { ok: 1, bug: 1, suspicious: 0 });
  assert.equal(summary.errors.unique, 2);
  const exception = summary.top_errors.find((e: any) => /Deliberate/.test(e.message));
  assert.equal(exception.first_step, 2);
  assert.equal(exception.first_call, 4, "the tap that threw, although the stream delivered it during the next call");
  assert.equal(exception.relation, "during");
  assert.equal(exception.type, "exception", "the stream's type wins over the response's 'error'");
  assert.equal(exception.count, 1, "stream event and response log are one error");
  const after = summary.top_errors.find((e: any) => /popup missing/.test(e.message));
  assert.equal(after.relation, "after");
  assert.equal(after.first_step, 2);
  assert.deepEqual(summary.state_diff.prefs, { added: 1, removed: 0, changed: 1, unchanged: 3 });
  assert.ok(ended.agentNextSteps.some((s: string) => s.includes("restore")));

  const report = readFileSync(ended.report, "utf8");
  const log = readFileSync(join(ended.folder, "log.jsonl"), "utf8");
  for (const phrase of ["# Playtest report: Smoke run", "### Step 1: Start the wave — ok", "### Step 2: Report a bug — bug",
    "Deliberate playtest exception", "Game.ReportBug () (at Assets/Game.cs:42)", "during call #", "after call #",
    "## State diff", "demo.coins", "save.json", "Phone portrait", "thumbs/", "project.test.playtest.boot", "booted"])
    assert.ok(report.includes(phrase), phrase);
  for (const secret of ["hunter2-secret", "abc123secret", "tok-old-123", "tok-new-456", "bridge-auth-token-xyz"]) {
    assert.ok(!report.includes(secret), `report leaks ${secret}`);
    assert.ok(!log.includes(secret), `log leaks ${secret}`);
  }
  assert.ok(!log.includes("Ignored") && !log.includes("Not mine"), "calls outside the session / of other owners are not logged");
  assert.equal(readdirSync(join(ended.folder, "report", "frames")).length, 2);
  assert.equal(readdirSync(join(ended.folder, "report", "thumbs")).length, 2);
  const meta = JSON.parse(readFileSync(join(ended.folder, "session.json"), "utf8"));
  assert.equal(meta.state, "ended");
  assert.equal(meta.report, ended.report);

  // After end the owner can start again; mark without a session refuses.
  assert.equal(body(await r.route(NAME, { action: "mark", label: "late" }, ME)).error.code, "no_open_session");
  const list = body(await r.route(NAME, { action: "list" }, ME));
  assert.equal(list.sessions[0].state, "ended");
});

test("recorder: an MCP server restart keeps the log; end with session adopts it and reports the lost in-flight call", async t => {
  const h = harness(t);
  const first = h.router(1111);
  t.after(() => first.closeJobs());
  const started = body(await first.route(NAME, { action: "start", name: "Restart" }, ME));
  await first.route("unity_open_mcp_inputsim_pointer", { action: "click", target_text: "Start Wave" }, ME);
  body(await first.route(NAME, { action: "mark", label: "Tap", verdict: "ok" }, ME));
  // The server dies while a wait is in flight: started, never finished.
  const owner = { project: h.project, agent: ME.agent };
  assert.ok(first.playtest.begin(owner, "unity_open_mcp_playtest_wait", { condition: { ms: 60000 } }));

  // A new process (new pid, new process agent id); the old pid is gone.
  const second = h.router(2222, () => false);
  t.after(() => second.closeJobs());
  const restarted = { agent: "agent-new-process" };
  const missing = await second.route(NAME, { action: "end" }, restarted);
  assert.equal(body(missing).error.code, "no_open_session");
  assert.equal(body(missing).orphaned[0].session, started.session);
  const status = body(await second.route(NAME, { action: "status" }, restarted));
  assert.equal(status.orphaned[0].session, started.session);

  const ended = body(await second.route(NAME, { action: "end", session: started.session }, restarted));
  assert.equal(ended.status, "ok", JSON.stringify(ended));
  assert.equal(ended.summary.calls, 1);
  assert.equal(ended.summary.lost_in_flight.length, 1);
  assert.equal(ended.summary.lost_in_flight[0].tool, "unity_open_mcp_playtest_wait");
  const report = readFileSync(ended.report, "utf8");
  assert.match(report, /Lost in flight:\*\* call #2 `playtest_wait`/);
  assert.ok(report.includes('`{"condition":{"ms":60000}}` | **lost**'), "the lost call keeps its arguments");
  assert.match(report, /new MCP server process took the session over from pid 1111/);
  const meta = JSON.parse(readFileSync(join(ended.folder, "session.json"), "utf8"));
  assert.equal(meta.adoptions[0].from_pid, 1111);
  assert.equal(meta.owner.agent, restarted.agent);
});

test("recorder: a live owner process keeps its session; resume continues numbering after a restart", async t => {
  const h = harness(t);
  const first = h.router(3333);
  t.after(() => first.closeJobs());
  const started = body(await first.route(NAME, { action: "start", name: "Shared" }, ME));
  await first.route("unity_open_mcp_inputsim_pointer", { action: "click", target_text: "A" }, ME);

  // Another running MCP server process cannot take it.
  const other = h.router(4444, pid => pid === 3333);
  t.after(() => other.closeJobs());
  const refused = await other.route(NAME, { action: "end", session: started.session }, OTHER);
  assert.equal(body(refused).error.code, "session_owned_by_other_agent");
  // Inside one process, another agent cannot either.
  const same = await first.route(NAME, { action: "end", session: started.session }, OTHER);
  assert.equal(body(same).error.code, "session_owned_by_other_agent");

  // Once the owner's process is gone, resume re-attaches and recording continues.
  const later = h.router(5555, () => false);
  t.after(() => later.closeJobs());
  const resumed = body(await later.route(NAME, { action: "resume", session: started.session }, ME));
  assert.equal(resumed.status, "ok", JSON.stringify(resumed));
  assert.equal(resumed.session.calls, 1);
  await later.route("unity_open_mcp_inputsim_pointer", { action: "click", target_text: "B" }, ME);
  const ended = body(await later.route(NAME, { action: "end" }, ME));
  assert.equal(ended.summary.calls, 2);
  const log = readFileSync(join(ended.folder, "log.jsonl"), "utf8").trim().split("\n").map(l => JSON.parse(l));
  assert.deepEqual(log.filter(e => e.k === "call").map(e => e.seq), [1, 2]);
});

test("recorder: limits are declared and bounded", () => {
  assert.equal(RECORDING_LIMITS.maxCalls, 500);
  assert.equal(RECORDING_LIMITS.maxFrames, 300);
  assert.equal(RECORDING_LIMITS.maxThumbnails, 60);
  assert.ok(RECORDING_LIMITS.maxArgsChars <= 4000);
});

test("project identity fallback reads plain and quoted ProjectSettings scalars", t => {
  const dir = mkdtempSync(join(tmpdir(), "uom-identity-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(join(dir, "ProjectSettings"));
  writeFileSync(join(dir, "ProjectSettings", "ProjectSettings.asset"), "  companyName: \"Acme \\\"Games\\\"\"\n  productName: Space Run\n");
  assert.deepEqual(readProjectIdentity(dir), { company: "Acme \"Games\"", product: "Space Run" });
  assert.equal(readProjectIdentity(join(dir, "missing")), null);
  assert.ok(!existsSync(join(dir, "missing")));
});
