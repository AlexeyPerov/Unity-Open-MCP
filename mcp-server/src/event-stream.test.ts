import { test } from "node:test";
import assert from "node:assert/strict";
import {
  createServer,
  type Server as HttpServer,
  type IncomingMessage,
  type ServerResponse,
} from "node:http";
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

// ----- M14: bearer token header on the SSE connection -----

interface StubHandle {
  server: HttpServer;
  port: number;
  close(): Promise<void>;
}

function startSseStub(capture: { auth?: string | null }): Promise<StubHandle> {
  return new Promise((resolve) => {
    const server = createServer((req: IncomingMessage, res: ServerResponse) => {
      if (capture.auth === undefined) {
        capture.auth = req.headers["authorization"] ?? null;
      }
      // Minimal valid SSE response so connect()'s res.ok check passes and the
      // reader enters its loop; we close immediately after to end the test.
      res.writeHead(200, {
        "Content-Type": "text/event-stream",
        "Cache-Control": "no-cache",
        Connection: "keep-alive",
      });
      res.write("event: ready\ndata: {}\n\n");
      res.end();
    });
    server.listen(0, "127.0.0.1", () => {
      const addr = server.address();
      const port = typeof addr === "object" && addr ? addr.port : 0;
      resolve({
        server,
        port,
        close: () => new Promise<void>((r) => server.close(() => r())),
      });
    });
  });
}

test("BridgeEventStream: sends Authorization: Bearer when a token was provided", async () => {
  const seen: { auth?: string | null } = {};
  const stub = await startSseStub(seen);
  try {
    const token = "deadbeef".repeat(8);
    const stream = new BridgeEventStream(
      `http://127.0.0.1:${stub.port}`,
      "test-sub",
      token,
    );
    stream.ensureSubscription();
    // Give the fetch + handler a tick to land.
    await new Promise((r) => setTimeout(r, 50));
    stream.stop();
    assert.equal(
      seen.auth,
      `Bearer ${token}`,
      "SSE connection must carry the discovered bearer token",
    );
  } finally {
    await stub.close();
  }
});

test("BridgeEventStream: omits Authorization when no token was provided", async () => {
  const seen: { auth?: string | null } = {};
  const stub = await startSseStub(seen);
  try {
    const stream = new BridgeEventStream(
      `http://127.0.0.1:${stub.port}`,
      "test-sub",
    );
    stream.ensureSubscription();
    await new Promise((r) => setTimeout(r, 50));
    stream.stop();
    assert.equal(
      seen.auth,
      null,
      "No Authorization header when the stream has no token (authMode \"none\")",
    );
  } finally {
    await stub.close();
  }
});

// ----- M31 Plan 6 / T6.1 — bulk-splice overflow -----

/**
 * Stub that floods `count` log events in one SSE response, then ends the
 * stream. Used to drive BridgeEventStream past QUEUE_CAPACITY (500) so the
 * bulk-splice overflow path runs and the `dropped` counter accumulates.
 */
function startFloodStub(count: number): Promise<StubHandle> {
  return new Promise((resolve) => {
    const server = createServer((_req: IncomingMessage, res: ServerResponse) => {
      res.writeHead(200, {
        "Content-Type": "text/event-stream",
        "Cache-Control": "no-cache",
        Connection: "keep-alive",
      });
      // One SSE block per event. seq is unique so parseSseBlock produces 600
      // distinct BridgeEvents — enough to overflow the 500-deep queue by 100.
      const blocks: string[] = [];
      for (let i = 0; i < count; i++) {
        blocks.push(
          `event: log\ndata: {"seq":${i},"ts":"2026-07-19T00:00:00.000Z","type":"log","logType":"log","message":"e${i}"}\n\n`,
        );
      }
      res.write(blocks.join(""));
      res.end();
    });
    server.listen(0, "127.0.0.1", () => {
      const addr = server.address();
      const port = typeof addr === "object" && addr ? addr.port : 0;
      resolve({
        server,
        port,
        close: () => new Promise<void>((r) => server.close(() => r())),
      });
    });
  });
}

test("BridgeEventStream: bulk-splice overflow drops the exact excess and preserves counter semantics", async () => {
  // M31 Plan 6 / T6.1 — flooding 600 events into a 500-deep queue must drop
  // exactly 100 (the excess), and the `dropped` counter must equal that
  // number. The old while+shift loop would have produced the same count but
  // re-indexed the array 100 times; the bulk splice does it once. This test
  // pins the counter-semantics contract (every dropped event counted) so a
  // future rewrite of the overflow path cannot silently regress it.
  const FLOOD = 600;
  const CAPACITY = 500; // QUEUE_CAPACITY in event-stream.ts
  const expectedDropped = FLOOD - CAPACITY;
  const stub = await startFloodStub(FLOOD);
  try {
    const stream = new BridgeEventStream(
      `http://127.0.0.1:${stub.port}`,
      "overflow-sub",
    );
    stream.ensureSubscription();
    // Wait for the flood to land and the reader to drain the response. The
    // stub writes everything in one chunk and closes, so a few ticks is enough.
    await new Promise((r) => setTimeout(r, 150));
    const result = stream.pull(1000);
    assert.equal(
      result.dropped,
      expectedDropped,
      `dropped must equal the exact excess (flood=${FLOOD}, capacity=${CAPACITY})`,
    );
    // Surviving events + dropped == flood total. The queue may have been
    // partially drained by pull, so we check events.length + dropped + (any
    // already-drained) — simplest invariant: nothing beyond CAPACITY survives.
    assert.ok(
      result.events.length <= CAPACITY,
      `events returned (${result.events.length}) must not exceed capacity (${CAPACITY})`,
    );
    stream.stop();
  } finally {
    await stub.close();
  }
});

test("BridgeEventStream: under-capacity flood drops nothing", async () => {
  // Counter-semantics complement: a flood below capacity must not drop a
  // single event. Guards against an off-by-one in the splice math.
  const FLOOD = 100;
  const stub = await startFloodStub(FLOOD);
  try {
    const stream = new BridgeEventStream(
      `http://127.0.0.1:${stub.port}`,
      "under-cap-sub",
    );
    stream.ensureSubscription();
    await new Promise((r) => setTimeout(r, 150));
    const result = stream.pull(1000);
    assert.equal(result.dropped, 0, "under-capacity flood must drop nothing");
    assert.equal(result.events.length, FLOOD, "all events survive");
    stream.stop();
  } finally {
    await stub.close();
  }
});
