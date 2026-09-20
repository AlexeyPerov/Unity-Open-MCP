import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { LiveClient } from "./live-client.js";
import { PingCache } from "./ping-cache.js";
import { ToolRouter } from "./tool-router.js";
import type { BridgeEventStream } from "./event-stream.js";
import type { BatchSpawn } from "./batch-spawn.js";
import { ALL_TOOLS } from "./tools/index.js";
import { ToolSessionState, filterVisibleTools } from "./tool-session-state.js";

const name = "unity_open_mcp_project_commands";
test("catalog is always visible after deactivating all groups", () => {
  const session = new ToolSessionState();
  for (const group of session.activeGroups()) session.deactivate(group);
  assert.ok(filterVisibleTools(ALL_TOOLS, session).some(t => t.name === name));
});

test("catalog uses fresh authenticated GET, preserves inventory and reports unsupported/unavailable", async () => {
  let mode = "catalog";
  let calls = 0;
  const server = createServer((req, res) => {
    assert.equal(req.headers.authorization, "Bearer test-token");
    const url = new URL(req.url!, "http://localhost");
    res.setHeader("content-type", "application/json");
    if (url.searchParams.has("catalog") && mode === "catalog") {
      calls++;
      assert.deepEqual(url.searchParams.getAll("tag"), ["a&b", "two"]);
      assert.equal(url.searchParams.get("query"), "a + b");
      res.end(JSON.stringify({ catalogVersion: 1, commands: [], total: calls }));
    } else if (mode === "error") { res.statusCode = 503; res.end("{}"); }
    else res.end(JSON.stringify({ tools: ["unity_open_mcp_ping"], groups: [] }));
  });
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  const port = (server.address() as { port: number }).port;
  const live = new LiveClient(port, new PingCache(), "test-token");
  try {
    const args = { action: "list", tags: ["a&b", "two"], query: "a + b", limit: 1 };
    assert.equal((await live.projectCommands(args)).total, 1);
    assert.equal((await live.projectCommands(args)).total, 2);
    assert.ok((await live.listBridgeTools())?.tools.has("unity_open_mcp_ping"));
    mode = "old";
    assert.equal(((await live.projectCommands(args)).error as { code: string }).code, "catalog_unsupported");
    mode = "error";
    assert.equal(((await live.projectCommands(args)).error as { code: string }).code, "catalog_unavailable");
  } finally { await new Promise<void>((resolve, reject) => server.close(e => e ? reject(e) : resolve())); }
  assert.equal(((await live.projectCommands({ action: "list" })).error as { code: string }).code, "catalog_unavailable");
});

test("router validates action-specific arguments and never dispatches a project command", async () => {
  const calls: Record<string, unknown>[] = [];
  const live = { projectCommands: async (args: Record<string, unknown>) => {
    calls.push(args); return { catalogVersion: 1, command: { id: args.id, available: true } };
  } } as unknown as LiveClient;
  const batch = { route: () => { throw new Error("must not spawn Unity"); } } as unknown as BatchSpawn;
  const router = new ToolRouter(live, batch, "", {} as BridgeEventStream, new ToolSessionState());
  for (const args of [{ action: "describe" }, { action: "describe", id: "project.demo.x", limit: 1 }, { action: "list", id: "x" }, { action: "invoke", id: "x" }, { action: "list", limit: 101 }, { action: "list", offset: -1 }])
    assert.equal((await router.route(name, args)).isError, true);
  assert.equal(calls.length, 0);
  assert.notEqual((await router.route(name, { action: "describe", id: "project.demo.x" })).isError, true);
  assert.deepEqual(calls, [{ action: "describe", id: "project.demo.x" }]);
});
