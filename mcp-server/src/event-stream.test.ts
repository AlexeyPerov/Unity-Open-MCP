import { test } from "node:test";
import assert from "node:assert/strict";
import {
  createServer,
  type Server as HttpServer,
  type IncomingMessage,
  type ServerResponse,
} from "node:http";
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { BridgeEventStream } from "./event-stream.js";
import { projectHash } from "./instance-discovery.js";
import { bridgeBaseUrl } from "./constants.js";

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

// ----- feedback #8 (2026-08-10): per-subscriber cursor (no backlog replay) -----

/**
 * Stub that emits `before` events, waits for the first pull to establish a
 * subscriber cursor, then emits `after` events and ends. Used to prove a named
 * subscriber's first pull returns empty (no backlog) and its second pull sees
 * only the post-subscription events.
 */
function startBacklogStub(opts: {
  before: number;
  after: number;
  firstPull: Promise<void>;
}): Promise<StubHandle> {
  return new Promise((resolve) => {
    const server = createServer((_req: IncomingMessage, res: ServerResponse) => {
      res.writeHead(200, {
        "Content-Type": "text/event-stream",
        "Cache-Control": "no-cache",
        Connection: "keep-alive",
      });
      const writeBatch = (start: number, count: number) => {
        const blocks: string[] = [];
        for (let i = 0; i < count; i++) {
          blocks.push(
            `event: log\ndata: {"seq":${start + i},"ts":"2026-08-10T00:00:00.000Z","type":"log","logType":"log","message":"e${start + i}"}\n\n`,
          );
        }
        res.write(blocks.join(""));
      };
      writeBatch(0, opts.before);
      // Once the test has pulled (cursor pinned at the tail), emit the after-batch.
      opts.firstPull.then(() => {
        writeBatch(opts.before, opts.after);
        res.end();
      });
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

test("BridgeEventStream: a NEW named subscriber's first pull returns empty (no backlog replay)", async () => {
  // feedback #8 — before the fix, every caller drained one shared FIFO queue,
  // so a fresh subscriber name replayed hours of accumulated history. The fix
  // pins a new named subscriber's cursor at the current queue tail on its
  // first pull, so it returns started:true + empty events.
  let resolveFirstPull: () => void;
  const firstPull = new Promise<void>((r) => {
    resolveFirstPull = r;
  });
  const stub = await startBacklogStub({ before: 5, after: 3, firstPull });
  try {
    const stream = new BridgeEventStream(
      `http://127.0.0.1:${stub.port}`,
      "backlog-stream",
    );
    stream.ensureSubscription();
    // Wait for the before-batch (5 events) to land.
    await new Promise((r) => setTimeout(r, 150));

    // First pull by a NEW named subscriber: cursor pinned at tail → empty.
    const first = stream.pull(1000, "fresh-agent");
    assert.equal(first.started, true, "first pull of a new subscriber starts it");
    assert.equal(first.events.length, 0, "no backlog replay for a new subscriber");

    // Let the stub emit the after-batch, then the second pull sees only those.
    resolveFirstPull!();
    await new Promise((r) => setTimeout(r, 150));
    const second = stream.pull(1000, "fresh-agent");
    assert.equal(second.started, false, "returning subscriber does not re-start");
    assert.equal(second.events.length, 3, "only post-subscription events delivered");
    stream.stop();
  } finally {
    await stub.close();
  }
});

test("BridgeEventStream: two named subscribers get independent cursors", async () => {
  // feedback #8 — each named subscriber must have its own cursor. Drain events
  // for sub-A; sub-B subscribing later must still see the events emitted after
  // ITS own first pull, not the events A already consumed.
  const FLOOD = 3;
  const stub = await startFloodStub(FLOOD);
  try {
    const stream = new BridgeEventStream(
      `http://127.0.0.1:${stub.port}`,
      "two-sub-stream",
    );
    stream.ensureSubscription();
    await new Promise((r) => setTimeout(r, 150));

    // sub-A drains the whole backlog (legacy-style, but via named cursor the
    // first pull pins at the tail → empty too). The point: after sub-A pulls,
    // sub-B's first pull is ALSO empty (independent cursor, not "A drained it").
    const a = stream.pull(1000, "sub-a");
    const b = stream.pull(1000, "sub-b");
    assert.equal(a.events.length, 0, "sub-a first pull: no backlog");
    assert.equal(b.events.length, 0, "sub-b first pull: no backlog (independent cursor)");
    stream.stop();
  } finally {
    await stub.close();
  }
});

// ----- cursor fix-up for the legacy drain() path (second front-removal site) -----

test("BridgeEventStream: a legacy pull() between a named subscriber's pulls does not skip its events", async () => {
  // The feedback-#8 named-subscriber cursor is an index into the shared queue.
  // Both enqueue()'s overflow eviction AND the legacy drain() remove items
  // from the FRONT of the queue, shifting every index down. The eviction path
  // decrements cursors to compensate; drain() did not, so a legacy pull() (no
  // subscriber) interleaved between a named subscriber's pulls left its cursor
  // pointing past the new tail and silently dropped the events it was owed.
  //
  // Scenario: queue=[e0,e1,e2]; sub "x" pins cursor at the tail (3) and pulls
  // empty. queue grows to [e0,e1,e2,e3,e4]. A legacy pull(2) drains e0,e1
  // → queue=[e2,e3,e4]. sub "x" must still see exactly [e3,e4] (its owed
  // tail), not skip them because its cursor stayed at 3 while the queue shrank.
  let resolveFirstPull: () => void;
  const firstPull = new Promise<void>((r) => {
    resolveFirstPull = r;
  });
  const stub = await startBacklogStub({ before: 3, after: 2, firstPull });
  try {
    const stream = new BridgeEventStream(
      `http://127.0.0.1:${stub.port}`,
      "legacy-drain-stream",
    );
    stream.ensureSubscription();
    // Wait for the before-batch (e0,e1,e2) to land.
    await new Promise((r) => setTimeout(r, 150));

    // sub "x" subscribes: cursor pinned at the tail (3) → first pull empty.
    const first = stream.pull(1000, "x");
    assert.equal(first.events.length, 0, "new subscriber: no backlog");

    // Release the after-batch (e3,e4); queue grows to 5 entries.
    resolveFirstPull!();
    await new Promise((r) => setTimeout(r, 150));

    // A legacy pull() with NO subscriber drains 2 from the FRONT (e0,e1).
    const legacy = stream.pull(2);
    assert.equal(legacy.events.length, 2, "legacy pull drains 2 from the front");

    // sub "x" must still see exactly [e3,e4] — the events after its
    // subscription. Without the drain() cursor fix-up its cursor stayed at 3
    // while the queue shrank to [e2,e3,e4], so slice(3) returned [] and e3,e4
    // were silently lost.
    const second = stream.pull(1000, "x");
    assert.equal(
      second.events.length,
      2,
      "named subscriber sees its owed events after a legacy drain",
    );
    assert.deepEqual(
      second.events.map((e) => e.message),
      ["e3", "e4"],
      "no events skipped or duplicated after the legacy front-drain",
    );
    stream.stop();
  } finally {
    await stub.close();
  }
});

// ----- fd-leak review (2026-08-19): 401 body cancel, reconnect backoff +
// unref, and reconnect-time port/token refresh from the instance lock -----
//
// The bridge mints a fresh bearer token on every Acquire (domain reload /
// editor restart). Before these fixes the reader kept its construction-time
// credentials forever: after the first reload every reconnect got a 401, the
// unconsumed Response body kept its undici socket out of the pool until GC,
// and the flat 2s retry cadence ran for the whole process lifetime.

test("BridgeEventStream: cancels the 401 response body and schedules a reconnect", async () => {
  // Dropping a non-OK Response without consuming/cancelling its body leaves
  // the socket pinned outside undici's keep-alive pool until GC. One 401 per
  // 2s retry accumulated abandoned sockets for the whole session.
  let cancelled = 0;
  let fetchCalls = 0;
  const origFetch = globalThis.fetch;
  globalThis.fetch = (async () => {
    fetchCalls++;
    return {
      ok: false,
      status: 401,
      body: {
        cancel: async () => {
          cancelled++;
        },
      },
    } as unknown as Response;
  }) as typeof fetch;
  const stream = new BridgeEventStream("http://127.0.0.1:1", "unauth-sub");
  try {
    stream.ensureSubscription();
    // Let the stubbed fetch promise chain (then → throw → catch) land.
    await new Promise((r) => setTimeout(r, 50));
    assert.equal(fetchCalls, 1);
    assert.equal(cancelled, 1, "the 401 body must be cancelled so undici frees the socket");
    const pull = stream.pull(1);
    assert.equal(pull.connected, false);
    assert.match(pull.lastError ?? "", /HTTP 401/, "failure surfaces via lastError");
  } finally {
    // stop() clears the pending (real) 2s reconnect timer.
    stream.stop();
    globalThis.fetch = origFetch;
  }
});

test("BridgeEventStream: reconnect delay backs off 2s→30s cap, unrefs the timer, and resets after success", async () => {
  // Manual time control: capture setTimeout callbacks instead of waiting the
  // full 2+4+8+16+30+30s sequence, firing them by hand. fetch is stubbed so
  // no real network runs under the patched globals.
  interface FakeTimer {
    delay: number;
    unref: boolean;
    fire: () => void;
  }
  const timers: FakeTimer[] = [];
  const origSetTimeout = globalThis.setTimeout;
  const origClearTimeout = globalThis.clearTimeout;
  const origFetch = globalThis.fetch;
  let fail = true;
  globalThis.fetch = (async () => {
    if (fail) {
      return {
        ok: false,
        status: 503,
        body: { cancel: async () => {} },
      } as unknown as Response;
    }
    // An immediately-closed stream: connect succeeds, pump reads done, and
    // the pump's finally schedules the next reconnect from the RESET delay.
    return {
      ok: true,
      status: 200,
      body: new ReadableStream<Uint8Array>({
        start(controller) {
          controller.close();
        },
      }),
    } as unknown as Response;
  }) as typeof fetch;
  globalThis.setTimeout = ((fn: (...args: unknown[]) => void, delay?: number) => {
    const rec: FakeTimer = {
      delay: delay ?? 0,
      unref: false,
      fire: () => fn(),
    };
    timers.push(rec);
    return { unref: () => { rec.unref = true; } } as unknown as NodeJS.Timeout;
  }) as typeof setTimeout;
  globalThis.clearTimeout = (() => {}) as typeof clearTimeout;
  const tick = (): Promise<void> =>
    new Promise((r) => origSetTimeout(r, 20));
  const delays = (): number[] => timers.map((t) => t.delay);
  const stream = new BridgeEventStream("http://127.0.0.1:1", "backoff-sub");
  try {
    stream.ensureSubscription();
    await tick(); // connect #1 fails → schedule @2s
    assert.deepEqual(delays(), [2000]);

    timers[0].fire(); await tick(); // @4s
    timers[1].fire(); await tick(); // @8s
    timers[2].fire(); await tick(); // @16s
    timers[3].fire(); await tick(); // 32s → capped at 30s
    timers[4].fire(); await tick(); // still the cap
    assert.deepEqual(
      delays(),
      [2000, 4000, 8000, 16000, 30000, 30000],
      "delay doubles per consecutive failure, capped at 30s",
    );
    assert.ok(
      timers.every((t) => t.unref),
      "every reconnect timer must be unref'd (no orphaned event loop after stdio close)",
    );

    // Success resets the backoff: the post-stream reconnect is scheduled at
    // the 2s base, not the capped 30s.
    fail = false;
    timers[5].fire(); await tick();
    assert.equal(
      timers[timers.length - 1].delay,
      2000,
      "delay resets to the base after a successful connect",
    );
  } finally {
    stream.stop();
    globalThis.fetch = origFetch;
    globalThis.setTimeout = origSetTimeout;
    globalThis.clearTimeout = origClearTimeout;
  }
});

/** 401-always stub — simulates a bridge rejecting the (stale) bearer. */
function start401Stub(capture: { auth?: string | null }): Promise<StubHandle> {
  return new Promise((resolve) => {
    const server = createServer((req: IncomingMessage, res: ServerResponse) => {
      if (capture.auth === undefined) {
        capture.auth = req.headers["authorization"] ?? null;
      }
      res.writeHead(401, { "Content-Type": "application/json" });
      res.end('{"error":"unauthorized"}');
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

interface HomeSandbox {
  dir: string;
  prevHome: string | undefined;
  prevUserProfile: string | undefined;
}

/** Redirect HOME/USERPROFILE at a temp dir so planted locks are isolated. */
function makeHomeSandbox(): HomeSandbox {
  const dir = mkdtempSync(join(tmpdir(), "uomcp-sse-"));
  return { dir, prevHome: process.env.HOME, prevUserProfile: process.env.USERPROFILE };
}

function restoreHomeSandbox(s: HomeSandbox): void {
  if (s.prevHome === undefined) delete process.env.HOME;
  else process.env.HOME = s.prevHome;
  if (s.prevUserProfile === undefined) delete process.env.USERPROFILE;
  else process.env.USERPROFILE = s.prevUserProfile;
  try {
    rmSync(s.dir, { recursive: true, force: true });
  } catch {
    // best-effort
  }
}

/** Plant a live-PID instance lock for `projectPath` in the sandbox HOME. */
function plantHomeLock(
  dir: string,
  projectPath: string,
  port: number,
  authToken: string,
): void {
  process.env.HOME = dir;
  process.env.USERPROFILE = dir;
  const hash = projectHash(projectPath);
  const instDir = join(dir, ".unity-open-mcp", "instances");
  if (!existsSync(instDir)) mkdirSync(instDir, { recursive: true });
  const payload = {
    pid: process.pid, // alive — the test runner itself
    port,
    authToken,
    projectPath,
    projectHash: hash,
    startedAt: "2026-08-19T00:00:00.000Z",
    updatedAt: "2026-08-19T00:00:00.000Z",
    heartbeatAt: "2026-08-19T00:00:00.000Z",
    state: "idle",
    isPlaying: false,
    isCompiling: false,
    bridgeVersion: "1.0.0",
    unityVersion: "6000.0.0f1",
  };
  writeFileSync(join(instDir, `${hash}.json`), JSON.stringify(payload));
}

test("BridgeEventStream: reconnect re-reads the instance lock and self-heals after the bridge token rotates", async () => {
  // The exact 1.0.0 leak scenario: first connect succeeds against the lock's
  // endpoint, the stub starts rejecting (Unity "reloaded" — new port+token in
  // the lock). The next reconnect must re-resolve port+token instead of
  // hammering the old endpoint with the stale bearer forever.
  const oldSeen: { auth?: string | null } = {};
  const newSeen: { auth?: string | null } = {};
  const oldStub = await start401Stub(oldSeen);
  const newStub = await startSseStub(newSeen);
  const sandbox = makeHomeSandbox();
  const PROJECT = "/test/SseTokenRotation";
  const stream = new BridgeEventStream(
    bridgeBaseUrl(oldStub.port),
    "rotate-sub",
    "token-1",
    PROJECT,
  );
  try {
    plantHomeLock(sandbox.dir, PROJECT, oldStub.port, "token-1");
    stream.ensureSubscription();
    await new Promise((r) => setTimeout(r, 100));
    assert.equal(oldSeen.auth, "Bearer token-1");
    assert.match(stream.pull(1).lastError ?? "", /HTTP 401/);

    // Unity restart / domain reload: fresh port + fresh token in the lock.
    plantHomeLock(sandbox.dir, PROJECT, newStub.port, "token-2");
    // Wait out the (real) base reconnect delay, then the reconnect must have
    // landed on the new stub with the refreshed bearer.
    await new Promise((r) => setTimeout(r, 2200));
    assert.equal(
      newSeen.auth,
      "Bearer token-2",
      "reconnect must carry the token re-read from the instance lock",
    );
  } finally {
    stream.stop();
    restoreHomeSandbox(sandbox);
    await oldStub.close();
    await newStub.close();
  }
});
