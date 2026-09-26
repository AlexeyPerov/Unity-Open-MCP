import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { LiveClient } from "../live-client.js";
import { PingCache } from "../ping-cache.js";

test("job-owned test start never replays a POST after the bridge drops its response", async t => {
  let starts = 0;
  const server = createServer((req, res) => {
    if (req.url === "/ping") {
      res.setHeader("Content-Type", "application/json");
      res.end(JSON.stringify({ connected: true, compiling: false, isPlaying: false, bridgeVersion: "1.2.3" }));
    } else if (req.url === "/tools/unity_senses_run_tests") {
      starts++;
      req.resume(); req.on("end", () => req.socket.destroy());
    } else { res.statusCode = 404; res.end(); }
  });
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  t.after(() => { server.closeAllConnections(); server.close(); });
  const port = (server.address() as { port: number }).port;
  const live = new LiveClient(port, new PingCache());
  await assert.rejects(live.runTestsJob({ run_id: "owned-run", timeout_ms: 1000 }, () => assert.fail("Unacknowledged start")));
  assert.equal(starts, 1);
});

test("bounded native result timeout orphans the owned run and same-key retry never posts twice", async t => {
  const { JobManager } = await import("./job-manager.js");
  const { testRunOperation } = await import("./adapters.js");
  let starts = 0;
  const server = createServer((req, res) => {
    res.setHeader("Content-Type", "application/json");
    if (req.url === "/ping") res.end(JSON.stringify({ connected: true, compiling: false, isPlaying: false }));
    else if (req.url === "/tools/unity_senses_run_tests") {
      let body = ""; req.on("data", chunk => body += chunk); req.on("end", () => {
        starts++; const args = JSON.parse(body);
        res.end(JSON.stringify({ status: "started", runId: args.run_id }));
      });
    } else { res.statusCode = 404; res.end("{}"); }
  });
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  t.after(() => { server.closeAllConnections(); server.close(); });
  const live = new LiveClient((server.address() as { port: number }).port, new PingCache());
  const manager = new JobManager(); t.after(() => manager.close());
  manager.register("tests", testRunOperation(() => live));
  const owner = { project: "/timeout-fixture", agent: "owner" };
  const args = { timeout_ms: 1000 };
  const job = manager.start(owner, "tests", args, "owned-timeout");
  const done = await manager.wait(owner, job.job_id, 5000);
  assert.equal(done.state, "orphaned");
  assert.equal(manager.start(owner, "tests", args, "owned-timeout").job_id, job.job_id);
  assert.equal(starts, 1);
});

test("project job status answered with HTTP 500 by a busy bridge is retried, not orphaned", async t => {
  const { JobManager } = await import("./job-manager.js");
  const { projectCommandOperation } = await import("./adapters.js");
  const jobs: string[] = [];
  const server = createServer((req, res) => {
    res.setHeader("Content-Type", "application/json");
    if (req.url?.startsWith("/tools?")) {
      res.end(JSON.stringify({ catalogVersion: 1, command: { available: true, async: true, cancellable: false, schemaVersion: "v1", inputSchema: { type: "object" } } }));
    } else if (req.url === "/project-command-jobs") {
      let body = ""; req.on("data", chunk => body += chunk); req.on("end", () => {
        const { action, job_id } = JSON.parse(body);
        jobs.push(action);
        // The bridge's main-thread dispatch timed out: unhandled exception envelope.
        if (jobs.length === 2) { res.statusCode = 500; res.end(JSON.stringify({ error: { code: "bridge_internal_error" } })); return; }
        res.end(JSON.stringify({ job_id, state: jobs.length === 1 ? "running" : "succeeded", phase: "executing", result: jobs.length === 1 ? null : { done: true } }));
      });
    } else { res.statusCode = 404; res.end("{}"); }
  });
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  t.after(() => { server.closeAllConnections(); server.close(); });
  const live = new LiveClient((server.address() as { port: number }).port, new PingCache());
  const manager = new JobManager(); t.after(() => manager.close());
  manager.register("project.demo.async", projectCommandOperation("project.demo.async", false, () => live, { pollIntervalMs: 5 }));
  const owner = { project: "/busy-fixture", agent: "owner" };
  const job = manager.start(owner, "project.demo.async", {}, "busy");
  const done = await manager.wait(owner, job.job_id, 5000);
  assert.equal(done.state, "succeeded"); assert.deepEqual(done.result, { done: true });
  assert.deepEqual(jobs, ["start", "status", "status"]);
});
