#!/usr/bin/env node
// mcp-sandbox.mjs — S5: disposable-project test suite.
//
// Package and Hub mutators would destroy the shared demo/ used by S0/S1. S5
// copies the demo (or a source project) to a temp dir and runs the destructive
// lifecycle there — the temp clone is ALWAYS deleted at the end (SIGINT-safe).
//
// Coverage (the 3 S0 SKIPS + hub mutators + destructive build):
//   Band L — package lifecycle: package_add (small test package) → package_remove
//   Band R — reimport_package on a non-critical package (raised timeout)
//   Band H — hub mutators: hub_set_install_path, hub_install_editor,
//            hub_install_modules — skip-with-reason when Hub CLI unavailable
//   Band B — build_start with explicit confirmation in the sandbox only
//
// The sandbox is a full Unity project clone. The Editor must NOT be open on the
// sandbox (S5 opens it via batch, or assumes a separate Editor instance). To
// avoid port collisions with a live demo Editor, S5 typically runs with the
// demo Editor closed and drives the sandbox via batch/headless tools.
//
// Usage:
//   node scripts/mcp-sandbox.mjs                            # clone ./demo → temp
//   node scripts/mcp-sandbox.mjs --source-project /path     # clone a different source
//   node scripts/mcp-sandbox.mjs --project /existing/clone  # use an existing clone (no copy)
//   node scripts/mcp-sandbox.mjs --keep-sandbox             # don't delete temp (debug)
//   node scripts/mcp-sandbox.mjs --list                     # list steps, don't run
//   node scripts/mcp-sandbox.mjs --json-out report.json
//
// Requirements: mcp-server/dist/index.js built. A Unity Editor may be open on
// the SOURCE project (for live reads) but NOT on the sandbox clone. Hub CLI
// steps skip gracefully when the Hub is not installed (CI without Hub).

import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { isAbsolute, join, basename, dirname } from "node:path";
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
  printStepList,
} from "./mcp-test-lib.mjs";

function printHelp() {
  console.error(`Usage: node scripts/mcp-sandbox.mjs [options]

Options:
  --source-project, -s <path>  Source project to clone (default: <repo>/demo).
                               MUST be absolute.
  --project, -P <path>         Use an existing clone (skip the copy step).
                               MUST be absolute.
  --keep-sandbox               Don't delete the temp clone at the end (debug).
  --band L,R,H,B               Run only bands L=package-lifecycle,
                               R=reimport-package, H=hub-mutators,
                               B=destructive-build
  --only needle                Run only steps whose label contains any needle
  --list                       List the suite steps and exit (no execution)
  --json-out <file>            Write a machine-readable report to <file>
  --timeout-ms <n>             Per-step timeout (default 300000 — package ops are slow)
  -h, --help                   Show this help

The CLI binary must exist at mcp-server/dist/index.js. The source project is
cloned to a temp dir and deleted at the end (SIGINT-safe). Hub steps skip
gracefully when the Hub CLI is unavailable.`);
}

function parseArgs(argv) {
  let sourceProject = null;
  let keepSandbox = false;
  const opts = parseCommonArgs(argv, {
    help: printHelp,
    defaults: { timeoutMs: 300_000 },
    extra: (a, o, av, i) => {
      if (a === "--source-project" || a === "-s") { sourceProject = av[++i]; return i; }
      if (a === "--keep-sandbox") { keepSandbox = true; return i; }
      console.error(`Unknown argument: ${a}`);
      process.exit(2);
    },
  });
  opts.sourceProject = sourceProject ?? join(REPO_ROOT, "demo");
  opts.keepSandbox = keepSandbox;
  return opts;
}

function buildSuite() {
  const { s, steps } = makeStepBuilder();

  // =====================================================================
  // BAND L — package lifecycle (the 2 of 3 S0 SKIPS)
  // =====================================================================
  // package_add: install a small, harmless test package. com.unity.textmeshpro
  // is a safe choice (already a transitive dep in many projects; idempotent).
  // Tolerate package_already_installed (the clone may already have it).
  s("package_add", "L", "unity_open_mcp_package_add", {
    package_id: "com.unity.textmeshpro",
    paths_hint: ["Packages/manifest.json"],
  }, {
    gate: true, expect: "tolerate",
    tolerate: ["package_already_installed", "network_error", "resolution_error"],
    timeoutMs: 180_000,
    after: (r, ctx) => { ctx.packageAdded = r.mutation?.success === true; },
  });

  // package_remove: remove the package we just added (or that was already there).
  // Tolerate package_not_installed (if the add was skipped/no-op).
  s("package_remove", "L", "unity_open_mcp_package_remove", {
    package_id: "com.unity.textmeshpro",
    paths_hint: ["Packages/manifest.json"],
  }, {
    gate: true, expect: "tolerate",
    tolerate: ["package_not_installed", "package_required", "resolution_error"],
    timeoutMs: 180_000,
  });

  // =====================================================================
  // BAND R — reimport_package (3rd S0 SKIP)
  // =====================================================================
  // Reimport a non-critical package. com.unity.textmeshpro is safe. If it's not
  // a local (file:-linked) package, reimport_package returns not_local_package —
  // that's an informative success (the route is exercised + the contract gap
  // surfaced). Tolerate it.
  s("reimport_package", "R", "unity_open_mcp_reimport_package", {
    package_id: "com.unity.textmeshpro",
  }, {
    expect: "tolerate",
    tolerate: ["not_local_package", "package_not_installed"],
    timeoutMs: 300_000,
  });

  // =====================================================================
  // BAND H — hub mutators (absent from S0)
  // =====================================================================
  // These need the Unity Hub CLI. In CI without Hub, they fail with
  // hub_not_found / hub_unavailable — tolerate so the suite stays green while
  // exercising the route + surfacing the env gap.
  s("hub_set_install_path", "H", "unity_open_mcp_hub_set_install_path", {
    install_path: "/Applications/Unity/Hub",
  }, {
    expect: "tolerate",
    tolerate: ["hub_not_found", "hub_unavailable", "install_path_invalid"],
  });
  s("hub_install_editor", "H", "unity_open_mcp_hub_install_editor", {
    version: "2022.3.20f1",
  }, {
    expect: "tolerate",
    tolerate: ["hub_not_found", "hub_unavailable", "version_not_found", "network_error"],
    timeoutMs: 300_000,
  });
  s("hub_install_modules", "H", "unity_open_mcp_hub_install_modules", {
    version: "2022.3.20f1", modules: ["ios"],
  }, {
    expect: "tolerate",
    tolerate: ["hub_not_found", "hub_unavailable", "version_not_found", "module_not_found"],
    timeoutMs: 300_000,
  });

  // =====================================================================
  // BAND B — destructive build (S0 uses refused path; S5 confirms in sandbox)
  // =====================================================================
  // build_start with explicit confirmation. In the sandbox, a real build is safe
  // (the clone is disposable). Use a fast target (StandaloneOSX) + the sandbox
  // project. Tolerate build_failed (compile errors in the clone) — the point is
  // that the build STARTED (build_confirmation_required means the deny heuristic
  // fired, which is a refusal, not a success).
  s("build_start_confirmed", "B", "unity_open_mcp_build_start", {
    target: "StandaloneOSX",
    paths_hint: ["ProjectSettings/ProjectSettings.asset"],
  }, {
    gate: true, expect: "tolerate",
    tolerate: ["build_failed", "build_target_not_found", "scene_not_found"],
    timeoutMs: 300_000,
  });

  return steps;
}

function main() {
  const opts = parseArgs(process.argv.slice(2));

  if (!existsSync(CLI_BIN)) {
    console.error(`CLI binary not found at ${CLI_BIN}.`);
    console.error(`Run \`npm run build\` in mcp-server/ first.`);
    process.exit(2);
  }
  if (!isAbsolute(opts.sourceProject)) {
    console.error(`--source-project must be an ABSOLUTE path. Got: ${opts.sourceProject}`);
    process.exit(2);
  }
  if (!existsSync(opts.sourceProject)) {
    console.error(`Source project not found: ${opts.sourceProject}`);
    process.exit(2);
  }

  const allSteps = buildSuite();
  let selected = allSteps;
  if (opts.band) selected = selected.filter((s) => opts.band.includes(s.band));
  if (opts.only) selected = selected.filter((s) => opts.only.some((n) => s.label.includes(n)));

  if (opts.list) {
    printStepList(selected, { suiteName: "Sandbox-test suite steps (S5)" });
    return;
  }

  // Determine the sandbox path: use --project if provided (existing clone),
  // otherwise clone the source to a temp dir.
  let sandboxPath = opts.project;
  let clonedSandbox = false;
  if (sandboxPath && sandboxPath !== join(REPO_ROOT, "demo")) {
    // --project given + not the default demo → assume it's an existing clone.
    if (!isAbsolute(sandboxPath)) {
      console.error(`--project must be an ABSOLUTE path. Got: ${sandboxPath}`);
      process.exit(2);
    }
    if (!existsSync(sandboxPath)) {
      console.error(`Sandbox project not found: ${sandboxPath}`);
      process.exit(2);
    }
  } else {
    // Clone the source to a temp dir.
    const tempBase = mkdtempSync(join(tmpdir(), "unity-open-mcp-sandbox-"));
    sandboxPath = join(tempBase, basename(opts.sourceProject));
    console.log(`unity-open-mcp sandbox test (S5)`);
    console.log(`  source:  ${opts.sourceProject}`);
    console.log(`  sandbox: ${sandboxPath}`);
    console.log(`  cli:     ${CLI_BIN}`);
    console.log(`  steps:   ${selected.length}`);
    console.log("");
    console.log("--- clone source → sandbox ---");
    try {
      // Copy the project tree, excluding Library/ (huge) + Temp/ + obj/ (stale).
      cpSync(opts.sourceProject, sandboxPath, {
        recursive: true,
        filter: (src) => {
          const rel = src.slice(opts.sourceProject.length).replace(/^\//, "");
          if (rel === "Library" || rel.startsWith("Library/")) return false;
          if (rel === "Temp" || rel.startsWith("Temp/")) return false;
          if (rel === "obj" || rel.startsWith("obj/")) return false;
          if (rel === ".git" || rel.startsWith(".git/")) return false;
          return true;
        },
      });
      clonedSandbox = true;
      process.stdout.write(`✓ ${"cloned source → sandbox".padEnd(34)}        ${sandboxPath}\n\n`);
    } catch (e) {
      console.error(`Failed to clone source to sandbox: ${e.message}`);
      try { rmSync(tempBase, { recursive: true, force: true }); } catch { /* best-effort */ }
      process.exit(2);
    }
  }

  if (!clonedSandbox) {
    console.log(`unity-open-mcp sandbox test (S5)`);
    console.log(`  sandbox: ${sandboxPath} (existing clone)`);
    console.log(`  cli:     ${CLI_BIN}`);
    console.log(`  steps:   ${selected.length}`);
    console.log("");
  }

  const runEnv = buildRunEnv();
  const ctx = {};
  const results = [];
  const bandSummary = {};
  let pass = 0;
  let fail = 0;

  // SIGINT: always delete the temp clone (if we created it) unless --keep-sandbox.
  const onSigInt = () => {
    console.error("\n^C — interrupted; deleting sandbox before exit...");
    if (clonedSandbox && !opts.keepSandbox) {
      try { rmSync(dirname(sandboxPath), { recursive: true, force: true }); } catch { /* best-effort */ }
    }
    process.exit(1);
  };
  process.on("SIGINT", onSigInt);

  let lastBand = null;
  for (const step of selected) {
    if (step.band !== lastBand) {
      if (lastBand !== null) console.log("");
      console.log(`--- Band ${step.band} ---`);
      lastBand = step.band;
    }
    const out = runToolLib(step, ctx, sandboxPath, opts.timeoutMs, runEnv);
    if (out.ok) pass++; else fail++;
    bandSummary[step.band] = bandSummary[step.band] || { pass: 0, fail: 0 };
    bandSummary[step.band][out.ok ? "pass" : "fail"]++;
    results.push({ label: step.label, band: step.band, tool: step.tool, expect: step.expect ?? "ok", passed: out.ok, ms: out.ms, detail: out.detail ?? out.error ?? "" });
    if (step.after) {
      try { step.after(out.result ?? {}, ctx); } catch { /* best-effort */ }
    }
    const mark = out.ok ? "✓" : "✗";
    const ms = String(out.ms).padStart(6);
    process.stdout.write(`${mark} ${step.label.padEnd(34)} ${ms}ms  ${out.detail ?? out.error}\n`);
  }

  process.removeListener("SIGINT", onSigInt);

  // Teardown: delete the temp clone (always, unless --keep-sandbox).
  console.log("");
  console.log("--- teardown ---");
  if (clonedSandbox && !opts.keepSandbox) {
    try {
      rmSync(dirname(sandboxPath), { recursive: true, force: true });
      process.stdout.write(`✓ ${"sandbox deleted".padEnd(34)}        ${dirname(sandboxPath)} gone\n`);
    } catch (e) {
      process.stdout.write(`✗ sandbox delete failed: ${e.message}\n`);
    }
  } else if (opts.keepSandbox) {
    process.stdout.write(`⊘ ${"sandbox kept (--keep-sandbox)".padEnd(34)}        ${sandboxPath}\n`);
  } else {
    process.stdout.write(`⊘ ${"sandbox was an existing clone (not deleted)".padEnd(34)}        ${sandboxPath}\n`);
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
      suite: "S5-sandbox",
      sourceProject: opts.sourceProject,
      sandboxPath,
      cloned: clonedSandbox,
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
