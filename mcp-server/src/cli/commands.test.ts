// Tests for the CLI command implementations (src/cli/commands.ts).
//
// Commands are pure async functions over a RouterStack — they don't touch
// stdout/stderr or process.exit, so we can drive them with a fake stack and
// assert on the returned CliCommandResult directly. The fake stack replaces
// LiveClient (ping polling) and ToolRouter (run-tool routing) with stubs.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  runPingCommand,
  runWaitForReadyCommand,
  runStatusCommand,
  runRunToolCommand,
  runStreamEventsCommand,
  runVerifyCommand,
  runBaselineCommand,
  runRegressionCommand,
  helpText,
  versionText,
} from "./commands.js";
import type { RouterStack } from "../routers.js";
import type { LiveClient } from "../live-client.js";
import type { ToolRouter } from "../tool-router.js";
import type { BridgeEventStream } from "../event-stream.js";
import type { PingCache } from "../ping-cache.js";
import type { ResourceRouter } from "../resource-router.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

// ---------------------------------------------------------------------------
// fake stack helpers
// ---------------------------------------------------------------------------

interface FakeLiveOpts {
  available?: boolean;
  /** Body returned by the fake /ping route (when available). */
  pingBody?: Record<string, unknown>;
}

function makeFakeLive(opts: FakeLiveOpts = {}): LiveClient {
  const available = opts.available ?? true;
  const pingBody = opts.pingBody ?? {
    connected: true,
    compiling: false,
    isPlaying: false,
    projectPath: "/proj",
    unityVersion: "6000.0.0f1",
    bridgeVersion: "0.1.0",
    mode: "live",
  };
  return {
    async isLiveAvailable() {
      return available;
    },
    async route(tool: string) {
      if (tool === "unity_open_mcp_ping") {
        const body = available
          ? pingBody
          : {
              connected: false,
              projectPath: null,
              unityVersion: null,
              bridgeVersion: "unknown",
              mode: "offline",
              compiling: false,
              isPlaying: false,
            };
        return {
          content: [{ type: "text" as const, text: JSON.stringify(body) }],
          isError: false,
        } satisfies CallToolResult;
      }
      throw new Error(`fake live: unexpected tool ${tool}`);
    },
  } as unknown as LiveClient;
}

function makeFakeRouter(result: CallToolResult): ToolRouter {
  return {
    async route(_tool: string, _args: Record<string, unknown>) {
      return result;
    },
  } as unknown as ToolRouter;
}

function makeStack(opts: {
  live?: LiveClient;
  router?: ToolRouter;
  port?: number;
  projectPath?: string;
  authToken?: string;
}): RouterStack {
  return {
    live: opts.live ?? makeFakeLive(),
    batch: {} as never,
    router: opts.router ?? makeFakeRouter({
      content: [{ type: "text", text: JSON.stringify({ ok: true }) }],
      isError: false,
    }),
    pingCache: {} as PingCache,
    resourceRouter: {} as ResourceRouter,
    eventStream: { stop() { /* noop */ } } as unknown as BridgeEventStream,
    sessionState: {} as never,
    projectPath: opts.projectPath ?? "/proj",
    port: opts.port ?? 22028,
    authToken: opts.authToken,
  };
}

// ---------------------------------------------------------------------------
// ping
// ---------------------------------------------------------------------------

test("runPingCommand: ready bridge → exit 0, ready=true", async () => {
  const result = await runPingCommand(makeStack({}), {
    json: true,
    timeoutMs: 1000,
  });
  assert.equal(result.exitCode, 0);
  assert.equal((result.json as { ready: boolean }).ready, true);
  assert.equal((result.json as { status: string }).status, "ready");
  assert.match(result.human, /Bridge: http:\/\/127.0.0.1:22028/);
});

test("runPingCommand: compiling bridge → exit 1, ready=false", async () => {
  const stack = makeStack({
    live: makeFakeLive({
      available: true,
      pingBody: { connected: true, compiling: true, isPlaying: false },
    }),
  });
  const result = await runPingCommand(stack, { json: true, timeoutMs: 1000 });
  assert.equal(result.exitCode, 1);
  assert.equal((result.json as { status: string }).status, "compiling");
  assert.match(result.human, /compiling/i);
});

test("runPingCommand: offline bridge → exit 1, status=offline", async () => {
  const stack = makeStack({ live: makeFakeLive({ available: false }) });
  const result = await runPingCommand(stack, { json: true, timeoutMs: 1000 });
  assert.equal(result.exitCode, 1);
  assert.equal((result.json as { status: string }).status, "offline");
});

// ---------------------------------------------------------------------------
// wait-for-ready
// ---------------------------------------------------------------------------

test("runWaitForReadyCommand: ready on first poll → exit 0", async () => {
  const result = await runWaitForReadyCommand(makeStack({}), {
    json: true,
    timeoutMs: 5_000,
    intervalMs: 50,
  });
  assert.equal(result.exitCode, 0);
  assert.equal((result.json as { ready: boolean }).ready, true);
  assert.equal((result.json as { status: string }).status, "ready");
});

test("runWaitForReadyCommand: never-ready bridge → exit 1, status=timeout", async () => {
  const stack = makeStack({ live: makeFakeLive({ available: false }) });
  const result = await runWaitForReadyCommand(stack, {
    json: true,
    timeoutMs: 200,
    intervalMs: 50,
  });
  assert.equal(result.exitCode, 1);
  assert.equal((result.json as { status: string }).status, "timeout");
});

// ---------------------------------------------------------------------------
// status
// ---------------------------------------------------------------------------

test("runStatusCommand: always exits 0 and reports resolved port + project", async () => {
  const result = await runStatusCommand(
    makeStack({ port: 19120, projectPath: "/path/to/MyGame" }),
    { json: true },
  );
  assert.equal(result.exitCode, 0);
  const json = result.json as {
    projectPath: string;
    port: number;
    instance: { classification: string };
    bridge: { status: string; ready: boolean };
  };
  assert.equal(json.projectPath, "/path/to/MyGame");
  assert.equal(json.port, 19120);
  // No lock file planted for this project → classification is "gone".
  assert.equal(json.instance.classification, "gone");
  assert.equal(json.bridge.status, "ready");
  assert.equal(json.bridge.ready, true);
});

test("runStatusCommand: surfaces authTokenDiscovered=true when token present", async () => {
  const result = await runStatusCommand(
    makeStack({ authToken: "deadbeef" }),
    { json: true },
  );
  assert.equal(
    (result.json as { authTokenDiscovered: boolean }).authTokenDiscovered,
    true,
  );
});

// ---------------------------------------------------------------------------
// run-tool
// ---------------------------------------------------------------------------

test("runRunToolCommand: unknown tool → exit 2, error code unknown_tool", async () => {
  const result = await runRunToolCommand(makeStack({}), {
    json: true,
    toolName: "unity_open_mcp_bogus",
    toolArgs: {},
  });
  assert.equal(result.exitCode, 2);
  const json = result.json as {
    tool: string;
    error: { code: string; available: string[] };
  };
  assert.equal(json.tool, "unity_open_mcp_bogus");
  assert.equal(json.error.code, "unknown_tool");
  assert.ok(json.error.available.length > 0);
  assert.match(result.human, /Unknown tool/);
});

test("runRunToolCommand: routes a known local tool and returns its JSON", async () => {
  // unity_open_mcp_capabilities is local-only; the fake router returns a fixed
  // envelope so we can assert the body flows through.
  const capsResult: CallToolResult = {
    content: [
      { type: "text", text: JSON.stringify({ tools: [{ name: "x" }] }) },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(capsResult) });
  const result = await runRunToolCommand(stack, {
    json: true,
    toolName: "unity_open_mcp_capabilities",
    toolArgs: {},
  });
  assert.equal(result.exitCode, 0);
  const json = result.json as { tool: string; isError: boolean; result: { tools: unknown[] } };
  assert.equal(json.tool, "unity_open_mcp_capabilities");
  assert.equal(json.isError, false);
  assert.deepEqual(json.result, { tools: [{ name: "x" }] });
});

test("runRunToolCommand: isError result → exit 1", async () => {
  const errorResult: CallToolResult = {
    content: [{ type: "text", text: JSON.stringify({ error: { code: "x" } }) }],
    isError: true,
  };
  const stack = makeStack({ router: makeFakeRouter(errorResult) });
  const result = await runRunToolCommand(stack, {
    json: true,
    toolName: "unity_open_mcp_scan_paths",
    toolArgs: { paths: ["Assets"] },
  });
  assert.equal(result.exitCode, 1);
  assert.equal((result.json as { isError: boolean }).isError, true);
});

test("runRunToolCommand: schema defaults are injected (timeout_ms on run_tests)", async () => {
  // run_tests documents a 60000 default. A CLI call omitting timeout_ms must
  // receive the same default an MCP client would (parity requirement).
  let receivedArgs: Record<string, unknown> | undefined;
  const stack = makeStack({
    router: {
      async route(_tool: string, args: Record<string, unknown>) {
        receivedArgs = args;
        return {
          content: [{ type: "text", text: JSON.stringify({ status: "started" }) }],
          isError: false,
        } as CallToolResult;
      },
    } as unknown as ToolRouter,
  });
  await runRunToolCommand(stack, {
    json: true,
    toolName: "unity_senses_run_tests",
    toolArgs: {},
  });
  assert.equal(receivedArgs?.timeout_ms, 60_000);
});

test("runRunToolCommand: bridge_status is a known tool (CLI parity)", async () => {
  // phase-3: the operator-only bridge_status tool must be reachable from the
  // CLI `run-tool` surface, not just the MCP server. ALL_TOOLS registration is
  // what makes it known; this guards against accidental removal.
  const statusResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({ status: "running", ready: true, _source: "local" }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(statusResult) });
  const result = await runRunToolCommand(stack, {
    json: true,
    toolName: "unity_open_mcp_bridge_status",
    toolArgs: {},
  });
  assert.equal(result.exitCode, 0);
  const json = result.json as { tool: string; isError: boolean; result: { status: string } };
  assert.equal(json.tool, "unity_open_mcp_bridge_status");
  assert.equal(json.isError, false);
  assert.equal(json.result.status, "running");
});

// ---------------------------------------------------------------------------
// stream-events
// ---------------------------------------------------------------------------

test("runStreamEventsCommand: drains events and exits 0 on success (non-follow)", async () => {
  const pullResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          subscriberId: "sub",
          events: [
            { seq: 1, ts: "t1", type: "log", logType: "log", message: "hello" },
          ],
          dropped: 0,
          connected: true,
          started: true,
          lastError: null,
        }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(pullResult) });
  const result = await runStreamEventsCommand(stack, {
    json: true,
    maxEvents: 50,
    follow: false,
  });
  assert.equal(result.exitCode, 0);
  const json = result.json as { connected: boolean; eventCount: number; events: unknown[] };
  assert.equal(json.connected, true);
  assert.equal(json.eventCount, 1);
});

test("runStreamEventsCommand: bridge unavailable + no events → exit 3 (timeout)", async () => {
  const pullResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          events: [],
          connected: false,
          lastError: "connect ECONNREFUSED",
        }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(pullResult) });
  const result = await runStreamEventsCommand(stack, {
    json: true,
    maxEvents: 50,
    follow: false,
  });
  assert.equal(result.exitCode, 3);
});

// ---------------------------------------------------------------------------
// verify
// ---------------------------------------------------------------------------

test("runVerifyCommand: no errors → exit 0 (success)", async () => {
  const scanResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          passed: true,
          issueCount: 0,
          issuesBySeverity: { error: 0, warn: 0, info: 0, verbose: 0 },
        }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(scanResult) });
  const result = await runVerifyCommand(stack, {
    json: true,
    paths: ["Assets/Prefabs"],
    mode: "auto",
    failOnSeverity: undefined,
    profile: undefined,
    includeRules: undefined,
    excludeRules: undefined,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 0);
  assert.equal((result.json as { tool: string }).tool, "unity_open_mcp_scan_paths");
});

test("runVerifyCommand: errors present → exit 2", async () => {
  const scanResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          passed: false,
          issueCount: 3,
          issuesBySeverity: { error: 2, warn: 1, info: 0, verbose: 0 },
        }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(scanResult) });
  const result = await runVerifyCommand(stack, {
    json: true,
    paths: ["Assets"],
    mode: "auto",
    failOnSeverity: undefined,
    profile: undefined,
    includeRules: undefined,
    excludeRules: undefined,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 2);
});

test("runVerifyCommand: warnings-only → exit 1", async () => {
  const scanResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          passed: true,
          issueCount: 2,
          issuesBySeverity: { error: 0, warn: 2, info: 0, verbose: 0 },
        }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(scanResult) });
  const result = await runVerifyCommand(stack, {
    json: true,
    paths: ["Assets"],
    mode: "auto",
    failOnSeverity: undefined,
    profile: undefined,
    includeRules: undefined,
    excludeRules: undefined,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 1);
});

test("runVerifyCommand: no paths + auto mode → routes to scan_all", async () => {
  const scanAllResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          summary: { counts: { error: 0, warn: 0, info: 0, verbose: 0 } },
        }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(scanAllResult) });
  const result = await runVerifyCommand(stack, {
    json: true,
    paths: [],
    mode: "auto",
    failOnSeverity: undefined,
    profile: undefined,
    includeRules: undefined,
    excludeRules: undefined,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 0);
  assert.equal((result.json as { tool: string }).tool, "unity_open_mcp_scan_all");
});

test("runVerifyCommand: validate-edit mode requires paths", async () => {
  const stack = makeStack({});
  const result = await runVerifyCommand(stack, {
    json: true,
    paths: [],
    mode: "validate-edit",
    failOnSeverity: undefined,
    profile: undefined,
    includeRules: undefined,
    excludeRules: undefined,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 2);
  assert.match(result.human, /validate-edit mode requires/);
});

test("runVerifyCommand: bridge_unavailable error → exit 3 (timeout)", async () => {
  const errorResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({ error: { code: "bridge_unavailable" } }),
      },
    ],
    isError: true,
  };
  const stack = makeStack({ router: makeFakeRouter(errorResult) });
  const result = await runVerifyCommand(stack, {
    json: true,
    paths: ["Assets"],
    mode: "auto",
    failOnSeverity: undefined,
    profile: undefined,
    includeRules: undefined,
    excludeRules: undefined,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 3);
});

test("runVerifyCommand: --fail-on-severity warn makes warnings fail (exit 2)", async () => {
  const scanResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          passed: false,
          issueCount: 1,
          issuesBySeverity: { error: 0, warn: 1, info: 0, verbose: 0 },
        }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(scanResult) });
  const result = await runVerifyCommand(stack, {
    json: true,
    paths: ["Assets"],
    mode: "auto",
    failOnSeverity: "warn",
    profile: undefined,
    includeRules: undefined,
    excludeRules: undefined,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 2);
});

// ---------------------------------------------------------------------------
// baseline
// ---------------------------------------------------------------------------

test("runBaselineCommand: success → exit 0", async () => {
  const baselineResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          baselinePath: "CI/baseline.json",
          schemaVersion: "v1",
          generatedAt: "2026-07-01T00:00:00Z",
          summary: { error: 5, warn: 10, info: 0, verbose: 0 },
        }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(baselineResult) });
  const result = await runBaselineCommand(stack, {
    json: true,
    subcommand: "create",
    baselinePath: "CI/baseline.json",
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 0);
  assert.equal((result.json as { subcommand: string }).subcommand, "create");
});

test("runBaselineCommand: tool error → exit 2", async () => {
  const errorResult: CallToolResult = {
    content: [
      { type: "text", text: JSON.stringify({ error: { code: "scan_failed" } }) },
    ],
    isError: true,
  };
  const stack = makeStack({ router: makeFakeRouter(errorResult) });
  const result = await runBaselineCommand(stack, {
    json: true,
    subcommand: "create",
    baselinePath: undefined,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 2);
});

// ---------------------------------------------------------------------------
// regression
// ---------------------------------------------------------------------------

test("runRegressionCommand: no regression → exit 0", async () => {
  const regResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          regressed: false,
          baseline: { error: 5 },
          current: { error: 5 },
          delta: { error: 0 },
        }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(regResult) });
  const result = await runRegressionCommand(stack, {
    json: true,
    baselinePath: "CI/baseline.json",
    regressionThreshold: undefined,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 0);
  assert.equal((result.json as { regressed: boolean }).regressed, false);
});

test("runRegressionCommand: regression detected → exit 2", async () => {
  const regResult: CallToolResult = {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          regressed: true,
          baseline: { error: 5 },
          current: { error: 8 },
          delta: { error: 3 },
        }),
      },
    ],
    isError: false,
  };
  const stack = makeStack({ router: makeFakeRouter(regResult) });
  const result = await runRegressionCommand(stack, {
    json: true,
    baselinePath: "CI/baseline.json",
    regressionThreshold: 0,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 2);
  assert.equal((result.json as { regressed: boolean }).regressed, true);
});

test("runRegressionCommand: tool error → exit 2", async () => {
  const errorResult: CallToolResult = {
    content: [
      { type: "text", text: JSON.stringify({ error: { code: "baseline_missing" } }) },
    ],
    isError: true,
  };
  const stack = makeStack({ router: makeFakeRouter(errorResult) });
  const result = await runRegressionCommand(stack, {
    json: true,
    baselinePath: "CI/baseline.json",
    regressionThreshold: undefined,
    platformProfile: undefined,
  });
  assert.equal(result.exitCode, 2);
});

// ---------------------------------------------------------------------------
// help / version text
// ---------------------------------------------------------------------------

test("helpText: mentions every command and key option", () => {
  const text = helpText("unity-open-mcp");
  for (const cmd of [
    "ping",
    "wait-for-ready",
    "status",
    "run-tool",
    "stream-events",
    "verify",
    "baseline",
    "regression",
  ]) {
    assert.ok(text.includes(cmd), `help missing ${cmd}`);
  }
  for (const opt of [
    "--json",
    "--project",
    "--port",
    "--timeout-ms",
    "--args",
    "--arg",
    "--max-events",
    "--follow",
    "--mode",
    "--fail-on-severity",
    "--baseline-path",
    "--regression-threshold",
  ]) {
    assert.ok(text.includes(opt), `help missing ${opt}`);
  }
  assert.ok(text.includes("UNITY_PROJECT_PATH"));
});

test("versionText: prints package + version", () => {
  assert.equal(versionText("0.1.0"), "unity-open-mcp 0.1.0");
});
