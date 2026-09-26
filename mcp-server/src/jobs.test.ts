import { test } from "node:test";
import assert from "node:assert/strict";
import { ToolRouter } from "./tool-router.js";
import { ToolSessionState, filterVisibleTools } from "./tool-session-state.js";
import { ALL_TOOLS } from "./tools/index.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import type { BridgeEventStream } from "./event-stream.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
const body = (result: CallToolResult) => JSON.parse((result.content[0] as { text: string }).text);
const name = "unity_open_mcp_jobs";
test("always-visible local jobs work without bridge, reject arbitrary tools, and isolate routed identities", async t => {
  const session = new ToolSessionState(); for (const group of session.activeGroups()) session.deactivate(group);
  assert.ok(filterVisibleTools(ALL_TOOLS, session).some(tool => tool.name === name));
  const router = new ToolRouter({} as LiveClient, {} as BatchSpawn, "/project", {} as BridgeEventStream, session);
  t.after(() => router.jobs.close());
  assert.deepEqual(body(await router.route(name, { action: "list" })).jobs, []);
  for (const target of ["unity_open_mcp_execute_csharp", "unity_open_mcp_build_start", "unity_open_mcp_reimport_package", "unity_open_mcp_package_add", "unity_open_mcp_reflection_probe_bake"]) {
    assert.ok(ALL_TOOLS.some(tool => tool.name === target), "Negative control must be a real registered tool");
    assert.equal(body(await router.route(name, { action: "start", tool_or_command: target, idempotency_key: target })).error.code, "job_operation_unsupported");
  }
  for (const args of [{ action: "wait" }, { action: "list", job_id: "x" }, { action: "start" }, { action: "wait", job_id: "x", timeout_ms: 30001 }, { action: "list", bogus: true }])
    assert.equal((await router.route(name, args)).isError, true);
  router.jobs.register("fixture", { mutating: true, cancellable: false, validate() {}, async run() { return { state: "succeeded", result: 1 }; } });
  const a = { agent: "a" }, b = { agent: "b" }, otherPort = { agent: "a", port: 9999 };
  const job = body(await router.routeJobs({ action: "start", tool_or_command: "fixture", idempotency_key: "same" }, a));
  for (const identity of [b, otherPort]) {
    assert.deepEqual(body(await router.routeJobs({ action: "list" }, identity)).jobs, []);
    for (const action of ["status", "wait", "cancel"])
      assert.equal(body(await router.routeJobs({ action, job_id: job.job_id }, identity)).error.code, "job_not_found");
  }
  const done = body(await router.routeJobs({ action: "wait", job_id: job.job_id, timeout_ms: 1000 }, a));
  assert.equal(done.state, "succeeded"); assert.equal(done._source, "local");
});

test("a retried project start returns its job without the catalog, which is described only for new jobs", async t => {
  let catalog: Record<string, unknown> = { command: { available: true, async: true, cancellable: false, schemaVersion: "v1", inputSchema: { type: "object" } } };
  let describes = 0;
  const live = {
    projectCommands: async () => { describes++; return catalog; },
    projectCommandJob: async (args: { job_id: string }) => ({ job_id: args.job_id, state: "succeeded", result: { done: true } }),
  } as unknown as LiveClient;
  const router = new ToolRouter(live, {} as BatchSpawn, "/project", {} as BridgeEventStream, new ToolSessionState());
  t.after(() => router.jobs.close());
  const start = (args: Record<string, unknown>, key: string) =>
    router.routeJobs({ action: "start", tool_or_command: "project.demo.async", args, idempotency_key: key });
  const job = body(await start({ args: {} }, "k"));
  assert.equal(job.state, "queued");
  assert.equal(body(await router.routeJobs({ action: "wait", job_id: job.job_id, timeout_ms: 1000 })).state, "succeeded");
  const describesAfterStart = describes;
  // The first response is lost; the retry lands while the Editor reloads.
  catalog = { error: { code: "catalog_unavailable", message: "Retry after compilation/reload settles." } };
  const retried = body(await start({ args: {} }, "k"));
  assert.equal(retried.job_id, job.job_id); assert.equal(retried.state, "succeeded");
  // A reload changed the declaration: the key still names the original job.
  catalog = { command: { available: true, async: false } };
  assert.equal(body(await start({ args: {} }, "k")).job_id, job.job_id);
  assert.equal(body(await start({ args: { other: true } }, "k")).error.code, "idempotency_conflict");
  assert.equal(describes, describesAfterStart, "a known key must not re-read the catalog");
  // A new key still needs the command's current async declaration.
  assert.equal(body(await start({ args: {} }, "new")).error.code, "job_operation_unsupported");
  catalog = { error: { code: "catalog_unavailable", message: "Retry after compilation/reload settles." } };
  assert.equal(body(await start({ args: {} }, "new")).error.code, "catalog_unavailable");
});
