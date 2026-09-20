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
