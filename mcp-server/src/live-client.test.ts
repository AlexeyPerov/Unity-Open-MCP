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
import {
  createServer,
  type Server as HttpServer,
  type IncomingMessage,
  type ServerResponse,
} from "node:http";
import { PingCache } from "./ping-cache.js";
import { LiveClient } from "./live-client.js";
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
