#!/usr/bin/env node
// mcp-headless.mjs — S2: headless (Editor-closed) MCP test suite.
//
// Proves the batch/offline paths work when no Unity Editor is open on the
// target project. S0 Band D only proves these tools return `locked` when the
// Editor holds the project; S2 proves they actually RUN headless.
//
// Preflight: asserts the bridge is offline / Editor does not have the project
// open. Refuses to run if the Editor is up (that's S0/S1's job) — clear error.
//
// Coverage:
//   Band D — batch meta-tools (must succeed): compile_check, scan_all,
//            baseline_create, regression_check. Baseline written to OS temp,
//            deleted in cleanup.
//   Band O — offline reads (no bridge): list_assets, read_asset,
//            find_references, read_compile_errors on known demo assets.
//   Band B — batch-capable fallbacks: find_members, execute_csharp,
//            invoke_method, execute_menu via batch spawn (the 8 BATCH_TOOL_NAMES).
//
// Unity -batchmode lock: CI must quit the Editor before running S2. A live
// Editor holding the project makes batch tools return editor_instance_locked
// instead of running — S2 treats that as a hard failure (not locked/tolerate).
//
// Usage:
//   node scripts/mcp-headless.mjs                       # full suite vs ./demo
//   node scripts/mcp-headless.mjs --project /path       # target another project
//   node scripts/mcp-headless.mjs --band D,O,B          # run named bands only
//   node scripts/mcp-headless.mjs --only needle         # subset by label
//   node scripts/mcp-headless.mjs --list                # list steps, don't run
//   node scripts/mcp-headless.mjs --json-out report.json
//
// Requirements: mcp-server/dist/index.js built; Unity Editor CLOSED (no bridge
// running on the target project). Pass --project as an ABSOLUTE path.

import { existsSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { isAbsolute, join } from "node:path";
import { tmpdir } from "node:os";

import {
  REPO_ROOT,
  CLI_BIN,
  classify,
  pluck,
  parseCommonArgs,
  makeStepBuilder,
  buildRunEnv,
  invokeTool,
  runTool as runToolLib,
  parseEnvelope,
  printStepList,
} from "./mcp-test-lib.mjs";

// Demo asset references for offline reads.
const DEMO_PREFAB = "Assets/Prefabs/GateTestCube.prefab";
const DEMO_MATERIAL = "Assets/Materials/TestMaterial.mat";

function printHelp() {
  console.error(`Usage: node scripts/mcp-headless.mjs [options]

Options:
  --project, -P <path>   Target Unity project (default: <repo>/demo). MUST be absolute.
  --band D,O,B           Run only bands D=batch-meta, O=offline-reads,
                         B=batch-capable-fallbacks
  --only needle          Run only steps whose label contains any needle
  --list                 List the suite steps and exit (no execution)
  --no-cleanup           Leave temp baseline on disk (debug)
  --json-out <file>      Write a machine-readable report to <file>
  --timeout-ms <n>       Per-step timeout (default 240000 — batch spawn is slow)
  -h, --help             Show this help

The CLI binary must exist at mcp-server/dist/index.js. A Unity Editor must NOT
be open on the target project — S2 proves the headless/batch path. If the
Editor is up, S2 refuses with an actionable message (run S0/S1 instead).`);
}

function parseArgs(argv) {
  return parseCommonArgs(argv, {
    help: printHelp,
    defaults: { timeoutMs: 240_000 },
  });
}

function buildSuite() {
  const { s, steps } = makeStepBuilder();

  // =====================================================================
  // BAND D — batch meta-tools (must succeed; NOT locked-tolerate like S0)
  // =====================================================================
  // baseline_path is resolved to a temp dir at runtime via resolveArgs so the
  // suite never writes into the project tree.
  s("compile_check", "D", "unity_open_mcp_compile_check", {}, {
    expect: "ok", timeoutMs: 300_000,
  });
  s("scan_all", "D", "unity_open_mcp_scan_all", {}, {
    expect: "ok", timeoutMs: 300_000,
  });
  s("baseline_create", "D", "unity_open_mcp_baseline_create", {
    resolveArgs: (ctx) => ({ baseline_path: join(ctx.tempDir, "s2-baseline.json") }),
  }, {
    expect: "ok", timeoutMs: 300_000,
    after: (r, ctx) => { ctx.baselineCreated = true; },
  });
  s("regression_check", "D", "unity_open_mcp_regression_check", {
    resolveArgs: (ctx) => ({ baseline_path: join(ctx.tempDir, "s2-baseline.json") }),
  }, {
    expect: "ok", timeoutMs: 300_000,
  });

  // =====================================================================
  // BAND O — offline reads (no bridge; local YAML/GUID parsers)
  // =====================================================================
  s("list_assets_offline", "O", "unity_open_mcp_list_assets", {
    folder: "Assets", max_per_folder: 10,
  });
  s("read_asset_offline", "O", "unity_open_mcp_read_asset", {
    asset_path: DEMO_PREFAB, profile: "compact",
  });
  s("find_references_offline", "O", "unity_open_mcp_find_references", {
    asset_path: DEMO_MATERIAL, profile: "compact",
  });
  s("read_compile_errors_offline", "O", "unity_open_mcp_read_compile_errors");

  // =====================================================================
  // BAND B — batch-capable fallbacks (the 8 BATCH_TOOL_NAMES, minus the 4
  // batch-meta tools already in Band D). These route to batch when the bridge
  // is down; with the Editor closed they spawn a headless Unity process.
  // =====================================================================
  s("find_members_batch", "B", "unity_open_mcp_find_members", {
    query: "Transform", max_results: 5,
  }, { expect: "ok", timeoutMs: 300_000 });
  s("execute_csharp_batch", "B", "unity_open_mcp_execute_csharp", {
    code: "return 42;", paths_hint: ["Assets/"],
  }, { gate: true, expect: "gate", timeoutMs: 300_000 });
  s("invoke_method_batch", "B", "unity_open_mcp_invoke_method", {
    type_name: "UnityEngine.Time", method_name: "get_time", is_static: true, paths_hint: ["Assets/"],
  }, { gate: true, expect: "gate", timeoutMs: 300_000 });
  s("execute_menu_batch", "B", "unity_open_mcp_execute_menu", {
    menu_path: "Assets/Refresh", paths_hint: ["Assets/"],
  }, { gate: true, expect: "gate", timeoutMs: 300_000 });

  return steps;
}

// A headless-specific classifier: `locked` codes are HARD failures here (the
// whole point of S2 is that the Editor is closed — a locked code means the
// Editor is actually open, which is an environment error, not a pass).
function classifyHeadless(step, parsed) {
  const isError = parsed.isError === true;
  const result = parsed.result ?? {};
  const errCode = result.error?.code ?? result.mutation?.error?.code;
  if (isError && (errCode === "editor_instance_locked" || errCode === "project_path_missing")) {
    return {
      pass: false,
      detail: `${errCode} — Editor appears to be open on the project. Quit Unity and re-run S2.`,
    };
  }
  return classify(step, parsed);
}

function main() {
  const opts = parseArgs(process.argv.slice(2));

  if (!existsSync(CLI_BIN)) {
    console.error(`CLI binary not found at ${CLI_BIN}.`);
    console.error(`Run \`npm run build\` in mcp-server/ first.`);
    process.exit(2);
  }
  if (!isAbsolute(opts.project)) {
    console.error(`--project must be an ABSOLUTE path (relative paths hash to a different bridge port). Got: ${opts.project}`);
    process.exit(2);
  }
  if (!existsSync(opts.project)) {
    console.error(`Project not found: ${opts.project}`);
    process.exit(2);
  }

  const allSteps = buildSuite();
  let selected = allSteps;
  if (opts.band) selected = selected.filter((s) => opts.band.includes(s.band));
  if (opts.only) selected = selected.filter((s) => opts.only.some((n) => s.label.includes(n)));

  if (opts.list) {
    printStepList(selected, { suiteName: "Headless-test suite steps (S2)" });
    console.log(`Project: ${opts.project}`);
    return;
  }

  const runEnv = buildRunEnv();

  // Preflight: assert the bridge is offline. If the Editor is open on the
  // project, the bridge responds to ping — refuse with an actionable message.
  console.log("--- preflight: assert Editor is closed ---");
  const ping = invokeTool(opts.project, "unity_open_mcp_ping", {}, 8_000, runEnv);
  if (ping && ping.isError !== true) {
    console.error("ERROR: bridge is reachable — the Unity Editor appears to be open on this project.");
    console.error("S2 proves the headless/batch path. Quit Unity (or close the project) and re-run.");
    console.error("For live-Editor coverage, run S0 (mcp-full-test.mjs) or S1 (mcp-behavior.mjs) instead.");
    process.exit(2);
  }
  process.stdout.write(`✓ ${"bridge offline (Editor closed)".padEnd(34)}        ok\n\n`);

  // Temp dir for the baseline file (cleaned up on exit / SIGINT).
  const tempDir = mkdtempSync(join(tmpdir(), "unity-open-mcp-s2-"));
  const ctx = { tempDir, baselineCreated: false };

  const onSigInt = () => {
    console.error("\n^C — interrupted; cleaning up temp dir before exit...");
    if (!opts.noCleanup) {
      try { rmSync(tempDir, { recursive: true, force: true }); } catch { /* best-effort */ }
    }
    process.exit(1);
  };
  process.on("SIGINT", onSigInt);

  console.log(`unity-open-mcp headless test (S2)`);
  console.log(`  project: ${opts.project}`);
  console.log(`  cli:     ${CLI_BIN}`);
  console.log(`  temp:    ${tempDir}`);
  console.log(`  steps:   ${selected.length}`);
  console.log("");

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
    // Use runToolLib but with the headless classifier override. We can't inject
    // the classifier into runToolLib directly, so re-run the classification on
    // the result if it errored. Simpler: call runToolLib, then re-classify.
    const out = runToolLib(step, ctx, opts.project, opts.timeoutMs, runEnv);
    // Re-classify with the headless override (catches editor_instance_locked).
    if (out.raw && typeof out.raw === "object") {
      const reclassed = classifyHeadless(step, out.raw);
      if (!reclassed.pass && out.ok) {
        out.ok = false;
        out.detail = reclassed.detail;
      }
    }
    if (out.ok) pass++;
    else fail++;
    bandSummary[step.band] = bandSummary[step.band] || { pass: 0, fail: 0 };
    bandSummary[step.band][out.ok ? "pass" : "fail"]++;
    results.push({ label: step.label, band: step.band, tool: step.tool, expect: step.expect ?? "ok", passed: out.ok, ms: out.ms, detail: out.detail ?? out.error ?? "" });
    if (step.after) {
      try { step.after(out.result ?? {}, ctx); } catch { /* best-effort handoff */ }
    }
    const mark = out.ok ? "✓" : "✗";
    const ms = String(out.ms).padStart(6);
    process.stdout.write(`${mark} ${step.label.padEnd(34)} ${ms}ms  ${out.detail ?? out.error}\n`);
  }

  process.removeListener("SIGINT", onSigInt);

  // Cleanup: delete the temp baseline dir.
  if (!opts.noCleanup) {
    console.log("");
    console.log("--- cleanup ---");
    try {
      rmSync(tempDir, { recursive: true, force: true });
      process.stdout.write(`✓ ${"temp baseline cleanup".padEnd(34)}        ${tempDir} gone\n`);
    } catch (e) {
      process.stdout.write(`✗ temp baseline cleanup failed: ${e.message}\n`);
    }
  }

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
      suite: "S2-headless",
      project: opts.project,
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
      console.error(`\n✗ ${f.label} (${f.tool}, expect=${f.expect}): ${f.detail}`);
    }
    process.exit(1);
  }
}

main();
