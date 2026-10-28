import test from "node:test";
import assert from "node:assert/strict";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { LiveClient } from "./live-client.js";
import { ALL_TOOLS } from "./tools/index.js";
import { routeGameView, GAME_VIEW_TOOL } from "./game-view-settle.js";

function tool(name: string) {
  const value = ALL_TOOLS.find((t) => t.name === name);
  assert(value, name);
  return value;
}
function props(name: string) {
  return tool(name).inputSchema.properties as Record<string, any>;
}

// ---------------------------------------------------------------------------
// Schema contract
// ---------------------------------------------------------------------------

test("game_view schema: actions, bounds, and no server-filled defaults that change behaviour", () => {
  const p = props(GAME_VIEW_TOOL);
  assert.deepEqual(p.action.enum, ["get", "set_resolution", "set_device", "list_devices"]);
  assert.deepEqual(tool(GAME_VIEW_TOOL).inputSchema.required, ["action"]);
  for (const key of ["width", "height"]) {
    assert.equal(p[key].type, "integer");
    assert.equal(p[key].minimum, 16);
    assert.equal(p[key].maximum, 8192);
    assert.equal(p[key].default, undefined, `${key} must not default (it selects set_resolution's form)`);
  }
  assert.deepEqual(p.orientation.enum, ["portrait", "portrait_upside_down", "landscape_left", "landscape_right"]);
  // The bridge defaults orientation to portrait and the settle timeout itself.
  assert.equal(p.orientation.default, undefined);
  assert.equal(p.settle_timeout_ms.default, undefined);
  assert.equal(p.settle_timeout_ms.maximum, 20_000);
  assert.equal(p.page_size.maximum, 100);
  const description = tool(GAME_VIEW_TOOL).description!;
  for (const phrase of ["Open MCP <w>x<h>", "never deleted", "ONLY action that opens", "not_settled",
    "unsupported_editor_version", "insets", "safe_area", "logical points and pixels", "gate-free"])
    assert.match(description, new RegExp(phrase.replace(/[<>]/g, ".")), phrase);
});

test("composed capture schemas: native by default, padded not stretched, safe-area overlay", () => {
  for (const name of ["unity_senses_screenshot", "unity_senses_capture_inline"]) {
    const p = props(name);
    // A schema default would be filled by the server and turn every composed
    // capture back into a scaled 1280x720 frame.
    assert.equal(p.width.default, undefined, name);
    assert.equal(p.height.default, undefined, name);
    assert.deepEqual(p.size.enum, ["native"], name);
    assert.deepEqual(p.overlay.enum, ["safe_area"], name);
    assert.equal(p.size.default, undefined, name);
    assert.equal(p.overlay.default, undefined, name);
    assert.match(p.width.description, /never stretched/, name);
    assert.match(p.width.description, /scaled_from/, name);
    assert.match(tool(name).description!, /capturedAt/, name);
  }
  // visual_compare keeps its own fixed-size defaults (no composed view).
  assert.equal(props("unity_senses_visual_compare").width.default, 1280);
});

// ---------------------------------------------------------------------------
// Settle routing
// ---------------------------------------------------------------------------

function envelope(output: Record<string, unknown>, logs: unknown[] = []): CallToolResult {
  return { content: [{ type: "text", text: JSON.stringify({ mutation: { success: true, output, error: null }, logs }) }], isError: false };
}
function body(result: CallToolResult): Record<string, any> {
  const text = result.content.find((c) => c.type === "text");
  return text?.type === "text" ? JSON.parse(text.text) : {};
}

const PENDING = {
  status: "ok", action: "set_resolution", mode: "game", playing: true,
  screen: { width: 2422, height: 1483 }, entry: { label: "Open MCP 1080x1920", created: false },
  expected: [1080, 1920], settled: null,
  settle: { state: "pending", timeout_ms: 500, operation: { operation_id: "op-12345678", state: "running" } },
};
const FRESH = { status: "ok", action: "get", mode: "game", playing: true, screen: { width: 1080, height: 1920 } };

function fakeLive(opts: {
  start: Record<string, unknown>;
  snapshots?: Array<Record<string, unknown>>;
  onCancel?: Record<string, unknown>;
}) {
  const calls: Array<{ kind: string; args: Record<string, unknown> }> = [];
  let polls = 0;
  const live = {
    route: async (name: string, args: Record<string, unknown>) => {
      assert.equal(name, GAME_VIEW_TOOL);
      calls.push({ kind: "route", args });
      return envelope(args.action === "get" && calls.length > 1 ? FRESH : opts.start, [{ severity: "log", message: "x" }]);
    },
    playmodeOperation: async (args: { action: string; operation_id: string }) => {
      calls.push({ kind: args.action, args });
      if (args.action === "cancel") return opts.onCancel ?? { operation_id: args.operation_id, state: "cancelled", reason: "cancelled", result: { settled: false } };
      const list = opts.snapshots ?? [];
      return list[Math.min(polls++, list.length - 1)];
    },
  } as unknown as LiveClient;
  return { live, calls };
}

test("get and edit-mode set_* pass straight through", async () => {
  const get = fakeLive({ start: FRESH });
  const r1 = await routeGameView(get.live, { action: "get" });
  assert.equal(r1.kind, "passthrough");
  assert.equal(get.calls.length, 1);

  const edit = fakeLive({ start: { ...PENDING, playing: false, settled: true, settle: { state: "settled", source: "edit_mode_view" } } });
  const r2 = await routeGameView(edit.live, { action: "set_resolution", width: 1080, height: 1920 });
  assert.equal(r2.kind, "passthrough");
  assert.equal(edit.calls.filter((c) => c.kind !== "route").length, 0, "no settle poll in edit mode");
});

test("play-mode set_* polls the settle watch, then reports the settled view", async () => {
  const { live, calls } = fakeLive({
    start: PENDING,
    snapshots: [
      { operation_id: "op-12345678", state: "running", progress: { ticks: 1 }, result: null },
      { operation_id: "op-12345678", state: "succeeded", reason: "completed", progress: { ticks: 3 },
        result: { settled: true, expected: [1080, 1920], observed: { screen: [1080, 1920], render_texture: [1080, 1920], frame: 42 } } },
    ],
  });
  const r = await routeGameView(live, { action: "set_resolution", width: 1080, height: 1920 }, { pollIntervalMs: 1 });
  assert.equal(r.kind, "body");
  if (r.kind !== "body") return;
  assert.equal(r.isError, false);
  assert.equal(r.body.settled, true);
  assert.equal(r.body.action, "set_resolution");
  assert.deepEqual(r.body.screen, { width: 1080, height: 1920 }, "state re-read after settling");
  assert.deepEqual((r.body.entry as any).label, "Open MCP 1080x1920", "set_* fields kept");
  assert.equal((r.body.settle as any).frames, 3);
  assert.deepEqual((r.body.settle as any).observed.screen, [1080, 1920]);
  assert.ok(Array.isArray(r.body.logs));
  assert.equal(calls.filter((c) => c.kind === "cancel").length, 0);
  assert.deepEqual(calls.at(-1)?.args, { action: "get" });
});

test("a settle watch that ends unsettled answers not_settled with what the game reported", async () => {
  const { live } = fakeLive({
    start: PENDING,
    snapshots: [{ operation_id: "op-12345678", state: "succeeded", reason: "completed", progress: { ticks: 300 },
      result: { settled: false, observed: { screen: [2422, 1483], render_texture: [2422, 1483], frame: 9 }, reason: "A Game view tab that is not visible does not render." } }],
  });
  const r = await routeGameView(live, { action: "set_resolution", width: 1080, height: 1920 }, { pollIntervalMs: 1 });
  assert.equal(r.kind, "body");
  if (r.kind !== "body") return;
  assert.equal(r.isError, true);
  assert.equal((r.body.error as any).code, "not_settled");
  assert.match((r.body.error as any).message, /not visible/);
  assert.deepEqual((r.body.error as any).observed.screen, [2422, 1483]);
  assert.equal(r.body.settled, false);
  assert.deepEqual(r.body.expected, [1080, 1920]);
  assert.equal((r.body.settle as any).operation, undefined);
});

test("paused play mode is not_settled at once, without polling", async () => {
  const { live, calls } = fakeLive({ start: { ...PENDING, paused: true, settled: false, settle: { state: "paused", reason: "Play mode is paused." } } });
  const r = await routeGameView(live, { action: "set_device", device: "iOS Notch Device (1170x2532)" });
  assert.equal(r.kind, "body");
  if (r.kind !== "body") return;
  assert.equal((r.body.error as any).code, "not_settled");
  assert.equal((r.body.error as any).state, "paused");
  assert.equal(calls.length, 1);
});

test("a lost settle watch (reload) and a deadline cancel are both not_settled", async () => {
  const lost = fakeLive({ start: PENDING, snapshots: [{ error: { code: "operation_not_found", message: "gone" } }] });
  const r1 = await routeGameView(lost.live, { action: "set_resolution", width: 1080, height: 1920 }, { pollIntervalMs: 1 });
  assert.equal(r1.kind === "body" && (r1.body.error as any).state, "lost");

  const stuck = fakeLive({
    start: { ...PENDING, settle: { ...PENDING.settle, timeout_ms: 5 } },
    snapshots: [{ operation_id: "op-12345678", state: "running", progress: { ticks: 0 }, result: null }],
  });
  const r2 = await routeGameView(stuck.live, { action: "set_resolution", width: 1080, height: 1920 }, { pollIntervalMs: 1, slackMs: 1 });
  assert.equal(r2.kind, "body");
  if (r2.kind !== "body") return;
  assert.equal((r2.body.error as any).code, "not_settled");
  assert.equal((r2.body.error as any).state, "timeout", "the poll's own deadline cancel reads as a timeout");
  assert.equal(stuck.calls.filter((c) => c.kind === "cancel").length, 1, "the watch is cancelled once at the deadline");
});

test("bridge refusals pass through untouched", async () => {
  const { live } = fakeLive({ start: { error: { code: "unsupported_editor_version", message: "2022.3" } } });
  const r = await routeGameView(live, { action: "set_device", device: "x" });
  assert.equal(r.kind, "passthrough");
  if (r.kind === "passthrough") assert.equal(body(r.result).mutation.output.error.code, "unsupported_editor_version");
});
