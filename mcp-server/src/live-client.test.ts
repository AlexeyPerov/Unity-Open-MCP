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
import { LiveClient } from "./live-client.js";
import { projectHash } from "./instance-discovery.js";
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
    port: 22028,
    authToken: "deadbeef",
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
