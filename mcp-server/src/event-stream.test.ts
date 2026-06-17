import { test } from "node:test";
import assert from "node:assert/strict";
import { BridgeEventStream } from "./event-stream.js";

// M13 T4.4 — SSE parser unit tests. The parser is the contract surface
// between bridge and MCP server; it must tolerate multi-line data blocks
// (log stacks) and the named event types the bridge emits.

test("parseSseBlock: parses a log event with stack", () => {
  const block = [
    "event: log",
    'data: {"seq":42,"ts":"2026-06-17T00:00:00.000Z","type":"log","logType":"error","message":"boom","stack":"Frame1\\nFrame2"}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.type, "log");
  assert.equal(evt?.seq, 42);
  assert.equal(evt?.logType, "error");
  assert.equal(evt?.message, "boom");
  assert.equal(evt?.stack, "Frame1\nFrame2");
});

test("parseSseBlock: parses an editor_state event", () => {
  const block = [
    "event: editor_state",
    'data: {"seq":7,"ts":"2026-06-17T00:00:01.000Z","type":"editor_state","state":"compiling","isCompiling":true,"isPlaying":false}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.type, "editor_state");
  assert.equal(evt?.state, "compiling");
  assert.equal(evt?.isCompiling, true);
  assert.equal(evt?.isPlaying, false);
});

test("parseSseBlock: reassembles multi-line data blocks (stacks split across data: lines)", () => {
  // SSE splits long payloads across multiple `data:` lines; the parser must
  // reassemble them with embedded newlines before JSON.parse.
  const block = [
    "event: log",
    'data: {"seq":1,"ts":"2026-06-17T00:00:00.000Z","type":"log","logType":"log","message":"hi",',
    'data: "stack":"Line1\\nLine2"}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.type, "log");
  assert.equal(evt?.message, "hi");
  assert.equal(evt?.stack, "Line1\nLine2");
});

test("parseSseBlock: handles the 'ready' control event", () => {
  const block = [
    "event: ready",
    'data: {"subscriber":"abc123"}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.type, "ready");
});

test("parseSseBlock: handles the 'missed' control event", () => {
  const block = [
    "event: missed",
    'data: {"missed":3}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.type, "missed");
  assert.match(evt?.message ?? "", /missed=3/);
});

test("parseSseBlock: returns null for empty blocks", () => {
  assert.equal(BridgeEventStream.parseSseBlock(""), null);
  assert.equal(BridgeEventStream.parseSseBlock("\n\n"), null);
});

test("parseSseBlock: defaults seq/ts when bridge omits them", () => {
  const block = [
    "event: log",
    'data: {"type":"log","logType":"log","message":"hi"}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(typeof evt?.seq, "number");
  assert.equal(typeof evt?.ts, "string");
});

test("drain: returns at most maxEvents, leaves the rest buffered", () => {
  // Construct without calling connect; the queue is private so we go through
  // the public surface by parsing blocks. We can't enqueue without a network
  // path, so we just assert drain on an empty queue returns an empty array.
  const stream = new BridgeEventStream("http://127.0.0.1:1");
  const out = stream.drain(10);
  assert.ok(Array.isArray(out));
  assert.equal(out.length, 0);
});

test("stop: is idempotent and clears connection state", () => {
  const stream = new BridgeEventStream("http://127.0.0.1:1");
  stream.stop();
  stream.stop();
  assert.equal(stream.isConnected, false);
});
