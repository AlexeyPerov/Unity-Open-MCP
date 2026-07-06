// M13 T4.5 — LiveClient × dismiss-loop integration.
//
// Drives a real local HTTP "bridge" stub through the compile-wait path and
// verifies the launch-errors dismiss loop is:
//   1. started (with an abort signal) whenever waitForCompile is entered,
//   2. aborted the moment the bridge reports idle (compiling=false,
//      connected=true),
//   3. skipped entirely when UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1.
//
// The OS-clicking loop itself is stubbed via the protected `runDismissLoop`
// hook so no PowerShell / osascript / xdotool is invoked.
//
// Note: `unity_open_mcp_ping` is NOT used here — its handler fetches /ping
// directly and never enters waitForCompile. We route a generic tool so the
// call goes handleToolCall → ensureReady → waitForCompile on a 503.

import { test } from "node:test";
import assert from "node:assert/strict";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import {
  createServer,
  type Server as HttpServer,
  type IncomingMessage,
  type ServerResponse,
} from "node:http";
import { mkdtempSync, mkdirSync, writeFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PingCache } from "./ping-cache.js";
import { LiveClient, shouldRetryPostAfterFailure } from "./live-client.js";
import { projectHash } from "./instance-discovery.js";
import { setUnityProcessScannerForTest } from "./running-unity.js";
import type { PollAndDismissOptions } from "./dialog-dismiss.js";

interface CapturedDismiss {
  opts: PollAndDismissOptions;
  aborted: boolean;
}

/**
 * LiveClient subclass that records every dismiss-loop invocation and
 * resolves the loop's promise when (if) the AbortController fires. The real
 * production loop polls the OS; here we only assert it was started and got
 * aborted at the right time.
 */
class SpyLiveClient extends LiveClient {
  readonly dismissCalls: CapturedDismiss[] = [];

  protected runDismissLoop(opts: PollAndDismissOptions): Promise<void> {
    const captured: CapturedDismiss = { opts, aborted: false };
    this.dismissCalls.push(captured);
    return new Promise<void>((resolve) => {
      if (opts.abortSignal?.aborted) {
        captured.aborted = true;
        resolve();
        return;
      }
      opts.abortSignal?.addEventListener(
        "abort",
        () => {
          captured.aborted = true;
          resolve();
        },
        { once: true },
      );
    });
  }
}

interface BridgeStub {
  server: HttpServer;
  port: number;
  close(): Promise<void>;
  /** Replace the response handler on the fly to flip compiling → idle. */
  setHandler(fn: (req: IncomingMessage, res: ServerResponse) => void): void;
}

function startBridgeStub(
  initialHandler: (req: IncomingMessage, res: ServerResponse) => void,
): Promise<BridgeStub> {
  return new Promise((resolve) => {
    let handler = initialHandler;
    const server = createServer((req, res) => handler(req, res));
    server.listen(0, "127.0.0.1", () => {
      const addr = server.address();
      const port = typeof addr === "object" && addr ? addr.port : 0;
      resolve({
        server,
        port,
        close: () => new Promise<void>((r) => server.close(() => r())),
        setHandler: (fn) => {
          handler = fn;
        },
      });
    });
  });
}

/** 503 response — bridge listener up, editor still compiling. */
function compilingHandler(_req: IncomingMessage, res: ServerResponse): void {
  res.writeHead(503, { "Content-Type": "application/json" });
  res.end(JSON.stringify({ connected: false, compiling: true }));
}

/**
 * 200 handler that serves a proper idle PingResponse on /ping (so the
 * compile-wait exit condition `!compiling && connected` is met) and a
 * generic success body on /tools/... (so the post-compile tool POST
 * completes). Path-aware because waitForCompile polls /ping while the
 * subsequent postTool POSTs to /tools/{name}.
 */
function idleOkHandler(req: IncomingMessage, res: ServerResponse): void {
  res.writeHead(200, { "Content-Type": "application/json" });
  if (req.url === "/ping") {
    res.end(
      JSON.stringify({
        connected: true,
        projectPath: "/proj",
        unityVersion: "6000.0.0f1",
        bridgeVersion: "0.1.0",
        mode: "live",
        compiling: false,
        isPlaying: false,
      }),
    );
    return;
  }
  // /tools/{name} — a DIRECT_RESPONSE_TOOLS-style success body.
  res.end(JSON.stringify({ ok: true }));
}

test("LiveClient: starts the dismiss loop during compile-wait, aborts it on idle", async () => {
  const bridge = await startBridgeStub(compilingHandler);
  try {
    const client = new SpyLiveClient(bridge.port, new PingCache());

    // Flip the bridge to idle shortly AFTER the compile poll enters. The
    // loop must start on a 503 and only stop when idle arrives — scheduling
    // the flip keeps the test from racing the first poll tick.
    const flip = setTimeout(() => bridge.setHandler(idleOkHandler), 150);

    // A generic (non-ping) tool routes through ensureReady → waitForCompile.
    await client.route("unity_open_mcp_validate_edit", { paths: ["Assets"] });
    clearTimeout(flip);

    assert.equal(
      client.dismissCalls.length,
      1,
      "dismiss loop started once during the compile wait",
    );
    const call = client.dismissCalls[0];
    assert.ok(call.opts.abortSignal, "dismiss loop received an abort signal");
    assert.equal(call.aborted, true, "dismiss loop aborted when compile resolved");
  } finally {
    await bridge.close();
  }
});

test("LiveClient: dismiss loop is skipped when opted out via env", async () => {
  const prev = process.env.UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS;
  process.env.UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS = "1";
  try {
    const bridge = await startBridgeStub(compilingHandler);
    try {
      const client = new SpyLiveClient(bridge.port, new PingCache());
      const flip = setTimeout(() => bridge.setHandler(idleOkHandler), 150);

      await client.route("unity_open_mcp_validate_edit", { paths: ["Assets"] });
      clearTimeout(flip);

      assert.equal(
        client.dismissCalls.length,
        0,
        "opt-out must preserve pre-feature behavior (no dismiss loop started)",
      );
    } finally {
      await bridge.close();
    }
  } finally {
    if (prev === undefined) delete process.env.UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS;
    else process.env.UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS = prev;
  }
});

// ----- M14: bearer token header -----

/**
 * Handler that records the Authorization header of the first /ping request it
 * sees, then responds with an idle 200. Used to assert LiveClient attaches the
 * bearer token discovered from the instance lock.
 */
function headerCapturingHandler(
  seen: { auth?: string | null },
): (req: IncomingMessage, res: ServerResponse) => void {
  return (req, res) => {
    if (req.url === "/ping" && seen.auth === undefined) {
      seen.auth = req.headers["authorization"] ?? null;
    }
    res.writeHead(200, { "Content-Type": "application/json" });
    if (req.url === "/ping") {
      res.end(
        JSON.stringify({
          connected: true,
          projectPath: "/proj",
          unityVersion: "6000.0.0f1",
          bridgeVersion: "0.1.0",
          mode: "live",
          compiling: false,
          isPlaying: false,
        }),
      );
      return;
    }
    res.end(JSON.stringify({ ok: true }));
  };
}

test("LiveClient: sends Authorization: Bearer <token> when a token was provided", async () => {
  const seen: { auth?: string | null } = {};
  const bridge = await startBridgeStub(headerCapturingHandler(seen));
  try {
    const token = "deadbeef".repeat(8);
    const client = new LiveClient(bridge.port, new PingCache(), token);
    await client.isLiveAvailable();
    assert.equal(
      seen.auth,
      `Bearer ${token}`,
      "Authorization header must carry the discovered token",
    );
  } finally {
    await bridge.close();
  }
});

test("LiveClient: omits Authorization header when no token was provided", async () => {
  const seen: { auth?: string | null } = {};
  const bridge = await startBridgeStub(headerCapturingHandler(seen));
  try {
    const client = new LiveClient(bridge.port, new PingCache());
    await client.isLiveAvailable();
    assert.equal(
      seen.auth,
      null,
      "No Authorization header when the client has no token (authMode \"none\")",
    );
  } finally {
    await bridge.close();
  }
});

// ----- Dead-bridge fail-fast -----
//
// When the bridge assembly fails to recompile, /ping ECONNREFUSED forever.
// Rather than hang on the 120s compile-wait, LiveClient must read the instance
// lock, detect the stale-heartbeat + live-PID signature, and return an
// immediate bridge_compile_failed error pointing at read_compile_errors.

interface Sandbox {
  dir: string;
}

function makeSandbox(): Sandbox {
  const dir = mkdtempSync(join(tmpdir(), "lc-"));
  return { dir };
}

function disposeSandbox(s: Sandbox): void {
  try {
    rmSync(s.dir, { recursive: true, force: true });
  } catch {
    // best-effort
  }
}

/** Plant a lock for `projectPath` in the sandbox with the given heartbeat age
 *  and PID. Points HOME/USERPROFILE at the sandbox so the module reads it. */
function plantLock(
  s: Sandbox,
  projectPath: string,
  pid: number,
  heartbeatAgeMs: number,
  state: string = "reloading",
  port: number = 22028,
  authToken: string = "deadbeef",
): void {
  process.env.HOME = s.dir;
  process.env.USERPROFILE = s.dir;
  const hash = projectHash(projectPath);
  const dir = join(s.dir, ".unity-open-mcp", "instances");
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
  const path = join(dir, `${hash}.json`);
  const heartbeatAt = new Date(Date.now() - heartbeatAgeMs).toISOString();
  const payload = {
    pid,
    port,
    authToken,
    projectPath,
    projectHash: hash,
    startedAt: heartbeatAt,
    updatedAt: heartbeatAt,
    heartbeatAt,
    state,
    isPlaying: false,
    isCompiling: false,
    bridgeVersion: "0.1.0",
    unityVersion: "6000.0.0f1",
  };
  writeFileSync(path, JSON.stringify(payload));
}

const DEAD_BRIDGE_PROJECT = "/test/DeadBridgeGame";

test("LiveClient: fail-fast bridge_compile_failed when heartbeat is stale + PID alive", async () => {
  // No bridge stub at all — /ping must ECONNREFUSED. The lock shows our own
  // PID (alive) with a heartbeat far past the stale threshold → dead_bridge.
  const s = makeSandbox();
  try {
    plantLock(s, DEAD_BRIDGE_PROJECT, process.pid, 60_000, "reloading");
    const client = new LiveClient(1, new PingCache(), undefined, DEAD_BRIDGE_PROJECT);

    const result = await client.route("unity_open_mcp_validate_edit", {
      paths: ["Assets"],
    });

    assert.equal(result.isError, true);
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.error.code, "bridge_compile_failed");
    assert.ok(
      body.error.message.includes("read_compile_errors"),
      "error must point the agent at read_compile_errors",
    );
  } finally {
    disposeSandbox(s);
  }
});

test("LiveClient: does NOT fail-fast bridge_compile_failed when a fresh test-pending file exists", async () => {
  // specs/feedback.md — a Unity test run can freeze the heartbeat writer long
  // enough to trip the stale-heartbeat threshold, flipping the classification
  // to dead_bridge even though the run will recover. When a fresh
  // test-pending-*.json signal exists, deadBridgeResult() must return null so
  // the client keeps waiting instead of poisoning the session. With no bridge
  // stub here, the wait loop eventually exhausts and returns bridge_offline —
  // the point is that it does NOT return the immediate bridge_compile_failed.
  const s = makeSandbox();
  try {
    plantLock(s, DEAD_BRIDGE_PROJECT, process.pid, 60_000, "reloading");
    // Plant a fresh test-pending file alongside the lock (same status dir).
    const statusDir = join(s.dir, ".unity-open-mcp");
    if (!existsSync(statusDir)) mkdirSync(statusDir, { recursive: true });
    writeFileSync(join(statusDir, "test-pending-run-abc.json"), "{}");

    const client = new LiveClient(1, new PingCache(), undefined, DEAD_BRIDGE_PROJECT);
    const result = await client.route("unity_open_mcp_validate_edit", {
      paths: ["Assets"],
    });

    assert.equal(result.isError, true);
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.notEqual(body.error.code, "bridge_compile_failed");
    assert.equal(body.error.code, "bridge_offline");
  } finally {
    disposeSandbox(s);
  }
});

test("LiveClient: returns bridge_offline (not bridge_compile_failed) when PID is dead", () => {
  // Dead PID → classifyInstance returns "gone", not "dead_bridge". The client
  // must fall back to the original bridge_offline behavior, NOT fail-fast.
  const s = makeSandbox();
  try {
    plantLock(s, DEAD_BRIDGE_PROJECT, 999_999_999, 60_000);
    const client = new LiveClient(1, new PingCache(), undefined, DEAD_BRIDGE_PROJECT);

    // ensureReady is private; route through a tool call. Because /ping throws
    // and the lock says "gone", we expect the generic offline error.
    return client.route("unity_open_mcp_validate_edit", { paths: ["Assets"] }).then(
      (result) => {
        assert.equal(result.isError, true);
        const body = JSON.parse((result.content[0] as { text: string }).text);
        assert.equal(body.error.code, "bridge_offline");
      },
    );
  } finally {
    disposeSandbox(s);
  }
});

// ----- M27 Plan 1 — cold Safe Mode (no lock + live Unity process) -----
//
// When Unity launches straight into Safe Mode, no instance lock is written
// (the bridge's [InitializeOnLoad] never ran). classifyInstance(null) →
// "gone", which pre-fix folded into the generic bridge_offline. The
// coldSafeModeResult scan reuses the bridge_compile_failed shape so the agent
// gets the read_compile_errors recovery hint instead of guessing.

const COLD_SAFE_PROJECT = "/test/ColdSafeGame";

test("LiveClient: tool call with no lock + live Unity process returns bridge_compile_failed (cold Safe Mode)", async () => {
  // No lock planted → classifyInstance returns "gone". The scanner fake
  // reports a Unity process for this project → coldSafeModeResult kicks in.
  const s = makeSandbox();
  const restore = setUnityProcessScannerForTest({
    scan() {
      return [{ pid: 7777, projectPath: COLD_SAFE_PROJECT }];
    },
  });
  try {
    // No bridge stub at all — /ping ECONNREFUSED.
    const client = new LiveClient(1, new PingCache(), undefined, COLD_SAFE_PROJECT);
    const result = await client.route("unity_open_mcp_validate_edit", {
      paths: ["Assets"],
    });
    assert.equal(result.isError, true);
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.error.code, "bridge_compile_failed");
    assert.ok(
      body.error.message.includes("read_compile_errors"),
      "cold Safe Mode error points at read_compile_errors",
    );
    assert.ok(
      body.error.message.includes("Safe Mode"),
      "cold Safe Mode error names Safe Mode explicitly",
    );
  } finally {
    restore();
    disposeSandbox(s);
  }
});

test("LiveClient: ping with no lock + live Unity process returns bridge_compile_failed (cold Safe Mode)", async () => {
  // ping is the first probe many agents use. It must surface the same
  // recovery hint as a tool call, not a generic bridge_offline.
  const s = makeSandbox();
  const restore = setUnityProcessScannerForTest({
    scan() {
      return [{ pid: 8888, projectPath: COLD_SAFE_PROJECT }];
    },
  });
  try {
    const client = new LiveClient(1, new PingCache(), undefined, COLD_SAFE_PROJECT);
    const result = await client.route("unity_open_mcp_ping", {});
    assert.equal(result.isError, true);
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.error.code, "bridge_compile_failed");
  } finally {
    restore();
    disposeSandbox(s);
  }
});

test("LiveClient: no lock + no Unity process still returns bridge_offline (genuine offline)", async () => {
  // Regression guard: the cold-Safe-Mode scan must not flip a genuine "Unity
  // is not running" state into bridge_compile_failed.
  const s = makeSandbox();
  const restore = setUnityProcessScannerForTest({
    scan() {
      return [];
    },
  });
  try {
    const client = new LiveClient(1, new PingCache(), undefined, COLD_SAFE_PROJECT);
    const result = await client.route("unity_open_mcp_validate_edit", {
      paths: ["Assets"],
    });
    assert.equal(result.isError, true);
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.error.code, "bridge_offline");
  } finally {
    restore();
    disposeSandbox(s);
  }
});

test("LiveClient: mid-session dead bridge (lock + stale heartbeat) unchanged by cold Safe Mode scan", async () => {
  // Regression guard for the mid-session dead-bridge path. A live PID + stale
  // heartbeat classifies as "dead_bridge" (not "gone"), so coldSafeModeResult
  // returns null and the existing deadBridgeResult fast-path still fires.
  const s = makeSandbox();
  const restore = setUnityProcessScannerForTest({
    scan() {
      return [{ pid: 4321, projectPath: DEAD_BRIDGE_PROJECT }];
    },
  });
  try {
    plantLock(s, DEAD_BRIDGE_PROJECT, process.pid, 60_000, "reloading");
    const client = new LiveClient(1, new PingCache(), undefined, DEAD_BRIDGE_PROJECT);
    const result = await client.route("unity_open_mcp_validate_edit", {
      paths: ["Assets"],
    });
    assert.equal(result.isError, true);
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.error.code, "bridge_compile_failed");
  } finally {
    restore();
    disposeSandbox(s);
  }
});

test("LiveClient: no projectPath preserves original offline behavior on unreachable bridge", async () => {
  // Without projectPath threaded in, the dead-bridge check is skipped —
  // existing callers that don't pass the arg keep working. T-fix-2 still runs
  // the bounded retry loop, but with no lock to read it classifies "gone" each
  // attempt and eventually returns bridge_offline.
  const client = new LiveClient(1, new PingCache());
  const result = await client.route("unity_open_mcp_validate_edit", {
    paths: ["Assets"],
  });
  assert.equal(result.isError, true);
  const body = JSON.parse((result.content[0] as { text: string }).text);
  assert.equal(body.error.code, "bridge_offline");
});

// ----- T-fix-2: transient-connection recovery (M20 Plan 4-5) -----
//
// Two recovery paths were added for the domain-reload listener gap:
//   1. lock classification "reloading" → waitForCompile then retry (the
//      listener socket is torn down for the reload duration)
//   2. transient refusal with no reload signal → bounded backoff retry
// Both must RECOVER instead of returning bridge_offline on a blip. The dead-
// bridge fast-path above must still fail fast.

/**
 * Handler that refuses every connection (ECONNREFUSED-equivalent: no listener).
 * Used to simulate the reload window where the bridge socket is down.
 */
// (no handler — port with nothing listening produces ECONNREFUSED on connect)

test("T-fix-2: transient refusal during a reloading lock window enters the wait path", async () => {
  // Plant a lock with state="reloading" + live PID (our own pid) + fresh
  // heartbeat → classifyInstance returns "reloading". The recovery helper must
  // enter waitForCompile (NOT return bridge_offline instantly). We point the
  // client at a port that never comes up and race against a guard: if the
  // recovery path was entered, the call is still in waitForCompile at the
  // guard cutoff → pass. If it returned instantly with bridge_offline, the
  // recovery path was NOT taken → fail.
  const s = makeSandbox();
  const projectPath = "/test/ReloadRecoveryGame";
  plantLock(s, projectPath, process.pid, 0, "reloading");
  try {
    const client = new LiveClient(1, new PingCache(), undefined, projectPath);

    // Race the call against a 3s guard. waitForCompile caps at 120s; if the
    // recovery path was entered we expect to still be waiting at 3s.
    type Outcome =
      | { kind: "result"; result: CallToolResult }
      | { kind: "still-waiting" };
    const stillWaiting = new Promise<Outcome>((resolve) => {
      setTimeout(() => resolve({ kind: "still-waiting" }), 3000);
    });
    const outcome = await Promise.race<Outcome>([
      client
        .route("unity_open_mcp_validate_edit", { paths: ["Assets"] })
        .then((r): Outcome => ({ kind: "result", result: r })),
      stillWaiting,
    ]);

    if (outcome.kind === "still-waiting") {
      // Recovery path entered (waitForCompile active) — the bug is fixed.
      return;
    }
    // If it returned, it must NOT be the instant bridge_offline — only
    // compile_timeout or bridge_compile_failed prove the wait/recovery path ran.
    assert.equal(outcome.result.isError, true);
    const body = JSON.parse((outcome.result.content[0] as { text: string }).text);
    assert.ok(
      body.error.code === "compile_timeout" || body.error.code === "bridge_compile_failed",
      `Expected compile_timeout/bridge_compile_failed (recovery path entered), got ${body.error.code}`,
    );
  } finally {
    disposeSandbox(s);
  }
});

test("T-fix-2: transient refusal with no reload signal exhausts retries then offline", async () => {
  // No lock file → classifyInstance "gone" every attempt → the bounded backoff
  // retry path. With nothing ever coming up, the loop must exhaust its retries
  // and return bridge_offline (proving the retry path runs and terminates,
  // rather than hanging or instant-offline). The "after N retries" message
  // suffix distinguishes this from the pre-fix instant offline.
  const s = makeSandbox();
  const projectPath = "/test/BackoffExhaustGame";
  try {
    // Port 1 — nothing listening, ECONNREFUSED every probe.
    const client = new LiveClient(1, new PingCache(), undefined, projectPath);
    const result = await client.route("unity_senses_read_console", {});
    assert.equal(result.isError, true);
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.error.code, "bridge_offline");
    // The recovery helper's offline message names the retry count — proves the
    // backoff loop ran instead of the pre-fix instant offline.
    assert.ok(
      body.error.message.includes("retries"),
      `expected the retry-exhausted offline message, got: ${body.error.message}`,
    );
  } finally {
    disposeSandbox(s);
  }
});

test("T-fix-2: dead-bridge still fails fast despite recovery paths", async () => {
  // Same as the existing fail-fast test but explicit: the recovery paths must
  // NOT mask a dead bridge. Stale heartbeat + live PID → dead_bridge →
  // bridge_compile_failed immediately, no waiting.
  const s = makeSandbox();
  try {
    plantLock(s, DEAD_BRIDGE_PROJECT, process.pid, 60_000, "reloading");
    const client = new LiveClient(1, new PingCache(), undefined, DEAD_BRIDGE_PROJECT);
    const result = await client.route("unity_open_mcp_validate_edit", {
      paths: ["Assets"],
    });
    assert.equal(result.isError, true);
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.error.code, "bridge_compile_failed");
  } finally {
    disposeSandbox(s);
  }
});

// ----- Endpoint refresh from lock (specs/feedback.md entries 2 + 4A) -----
//
// LiveClient cached baseUrl/authToken once at construction. On a Unity restart
// (new PID/port/authToken in the lock) every retry hit the dead cached port
// for minutes (entry 2), and the POST-path retry re-dispatched the Work to the
// new bridge — duplicate side-effects (entry 4A). The fix: handleTransientOffline
// calls refreshEndpointFromLock() first, and postTool only re-POSTs when the
// endpoint actually changed.

const REFRESH_PROJECT = "/test/RestartRefreshGame";

/**
 * Handler that serves idle 200 on /ping and a DIRECT_RESPONSE-style success on
 * /tools/*, recording how many tool POSTs it received. Used to prove (a) the
 * client reached this stub after a lock rewrite, and (b) no duplicate POSTs.
 */
function countingIdleHandler(
  toolHits: { count: number },
): (req: IncomingMessage, res: ServerResponse) => void {
  return (req, res) => {
    if (req.url?.startsWith("/tools/")) toolHits.count++;
    res.writeHead(200, { "Content-Type": "application/json" });
    if (req.url === "/ping") {
      res.end(
        JSON.stringify({
          connected: true,
          projectPath: REFRESH_PROJECT,
          unityVersion: "6000.0.0f1",
          bridgeVersion: "0.1.0",
          mode: "live",
          compiling: false,
          isPlaying: false,
        }),
      );
      return;
    }
    res.end(JSON.stringify({ ok: true }));
  };
}

test("refresh: LiveClient re-points at the new port after a Unity restart (entry 2)", async () => {
  // Start a stub on port A, plant a lock pointing at port A (live PID = us).
  // Then rewrite the lock to port B, start a stub on port B, and close stub A.
  // A tool call must now succeed against port B — proving baseUrl refreshed.
  const s = makeSandbox();
  const hitsB: { count: number } = { count: 0 };
  try {
    const bridgeA = await startBridgeStub(countingIdleHandler({ count: 0 }));
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridgeA.port);
    const client = new LiveClient(
      bridgeA.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    // Simulate the restart: new port in the lock, stub B up, stub A down.
    const bridgeB = await startBridgeStub(countingIdleHandler(hitsB));
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridgeB.port);
    await bridgeA.close();

    // route a read-only tool. The first fetch hits the dead port A → ECONNREFUSED
    // → handleTransientOffline → refreshEndpointFromLock re-points to port B →
    // the /ping probe succeeds → caller retries → succeeds on port B.
    const result = await client.route("unity_open_mcp_validate_edit", {
      paths: ["Assets"],
    });
    assert.equal(result.isError, false, "tool must succeed against the new port");
    assert.ok(
      hitsB.count >= 1,
      "the tool POST reached stub B (the refreshed endpoint)",
    );
    await bridgeB.close();
  } finally {
    disposeSandbox(s);
  }
});

test("refresh: connection failure (socket reset) RETRIES on unchanged endpoint (entry 2026-07-02-b)", async () => {
  // A bridge that is UP for /ping (so handleTransientOffline recovers → null)
  // but whose FIRST /tools/* POST hard-resets the socket (req.socket.destroy),
  // then succeeds on the next POST. undici throws a TypeError for a socket
  // reset — a *connection* failure, not a response timeout. Because no bytes
  // reached the server's dispatch queue on the reset POST, no side effect is
  // possible and re-POSTing is SAFE even on an unchanged endpoint. The client
  // must retry and the second POST must succeed.
  //
  // This is the regression test for the bug where a burst of parallel /tools/*
  // calls left a dead connection in the pool: every subsequent POST threw a
  // TypeError on connect, the old "unchanged endpoint → no retry" guard
  // wedged the whole typed-editor surface, and the agent saw bridge_offline
  // even though the bridge was healthy and curl worked fine.
  const s = makeSandbox();
  const toolHits: { count: number } = { count: 0 };
  try {
    const bridge = await startBridgeStub((req, res) => {
      if (req.url?.startsWith("/tools/")) {
        toolHits.count++;
        if (toolHits.count === 1) {
          // First POST: hard-reset the socket BEFORE responding. The client's
          // fetch throws a TypeError ("fetch failed") → connection-class failure.
          // req.socket.destroy (not res.destroy) reliably aborts the client's
          // fetch; res.destroy can leave the socket half-open and hang the
          // client until its timeout.
          req.socket.destroy();
          return;
        }
        // Second POST: succeed (DIRECT_RESPONSE-style body).
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: true }));
        return;
      }
      // /ping answers idle 200 so recovery returns null ("recovered") fast.
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(
        JSON.stringify({
          connected: true,
          projectPath: REFRESH_PROJECT,
          unityVersion: "6000.0.0f1",
          bridgeVersion: "0.1.0",
          mode: "live",
          compiling: false,
          isPlaying: false,
        }),
      );
    });
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    const result = await client.route("unity_open_mcp_validate_edit", {
      paths: ["Assets"],
    });
    // Exactly two tool POSTs — the first reset, the retry succeeded. Pre-fix
    // this was 1 POST and a bridge_offline result (the wedge).
    assert.equal(
      toolHits.count,
      2,
      `expected exactly two tool POSTs (reset then retry), got ${toolHits.count}`,
    );
    assert.equal(result.isError, false, "retry against the same endpoint must succeed");
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

test("shouldRetryPostAfterFailure: unit cases for the connection-vs-timeout retry policy", () => {
  // Pure unit test of the retry decision — fast and exhaustive, complementing
  // the connection-failure integration test above. Driving a real response-
  // timeout through postTool takes 40s (the fetch timeout is floored at
  // BRIDGE_DEFAULT_TIMEOUT_MS + slack), so the four discriminating cases are
  // covered here on the pure helper instead.
  //
  // TypeError   = connection failure (socket never connected / reset before the
  //               body flushed / ECONNREFUSED). No bytes reached the server →
  //               retry is always safe.
  // AbortError  = response timeout (connected, server still processing). A side
  //               effect may have happened → only retry when endpoint changed.
  const typeErr = new TypeError("fetch failed");
  const abortErr = new DOMException("The operation was aborted", "AbortError");
  const otherErr = new Error("something else");

  // Unchanged endpoint: connection failure retries, timeout/other does not.
  assert.equal(shouldRetryPostAfterFailure(typeErr, false), true);
  assert.equal(shouldRetryPostAfterFailure(abortErr, false), false);
  assert.equal(shouldRetryPostAfterFailure(otherErr, false), false);

  // Endpoint changed (Unity restart → new bridge never saw the POST): always
  // retry regardless of failure class — a restart makes the retry safe even for
  // a response timeout, because the new listener never received the Work.
  assert.equal(shouldRetryPostAfterFailure(typeErr, true), true);
  assert.equal(shouldRetryPostAfterFailure(abortErr, true), true);
  assert.equal(shouldRetryPostAfterFailure(otherErr, true), true);
});

test("refresh: env-port override disables lock refresh", async () => {
  // When UNITY_OPEN_MCP_BRIDGE_PORT (passed as envPort) pins the endpoint,
  // refreshEndpointFromLock must be a no-op even if the lock points elsewhere.
  // The client stays on the env port; a lock rewrite must NOT redirect it.
  const s = makeSandbox();
  try {
    const bridge = await startBridgeStub(countingIdleHandler({ count: 0 }));
    // Lock points at a DIFFERENT port, but envPort pins the client to the stub.
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", 1);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      undefined,
      REFRESH_PROJECT,
      undefined,
      bridge.port, // envPort override — authoritative
    );
    // Direct call: a ping against the env-pinned port must succeed despite the
    // lock claiming port 1.
    const live = await client.isLiveAvailable();
    assert.equal(live, true, "env-port override keeps the client on the stub port");
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

// ----- Direct-response envelope-shape routing (entry 2026-07-02-b real cause) -----
//
// The bridge returns TWO /tools/* response shapes:
//   1. Mutation envelope  { mutation: { success, ... }, gate: {...} } for gate-
//      tracked mutating tools.
//   2. Direct body         the tool's own JSON (e.g. { status, count, tags })
//      for read-only / state-only tools the bridge classifies as
//      DirectResponseTools (~80: editor_get_tags, asmdef_list, shader_list_all,
//      scene_list_opened, material_get_properties, ...).
//
// postTool previously routed off a hardcoded DIRECT_RESPONSE_TOOLS set that was
// ~60 entries behind the bridge's set. Tools missing from it fell through to
// deriveIsError(body), which dereferenced body.mutation.success — undefined for
// a direct body → TypeError. That thrown TypeError landed in the connection-
// recovery catch block, surfacing a misleading bridge_offline (and, with the
// entry-2026-07-02-b retry change, looping). The fix: detect the envelope shape
// from the body itself (presence of a top-level `mutation` object), so the
// hardcoded set is no longer the routing authority. These tests pin both shapes.

/**
 * Bridge stub that serves idle /ping and a DIRECT body (no `mutation` field)
 * for /tools/*, recording how many tool POSTs it received. The direct body
 * shape is what ~80 read-only typed-editor tools return.
 */
function directBodyHandler(
  toolHits: { count: number },
): (req: IncomingMessage, res: ServerResponse) => void {
  return (req, res) => {
    if (req.url?.startsWith("/tools/")) toolHits.count++;
    res.writeHead(200, { "Content-Type": "application/json" });
    if (req.url === "/ping") {
      res.end(
        JSON.stringify({
          connected: true,
          projectPath: REFRESH_PROJECT,
          unityVersion: "6000.0.0f1",
          bridgeVersion: "0.1.0",
          mode: "live",
          compiling: false,
          isPlaying: false,
        }),
      );
      return;
    }
    // Direct body: the tool's own JSON, NO mutation/gate envelope. This is the
    // shape that broke before the fix (deriveIsError threw on missing mutation).
    res.end(JSON.stringify({ status: "ok", count: 7, tags: ["Untagged", "Player"] }));
  };
}

test("envelope shape: direct body (no mutation field) is returned verbatim, no loop", async () => {
  // A tool NOT in the DIRECT_RESPONSE_TOOLS allowlist that returns a direct body
  // (editor_get_tags shape). postTool must route it as a direct response based
  // on the BODY, return it verbatim, and post exactly ONE POST (no retry loop).
  // Pre-fix this surfaced bridge_offline (the thrown TypeError hit the catch).
  const s = makeSandbox();
  const toolHits: { count: number } = { count: 0 };
  try {
    const bridge = await startBridgeStub(directBodyHandler(toolHits));
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    // editor_get_tags is NOT in DIRECT_RESPONSE_TOOLS but the bridge returns a
    // direct body for it. Must succeed and post exactly once.
    const result = await client.route("unity_open_mcp_editor_get_tags", {});
    assert.equal(result.isError, false, "direct body must not be an error");
    assert.equal(
      toolHits.count,
      1,
      `expected exactly one tool POST (no retry/loop), got ${toolHits.count}`,
    );
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.status, "ok");
    assert.equal(body.count, 7);
    assert.deepEqual(body.tags, ["Untagged", "Player"]);
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

test("envelope shape: mutation body is routed through deriveIsError", async () => {
  // A mutating tool that returns a proper mutation envelope. postTool must route
  // it through deriveIsError and surface the gate envelope. Regression guard
  // that the shape-detection change did not break the mutation path.
  const s = makeSandbox();
  const toolHits: { count: number } = { count: 0 };
  try {
    const bridge = await startBridgeStub((req, res) => {
      if (req.url?.startsWith("/tools/")) toolHits.count++;
      res.writeHead(200, { "Content-Type": "application/json" });
      if (req.url === "/ping") {
        res.end(
          JSON.stringify({
            connected: true,
            projectPath: REFRESH_PROJECT,
            unityVersion: "6000.0.0f1",
            bridgeVersion: "0.1.0",
            mode: "live",
            compiling: false,
            isPlaying: false,
          }),
        );
        return;
      }
      // Full mutation envelope with a clean gate delta.
      res.end(
        JSON.stringify({
          mutation: { success: true, output: { ok: true }, error: null },
          gate: {
            mode: "enforce",
            skipped: false,
            validation: { passed: true },
            delta: { newErrors: 0, newWarnings: 0 },
          },
          agentNextSteps: [],
        }),
      );
    });
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    const result = await client.route("unity_open_mcp_gameobject_create", {
      name: "X",
      paths_hint: ["Assets"],
    });
    assert.equal(result.isError, false, "clean mutation envelope is not an error");
    assert.equal(toolHits.count, 1);
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.mutation.success, true);
    assert.equal(body.gate.mode, "enforce");
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

test("envelope shape: mutation body with newErrors is an error", async () => {
  // deriveIsError must still flag a mutation envelope whose gate delta reports
  // new errors, even after the shape-detection + defensive hardening change.
  const s = makeSandbox();
  try {
    const bridge = await startBridgeStub((req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      if (req.url === "/ping") {
        res.end(
          JSON.stringify({
            connected: true,
            projectPath: REFRESH_PROJECT,
            unityVersion: "6000.0.0f1",
            bridgeVersion: "0.1.0",
            mode: "live",
            compiling: false,
            isPlaying: false,
          }),
        );
        return;
      }
      res.end(
        JSON.stringify({
          mutation: { success: true, output: {}, error: null },
          gate: {
            mode: "enforce",
            skipped: false,
            validation: { passed: false },
            delta: { newErrors: 2, newWarnings: 0 },
          },
          agentNextSteps: [],
        }),
      );
    });
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    const result = await client.route("unity_open_mcp_gameobject_create", {
      name: "X",
      paths_hint: ["Assets"],
    });
    assert.equal(result.isError, true, "gate delta with newErrors must be an error");
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

// ----- Malformed bridge body (specs/feedback.md entry 2026-07-03-c) -----
//
// When the bridge returns HTTP 200 with a body that is NOT valid JSON AND has
// substantial content (the case that motivated the fix: an execute_csharp
// snippet whose OutputSerializer output was corrupted by a TypeLoadException
// mid-walk, interpolated raw into the gate envelope), JSON.parse rejects.
// Previously postTool's direct-body fallback silently degraded to
// isError:false with an empty `{}` — a fake success. For compile-reload
// tools, annotateCompileVerify (gated on !result.isError) then stamped
// `_compileVerify` onto the `{}`, producing a malformed envelope
// `{"_compileVerify":{...}}` with NO `mutation` object —
// violating the documented contract. The fix: surface a structured
// `bridge_response_unparsable` error instead so the failure is visible and the
// annotation never runs on a body that lost its mutation block.

test("malformed body: compile-reload tool surfaces as error and is NOT annotated with _compileVerify", async () => {
  // execute_csharp is a compile-reload tool. The bridge returns 200 with a body
  // that is not valid JSON (unbalanced braces — mirroring the real failure:
  // OutputSerializer truncating mid-walk). postTool must surface a structured
  // error, and because isError is true, annotateCompileVerify must NOT stamp
  // _compileVerify onto the body.
  const s = makeSandbox();
  try {
    const bridge = await startBridgeStub((req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      if (req.url === "/ping") {
        res.end(
          JSON.stringify({
            connected: true,
            projectPath: REFRESH_PROJECT,
            unityVersion: "6000.0.0f1",
            bridgeVersion: "0.1.0",
            mode: "live",
            compiling: false,
            isPlaying: false,
          }),
        );
        return;
      }
      // Malformed: unbalanced JSON. res.json() will reject on the client side.
      res.end('{"mutation":{"success":true,"output":{"broken":');
    });
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    const result = await client.route("unity_open_mcp_execute_csharp", {
      code: "return 1;",
    });
    assert.equal(
      result.isError,
      true,
      "malformed bridge body must surface as an error, not a fake success",
    );
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(
      body.error.code,
      "bridge_response_unparsable",
      "must carry the machine-readable unparsable-body code",
    );
    // The body must NOT carry a _compileVerify annotation — that was the
    // signature of the bug (an envelope with _compileVerify but no mutation).
    assert.equal(
      body._compileVerify,
      undefined,
      "annotateCompileVerify must not run on an errored result",
    );
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

test("malformed body: non-compile-reload tool surfaces as error", async () => {
  // A read-only tool (lifecycle 'none') whose body fails to parse must also
  // surface as a structured error — the guard is not compile-reload-specific.
  const s = makeSandbox();
  try {
    const bridge = await startBridgeStub((req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      if (req.url === "/ping") {
        res.end(
          JSON.stringify({
            connected: true,
            projectPath: REFRESH_PROJECT,
            unityVersion: "6000.0.0f1",
            bridgeVersion: "0.1.0",
            mode: "live",
            compiling: false,
            isPlaying: false,
          }),
        );
        return;
      }
      // Garbage bytes, not JSON at all.
      res.end("<<<not json>>>");
    });
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    const result = await client.route("unity_open_mcp_editor_get_tags", {});
    assert.equal(result.isError, true, "garbage body must surface as an error");
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.error.code, "bridge_response_unparsable");
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

test("malformed body: direct-body tool with valid JSON still succeeds (regression guard)", async () => {
  // The unparsable-body guard must NOT regress the direct-body success path:
  // a tool whose body parses cleanly (even when it has no `mutation` field)
  // must still return isError:false verbatim. This pins the boundary between
  // "parsed === null → error" and "parsed !== null → direct body".
  const s = makeSandbox();
  try {
    const bridge = await startBridgeStub((req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      if (req.url === "/ping") {
        res.end(
          JSON.stringify({
            connected: true,
            projectPath: REFRESH_PROJECT,
            unityVersion: "6000.0.0f1",
            bridgeVersion: "0.1.0",
            mode: "live",
            compiling: false,
            isPlaying: false,
          }),
        );
        return;
      }
      res.end(JSON.stringify({ tags: ["Untagged", "Player"] }));
    });
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    const result = await client.route("unity_open_mcp_editor_get_tags", {});
    assert.equal(result.isError, false, "valid direct body must still succeed");
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.deepEqual(body.tags, ["Untagged", "Player"]);
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

// ----- Empty body on a compile-reload tool (specs/feedback.md 2026-07-05) -----
//
// The reload-vs-corruption split: a compile-reload tool that triggers a domain
// reload mid-response tears down the bridge's HTTP socket, so the agent
// observes HTTP 200 with an EMPTY body (no Content-Length / chunk completing).
// The mutation almost certainly committed (reimport_package / asmdef_* /
// package_add all commit before the reload), so framing this as
// bridge_response_unparsable would mislead the agent into suspecting
// corruption. Instead surface a typed triggered_reload outcome that tells the
// agent to verify post-state rather than retry blindly.

test("empty body on compile-reload tool surfaces as triggered_reload, not unparsable", async () => {
  // reimport_package is a compile-reload tool. The bridge returns 200 with an
  // EMPTY body (the signature of a domain reload tearing down the socket
  // mid-response). postTool must surface triggered_reload (isError:false), not
  // bridge_response_unparsable — the mutation likely committed.
  const s = makeSandbox();
  try {
    const bridge = await startBridgeStub((req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      if (req.url === "/ping") {
        res.end(
          JSON.stringify({
            connected: true,
            projectPath: REFRESH_PROJECT,
            unityVersion: "6000.0.0f1",
            bridgeVersion: "0.1.0",
            mode: "live",
            compiling: false,
            isPlaying: false,
          }),
        );
        return;
      }
      // Empty body — the hallmark of a socket torn down mid-response.
      res.end("");
    });
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    const result = await client.route("unity_open_mcp_reimport_package", {
      package_id: "com.alexeyperov.unity-open-mcp-bridge",
    });
    assert.equal(
      result.isError,
      false,
      "empty body on a compile-reload tool is triggered_reload, not an error",
    );
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.status, "triggered_reload");
    assert.equal(body.triggeredReload, true);
    assert.equal(body._route.outcome, "triggered_reload");
    // The hint must tell the agent NOT to retry blindly (it would re-run the
    // mutation) and to verify post-state instead.
    assert.ok(
      body.message.includes("do NOT retry blindly"),
      "message must warn against a blind retry",
    );
    assert.ok(
      body.agentNextSteps.some((s: string) => s.includes("editor_status")),
      "agentNextSteps must point at editor_status / bridge_status",
    );
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

test("empty body on NON-compile-reload tool still surfaces as bridge_response_unparsable", async () => {
  // The reload split is gated on lifecycle === compile-reload. A read-only
  // tool whose body is empty is genuine corruption (the bridge should always
  // return a body for these), so it must still surface the unparsable-body
  // error.
  const s = makeSandbox();
  try {
    const bridge = await startBridgeStub((req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      if (req.url === "/ping") {
        res.end(
          JSON.stringify({
            connected: true,
            projectPath: REFRESH_PROJECT,
            unityVersion: "6000.0.0f1",
            bridgeVersion: "0.1.0",
            mode: "live",
            compiling: false,
            isPlaying: false,
          }),
        );
        return;
      }
      res.end("");
    });
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    const result = await client.route("unity_open_mcp_editor_get_tags", {});
    assert.equal(
      result.isError,
      true,
      "empty body on a non-compile-reload tool is genuine corruption",
    );
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.error.code, "bridge_response_unparsable");
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

test("whitespace-only body on compile-reload tool still counts as triggered_reload", async () => {
  // A torn-down socket can deliver a stray newline or whitespace before the
  // connection drops. The reload detector must treat whitespace-only as empty
  // (trim before the length check) so the agent still gets triggered_reload.
  const s = makeSandbox();
  try {
    const bridge = await startBridgeStub((req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      if (req.url === "/ping") {
        res.end(
          JSON.stringify({
            connected: true,
            projectPath: REFRESH_PROJECT,
            unityVersion: "6000.0.0f1",
            bridgeVersion: "0.1.0",
            mode: "live",
            compiling: false,
            isPlaying: false,
          }),
        );
        return;
      }
      res.end("\n  \n");
    });
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    const result = await client.route("unity_open_mcp_asmdef_modify", {
      asset_path: "Assets/test.asmdef",
      add_references: ["Test"],
      paths_hint: ["Assets/test.asmdef"],
    });
    assert.equal(result.isError, false, "whitespace-only body trims to empty");
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.status, "triggered_reload");
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});

test("substantial corrupt body on compile-reload tool STILL surfaces as unparsable", async () => {
  // The 2026-07-03-c contract is preserved: a SUBSTANTIAL unparseable body on
  // a compile-reload tool is still treated as corruption (OutputSerializer
  // failure / truncated envelope), NOT triggered_reload. Only an empty body
  // is the reload signature.
  const s = makeSandbox();
  try {
    const bridge = await startBridgeStub((req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      if (req.url === "/ping") {
        res.end(
          JSON.stringify({
            connected: true,
            projectPath: REFRESH_PROJECT,
            unityVersion: "6000.0.0f1",
            bridgeVersion: "0.1.0",
            mode: "live",
            compiling: false,
            isPlaying: false,
          }),
        );
        return;
      }
      // Substantial corrupt body — half-written envelope from a serialization
      // failure. NOT the empty-body reload signature.
      res.end('{"mutation":{"success":true,"output":{"broken":');
    });
    plantLock(s, REFRESH_PROJECT, process.pid, 0, "idle", bridge.port);
    const client = new LiveClient(
      bridge.port,
      new PingCache(),
      "deadbeef",
      REFRESH_PROJECT,
    );

    const result = await client.route("unity_open_mcp_execute_csharp", {
      code: "return 1;",
    });
    assert.equal(
      result.isError,
      true,
      "substantial corrupt body is still an error, even on a compile-reload tool",
    );
    const body = JSON.parse((result.content[0] as { text: string }).text);
    assert.equal(body.error.code, "bridge_response_unparsable");
    await bridge.close();
  } finally {
    disposeSandbox(s);
  }
});
