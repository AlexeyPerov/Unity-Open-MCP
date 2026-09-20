import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { LiveClient } from "./live-client.js";
import { withSchemaDefaults } from "./schema-defaults.js";
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

test("invoke validates a fresh schema before POST and pins the contract and lifecycle", async () => {
  const dispatched: unknown[][] = [];
  let version = "v1";
  let available = true;
  const command = () => ({ id: "project.demo.x", available, invocationSupported: true,
    schemaVersion: version, lifecycle: "restart_then_settle", inputSchema: {
      type: "object", additionalProperties: false, required: ["value"], properties: {
        value: { type: "integer", minimum: 1, maximum: 3 }, labels: { type: ["array", "null"], items: { type: "string" } },
      },
    } });
  const live = { projectCommands: async () => ({ command: command() }),
    route: async (...args: unknown[]) => { dispatched.push(args); return { content: [{ type: "text", text: "{}" }] }; }
  } as unknown as LiveClient;
  const router = new ToolRouter(live, {} as BatchSpawn, "", {} as BridgeEventStream, new ToolSessionState());
  for (const args of [{}, { value: "1" }, { value: 2, extra: true }, { value: 2, constructor: "unknown" }, { value: 4 }, { value: 1, labels: [2] }])
    assert.equal((await router.route(name, { action: "invoke", command_id: "project.demo.x", args })).isError, true);
  assert.equal(dispatched.length, 0);
  const invoke = { action: "invoke", command_id: "project.demo.x", args: { value: 2 }, gate: "warn", paths_hint: ["Assets/Test"] };
  assert.notEqual((await router.route(name, invoke)).isError, true);
  assert.deepEqual(dispatched[0], [name, { ...invoke, schema_version: "v1" }, "restart_then_settle"]);
  version = "v2";
  assert.equal((await router.route(name, { ...invoke, schema_version: "v1" })).isError, true);
  available = false;
  assert.equal((await router.route(name, invoke)).isError, true);
  assert.equal(dispatched.length, 1);
});

test("catalog lifecycle gives empty invocation responses the built-in reload outcome", async () => {
  const live = new LiveClient(1, new PingCache());
  const shape = (live as unknown as { shapeToolResult: (name: string, response: Response, lifecycle: string) => Promise<any> }).shapeToolResult.bind(live);
  const result = await shape(name, new Response("", { status: 200 }), "restart_then_settle");
  assert.equal(JSON.parse(result.content[0].text).status, "triggered_reload");
  assert.equal((await shape(name, new Response("", { status: 200 }), "none")).isError, true);
});


test("project command discovery and omitted gate survive CLI/stdio default injection", () => {
  const tool = ALL_TOOLS.find(t => t.name === name)!;
  for (const args of [{ action: "list" }, { action: "describe", id: "project.demo.x" }, { action: "invoke", command_id: "project.demo.x" }])
    assert.deepEqual(withSchemaDefaults(tool, args), args);
});
