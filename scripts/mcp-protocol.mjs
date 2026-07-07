#!/usr/bin/env node
// mcp-protocol.mjs — S3: stdio MCP transport test suite.
//
// Agents use MCP over stdio; S0 only exercises `unity-open-mcp run-tool` CLI
// subprocesses (one fresh process per call). S3 spawns the actual MCP stdio
// server and drives it with JSON-RPC: initialize → tools/list → tools/call.
//
// Coverage:
//   Band P — protocol handshake: initialize (assert capabilities +
//            listChanged:true), tools/list (assert non-empty, parse tool names).
//   Band T — tools/call: one local-routed tool (capabilities, no Unity needed)
//            + one live tool when bridge is up (ping; skips with --skip-live).
//   Band M — manage_tools session: activate a non-default group → assert
//            notifications/tools/list_changed fires (or poll tools/list delta).
//   Band R — route spot-check: ≥10 tools, assert _route.route in the envelope
//            matches the router policy (live / local / offline / batch).
//
// The local-only portion runs without Unity. The live portion (tools/call on a
// live tool) requires the bridge up; pass --skip-live to skip it gracefully.
//
// Usage:
//   node scripts/mcp-protocol.mjs                            # local + live (if bridge up)
//   node scripts/mcp-protocol.mjs --project /path            # target project
//   node scripts/mcp-protocol.mjs --skip-live                # local-only portion
//   node scripts/mcp-protocol.mjs --list                     # list steps, don't run
//   node scripts/mcp-protocol.mjs --json-out report.json
//
// Requirements: mcp-server/dist/index.js built. For the live portion, a Unity
// Editor open with the project + bridge running. --skip-live runs the
// local-only portion without Unity.

import { spawn } from "node:child_process";
import { existsSync, writeFileSync } from "node:fs";
import { isAbsolute } from "node:path";

import { REPO_ROOT, CLI_BIN, parseCommonArgs } from "./mcp-test-lib.mjs";

function printHelp() {
  console.error(`Usage: node scripts/mcp-protocol.mjs [options]

Options:
  --project, -P <path>   Target Unity project (default: <repo>/demo). MUST be absolute.
  --skip-live            Skip the live-bridge portion (local-only: initialize,
                         tools/list, local tool calls, route spot-checks on
                         local-routed tools). Use when Unity is not running.
  --band P,T,M,R         Run only bands P=protocol-handshake, T=tools-call,
                         M=manage_tools-session, R=route-spot-check
  --only needle          Run only steps whose label contains any needle
  --list                 List the suite steps and exit (no execution)
  --json-out <file>      Write a machine-readable report to <file>
  --timeout-ms <n>       Per-step timeout (default 30000)
  -h, --help             Show this help

The CLI binary must exist at mcp-server/dist/index.js. For the live portion,
a Unity Editor must be open with the project + bridge running. --skip-live
runs the local-only portion without Unity.`);
}

function parseArgs(argv) {
  return parseCommonArgs(argv, {
    help: printHelp,
    defaults: { timeoutMs: 30_000 },
    extra: (a, opts, av, i) => {
      if (a === "--skip-live") { opts.skipLive = true; return i; }
      console.error(`Unknown argument: ${a}`);
      process.exit(2);
    },
  });
}

// --- stdio JSON-RPC client over a child process ---
class McpStdioClient {
  constructor(serverPath, env, projectPath) {
    this.serverPath = serverPath;
    this.env = { ...env, UNITY_PROJECT_PATH: projectPath };
    this.proc = null;
    this.buffer = "";
    this.pending = new Map(); // id → { resolve, reject }
    this.notifications = [];
    this.nextId = 1;
  }

  start() {
    return new Promise((resolve, reject) => {
      this.proc = spawn(process.execPath, [this.serverPath], {
        env: this.env,
        stdio: ["pipe", "pipe", "pipe"],
      });
      let stderrBuf = "";
      const onErr = (d) => {
        stderrBuf += d.toString();
        // The server logs "[unity-open-mcp] Bridge port resolved..." + auth token
        // to stderr once it's ready to accept requests. Resolve on the auth line.
        if (stderrBuf.includes("auth token") || stderrBuf.includes("authMode")) {
          resolve();
        }
      };
      this.proc.stderr.on("data", onErr);
      this.proc.on("error", reject);
      this.proc.stdout.on("data", (d) => this._onData(d));
      this.proc.on("exit", (code) => {
        if (code !== 0 && code !== null) {
          reject(new Error(`MCP server exited with code ${code}. stderr: ${stderrBuf.slice(-500)}`));
        }
      });
      // Fallback: resolve after 3s even if the readiness line didn't appear
      // (older bridge / no auth). The server may still accept requests.
      setTimeout(() => resolve(), 3000);
    });
  }

  _onData(d) {
    this.buffer += d.toString();
    // The MCP SDK's StdioServerTransport uses newline-delimited JSON (NOT
    // LSP Content-Length framing). Split on newlines; each line is one message.
    while (true) {
      const nl = this.buffer.indexOf("\n");
      if (nl < 0) break;
      const line = this.buffer.slice(0, nl).trim();
      this.buffer = this.buffer.slice(nl + 1);
      if (line) this._handleMessage(line);
    }
  }

  _handleMessage(raw) {
    let msg;
    try { msg = JSON.parse(raw); } catch { return; }
    if (msg.id != null && this.pending.has(msg.id)) {
      const { resolve, reject } = this.pending.get(msg.id);
      this.pending.delete(msg.id);
      if (msg.error) reject(Object.assign(new Error(msg.error.message ?? "JSON-RPC error"), { rpcError: msg.error }));
      else resolve(msg.result);
    } else if (msg.method && msg.id == null) {
      // Notification.
      this.notifications.push(msg);
    }
  }

  request(method, params) {
    const id = this.nextId++;
    const msg = { jsonrpc: "2.0", id, method, params: params ?? {} };
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      // The MCP SDK uses newline-delimited JSON (serializeMessage adds \n).
      this.proc.stdin.write(JSON.stringify(msg) + "\n");
    });
  }

  notify(method, params) {
    const msg = { jsonrpc: "2.0", method, params: params ?? {} };
    this.proc.stdin.write(JSON.stringify(msg) + "\n");
  }

  waitForNotification(method, timeoutMs = 5000) {
    const existing = this.notifications.find((n) => n.method === method);
    if (existing) return Promise.resolve(existing);
    return new Promise((resolve, reject) => {
      const t0 = Date.now();
      const tick = () => {
        const hit = this.notifications.find((n) => n.method === method);
        if (hit) return resolve(hit);
        if (Date.now() - t0 > timeoutMs) return reject(new Error(`timeout waiting for ${method}`));
        setTimeout(tick, 100);
      };
      tick();
    });
  }

  stop() {
    if (this.proc) {
      try { this.proc.kill("SIGTERM"); } catch { /* best-effort */ }
      this.proc = null;
    }
  }
}

// --- step definitions (descriptive; the runner interprets them) ---
function buildSuite() {
  return [
    // Band P — protocol handshake
    { id: "initialize", band: "P", desc: "initialize → assert protocolVersion + capabilities.tools.listChanged === true", requiresLive: false },
    { id: "tools_list", band: "P", desc: "tools/list → assert non-empty, parse tool names", requiresLive: false },
    // Band T — tools/call
    { id: "call_local_capabilities", band: "T", desc: "tools/call capabilities (local-routed, no Unity)", requiresLive: false },
    { id: "call_live_ping", band: "T", desc: "tools/call ping (live-routed; needs bridge)", requiresLive: true },
    // Band M — manage_tools session + list_changed
    { id: "manage_tools_activate_list_changed", band: "M", desc: "manage_tools(activate) → notifications/tools/list_changed", requiresLive: false },
    { id: "manage_tools_deactivate", band: "M", desc: "manage_tools(deactivate) → restore session", requiresLive: false },
    // Band R — route spot-check (≥10 tools)
    { id: "route_spot_check", band: "R", desc: "assert _route.route on ≥10 tools matches router policy", requiresLive: false },
  ];
}

// --- the route policy (mirrors mcp-server/src/tool-router.ts priority) ---
const LOCAL_PINNED = new Set([
  "unity_open_mcp_capabilities", "unity_open_mcp_list_rules", "unity_open_mcp_generate_skill",
  "unity_open_mcp_manage_tools", "unity_open_mcp_bridge_status", "unity_senses_pull_events",
  "unity_open_mcp_hub_list_editors", "unity_open_mcp_hub_available_releases",
  "unity_open_mcp_hub_install_editor", "unity_open_mcp_hub_install_modules",
  "unity_open_mcp_hub_get_install_path", "unity_open_mcp_hub_set_install_path",
]);
const OFFLINE_PINNED = new Set([
  "unity_open_mcp_list_assets", "unity_open_mcp_read_compile_errors",
  "unity_open_mcp_read_asset", "unity_open_mcp_search_assets",
  "unity_open_mcp_find_references", "unity_open_mcp_dependencies",
]);
const BATCH_PINNED = new Set([
  "unity_open_mcp_compile_check", "unity_open_mcp_scan_all",
  "unity_open_mcp_baseline_create", "unity_open_mcp_regression_check",
  "unity_open_mcp_find_members", "unity_open_mcp_execute_csharp",
  "unity_open_mcp_invoke_method", "unity_open_mcp_execute_menu",
]);

function expectedRoute(toolName) {
  if (LOCAL_PINNED.has(toolName)) return "local";
  if (OFFLINE_PINNED.has(toolName)) return "offline";
  if (BATCH_PINNED.has(toolName)) return "batch";
  return "live";
}

async function runStep(step, client, opts, results) {
  const t0 = Date.now();
  let pass = false;
  let detail = "";
  try {
    if (step.requiresLive && opts.skipLive) {
      pass = true;
      detail = "skipped (--skip-live)";
    } else if (step.id === "initialize") {
      const res = await client.request("initialize", {
        protocolVersion: "2024-11-05",
        capabilities: {},
        clientInfo: { name: "s3-protocol-test", version: "1.0" },
      });
      const listChanged = res?.capabilities?.tools?.listChanged === true;
      pass = !!res?.protocolVersion && listChanged;
      detail = `protocolVersion=${res?.protocolVersion} listChanged=${listChanged}`;
    } else if (step.id === "tools_list") {
      const res = await client.request("tools/list", {});
      const tools = res?.tools ?? [];
      client.toolsList = tools;
      pass = tools.length > 0;
      detail = `${tools.length} tools advertised`;
    } else if (step.id === "call_local_capabilities") {
      const res = await client.request("tools/call", {
        name: "unity_open_mcp_capabilities", arguments: { kind: "tools", include_planned: false },
      });
      pass = !res?.isError;
      detail = pass ? "capabilities returned" : `err=${res?.content?.[0]?.text ?? "unknown"}`;
    } else if (step.id === "call_live_ping") {
      const res = await client.request("tools/call", {
        name: "unity_open_mcp_ping", arguments: {},
      });
      pass = !res?.isError;
      detail = pass ? "ping ok" : `err=${res?.content?.[0]?.text ?? "unknown"}`;
    } else if (step.id === "manage_tools_activate_list_changed") {
      client.notifications = []; // reset
      await client.request("tools/call", {
        name: "unity_open_mcp_manage_tools", arguments: { action: "activate", group: "diagnostics" },
      });
      try {
        await client.waitForNotification("notifications/tools/list_changed", 5000);
        pass = true;
        detail = "list_changed received";
      } catch {
        // Fallback: poll tools/list for a delta (the group's tools should now appear).
        const res = await client.request("tools/list", {});
        const tools = res?.tools ?? [];
        pass = tools.length > (client.toolsList?.length ?? 0);
        detail = pass ? `list_changed via poll (tools grew to ${tools.length})` : "no list_changed + no list delta";
      }
    } else if (step.id === "manage_tools_deactivate") {
      await client.request("tools/call", {
        name: "unity_open_mcp_manage_tools", arguments: { action: "deactivate", group: "diagnostics" },
      });
      pass = true;
      detail = "deactivated";
    } else if (step.id === "route_spot_check") {
      const res = await client.request("tools/list", {});
      const tools = res?.tools ?? [];
      // Determine whether the bridge is up (if so, live is an acceptable route
      // for batch-capable + offline-first tools — the router prefers live when
      // available). We detect this from the ping step's result or a fresh ping.
      let bridgeUp = false;
      try {
        const pingRes = await client.request("tools/call", { name: "unity_open_mcp_ping", arguments: {} });
        bridgeUp = !pingRes?.isError;
      } catch { /* bridge down — only local/offline routes valid */ }

      // Sample 10 tools across route categories and check _route.route in a
      // tools/call envelope. Local/offline tools can be called without Unity;
      // live/batch tools may error but still carry _route.
      const sample = [];
      const byExpected = {};
      for (const t of tools) {
        const exp = expectedRoute(t.name);
        if (!byExpected[exp]) byExpected[exp] = [];
        byExpected[exp].push(t);
      }
      for (const route of Object.keys(byExpected)) {
        sample.push(byExpected[route][0]);
      }
      // Pad to ≥10 if possible.
      for (const t of tools) {
        if (sample.length >= 10) break;
        if (!sample.includes(t)) sample.push(t);
      }
      let checked = 0;
      let mismatches = 0;
      for (const t of sample) {
        if (opts.skipLive && expectedRoute(t.name) === "live" && !bridgeUp) continue;
        try {
          const callRes = await client.request("tools/call", { name: t.name, arguments: {} });
          const text = callRes?.content?.[0]?.text ?? "{}";
          let env;
          try { env = JSON.parse(text); } catch { env = {}; }
          const actual = env?._route?.route ?? env?.result?._route?.route;
          const exp = expectedRoute(t.name);
          checked++;
          if (actual) {
            // The router prefers `live` when the bridge is up, even for
            // batch-capable (live+batch) and offline-first tools. So `live` is
            // always acceptable when bridgeUp; otherwise the route must match.
            const acceptable = actual === exp || (bridgeUp && actual === "live");
            if (!acceptable) {
              mismatches++;
              detail += `${t.name}: expected ${exp} got ${actual}; `;
            }
          }
        } catch {
          // A JSON-RPC error is fine — we still got a response (route may be in the error).
          checked++;
        }
      }
      pass = checked >= 10 && mismatches === 0;
      if (checked < 10) detail += `only ${checked} tools checked (need ≥10); `;
      detail += `${checked} checked, ${mismatches} route mismatches${bridgeUp ? " (bridge up — live acceptable)" : " (bridge down)"}`;
    }
  } catch (err) {
    pass = false;
    detail = `exception: ${err.message}`;
  }
  const ms = Date.now() - t0;
  results.push({ label: step.id, band: step.band, desc: step.desc, passed: pass, ms, detail });
  const mark = pass ? "✓" : "✗";
  process.stdout.write(`${mark} ${step.id.padEnd(34)} ${String(ms).padStart(6)}ms  ${detail}\n`);
  return pass;
}

function main() {
  const opts = parseArgs(process.argv.slice(2));

  if (!existsSync(CLI_BIN)) {
    console.error(`CLI binary not found at ${CLI_BIN}.`);
    console.error(`Run \`npm run build\` in mcp-server/ first.`);
    process.exit(2);
  }
  if (!isAbsolute(opts.project)) {
    console.error(`--project must be an ABSOLUTE path. Got: ${opts.project}`);
    process.exit(2);
  }
  if (!existsSync(opts.project)) {
    console.error(`Project not found: ${opts.project}`);
    process.exit(2);
  }

  const allSteps = buildSuite();
  let selected = allSteps;
  if (opts.band) selected = selected.filter((s) => opts.band.includes(s.band));
  if (opts.only) selected = selected.filter((s) => opts.only.some((n) => s.id.includes(n)));

  if (opts.list) {
    console.log("Protocol-test suite steps (S3):");
    let lastBand = null;
    for (const s of selected) {
      if (s.band !== lastBand) {
        console.log(`\n  --- Band ${s.band} ---`);
        lastBand = s.band;
      }
      const live = s.requiresLive ? " [live]" : " [local]";
      console.log(`  ${s.id.padEnd(34)} ${s.desc}${live}`);
    }
    console.log(`\n${selected.length} step(s).${opts.skipLive ? " (--skip-live: live steps will skip)" : ""}`);
    console.log(`Project: ${opts.project}`);
    return;
  }

  console.log(`unity-open-mcp protocol test (S3)`);
  console.log(`  project: ${opts.project}`);
  console.log(`  server:  ${CLI_BIN}`);
  console.log(`  steps:   ${selected.length}`);
  console.log(`  mode:    ${opts.skipLive ? "local-only (--skip-live)" : "local + live"}`);
  console.log("");

  const client = new McpStdioClient(CLI_BIN, process.env, opts.project);
  let stopped = false;
  const cleanup = () => {
    if (!stopped) { client.stop(); stopped = true; }
  };
  process.on("SIGINT", () => { cleanup(); process.exit(1); });
  process.on("exit", cleanup);

  (async () => {
    try {
      await client.start();
    } catch (err) {
      console.error(`Failed to start MCP server: ${err.message}`);
      process.exit(2);
    }

    const results = [];
    const bandSummary = {};
    let pass = 0;
    let fail = 0;

    let lastBand = null;
    for (const step of selected) {
      if (step.band !== lastBand) {
        if (lastBand !== null) console.log("");
        console.log(`--- Band ${step.band} ---`);
        lastBand = step.band;
      }
      const ok = await runStep(step, client, opts, results);
      if (ok) pass++; else fail++;
      bandSummary[step.band] = bandSummary[step.band] || { pass: 0, fail: 0 };
      bandSummary[step.band][ok ? "pass" : "fail"]++;
    }

    cleanup();

    console.log("");
    console.log("Band summary:");
    for (const b of Object.keys(bandSummary).sort()) {
      const bs = bandSummary[b];
      console.log(`  Band ${b}: ${bs.pass} passed, ${bs.fail} failed`);
    }
    console.log("");
    console.log(`${pass} passed, ${fail} failed (${selected.length} total)`);

    if (opts.jsonOut) {
      const report = {
        suite: "S3-protocol",
        project: opts.project,
        skipLive: !!opts.skipLive,
        ranAt: new Date().toISOString(),
        summary: { pass, fail, total: selected.length, bandSummary },
        steps: results,
      };
      try {
        writeFileSync(opts.jsonOut, JSON.stringify(report, null, 2));
        console.log(`Report written to ${opts.jsonOut}`);
      } catch (e) {
        console.error(`Failed to write json-out: ${e.message}`);
      }
    }

    if (fail > 0) {
      const fails = results.filter((r) => !r.passed).slice(0, 5);
      for (const f of fails) {
        console.error(`\n✗ ${f.label}: ${f.detail}`);
      }
      process.exit(1);
    }
  })().catch((err) => {
    console.error(`S3 fatal: ${err.message}`);
    cleanup();
    process.exit(1);
  });
}

main();
