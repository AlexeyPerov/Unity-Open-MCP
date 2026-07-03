#!/usr/bin/env node
// mcp-smoke.mjs — lightweight smoke test for the unity-open-mcp tool surface.
//
// Drives a configurable set of MCP tools against a live Unity project via the
// `unity-open-mcp run-tool` CLI (one fresh process per call). Each call is a
// fresh process, so this script is immune to any in-process connection/state
// issues in a long-lived MCP client — it exercises the same router stack an MCP
// client uses, end to end, with the same JSON an MCP client would receive.
//
// The default suite covers three bands:
//   1. Bridge & lifecycle health      (ping, editor_status, capabilities, ...)
//   2. Read-only family coverage      (list_assets, read_asset, asmdef, shader,
//                                      tags/layers, validate_edit, scene reads)
//   3. Safe mutations + cleanup       (create temp GameObject → duplicate →
//                                      modify → reparent → destroy; create temp
//                                      folder+material → delete) — fully cleaned
//                                      up, leaves the project untouched.
//
// Usage:
//   node scripts/mcp-smoke.mjs                              # default suite vs ./demo
//   node scripts/mcp-smoke.mjs --project /path/to/project   # target another project
//   node scripts/mcp-smoke.mjs --readonly                  # skip the mutation band
//   node scripts/mcp-smoke.mjs --list                      # list the suite, don't run
//   node scripts/mcp-smoke.mjs --only ping,editor_get_tags # run a subset by label
//
// Exit code: 0 if every step passed, 1 if any failed. A failure in one step
// does not abort the rest (independent steps keep running); cleanup always runs.
//
// Requirements: the unity-open-mcp CLI must be built (mcp-server/dist/index.js)
// and a Unity Editor must be open with the target project and the bridge running.
// The script does NOT spawn Unity — it fails fast with a clear message if the
// bridge is unreachable.

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, rmSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(__dirname, "..");
const CLI_BIN = resolve(REPO_ROOT, "mcp-server", "dist", "index.js");
const DEFAULT_PROJECT = resolve(REPO_ROOT, "demo");

// ---------------------------------------------------------------------------
// arg parsing
// ---------------------------------------------------------------------------

function parseArgs(argv) {
  const opts = {
    project: DEFAULT_PROJECT,
    readonly: false,
    list: false,
    only: null, // comma-separated labels, null = all
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--project" || a === "-P") opts.project = argv[++i];
    else if (a === "--readonly") opts.readonly = true;
    else if (a === "--list") opts.list = true;
    else if (a === "--only") opts.only = argv[++i].split(",").map((s) => s.trim());
    else if (a === "--help" || a === "-h") {
      printHelp();
      process.exit(0);
    } else {
      console.error(`Unknown argument: ${a}`);
      process.exit(2);
    }
  }
  return opts;
}

function printHelp() {
  console.error(`Usage: node scripts/mcp-smoke.mjs [options]

Options:
  --project, -P <path>   Target Unity project (default: <repo>/demo)
  --readonly             Skip the safe-mutation band (band 3)
  --only a,b,c           Run only steps whose label contains any of a,b,c
  --list                 List the suite steps and exit (no execution)
  -h, --help             Show this help

The CLI binary must exist at mcp-server/dist/index.js (run \`npm run build\` in
mcp-server/ first). A Unity Editor must be open with the project and the bridge
running.`);
}

// ---------------------------------------------------------------------------
// suite definition
//
// Each step is { label, tool, args?, gate? }. `gate: true` marks steps whose
// result is a mutation envelope (so pass/fail is derived from mutation.success
// + the gate delta); the default derives pass/fail from the JSON isError flag.
// A step may carry `setup` (runs before) / `cleanup` (always runs) hooks for
// the mutation band's temp-asset lifecycle.
// ---------------------------------------------------------------------------

const SMOKE_FOLDER = "Assets/MCP_Smoke";
const SMOKE_MAT = `${SMOKE_FOLDER}/SmokeTemp.mat`;

function buildSuite({ readonly }) {
  const steps = [
    // --- Band 1: bridge & lifecycle ---
    { label: "ping", tool: "unity_open_mcp_ping" },
    { label: "editor_status", tool: "unity_open_mcp_editor_status" },
    { label: "capabilities", tool: "unity_open_mcp_capabilities", args: { kind: "tools" } },
    { label: "list_rules", tool: "unity_open_mcp_list_rules" },
    {
      label: "manage_tools",
      tool: "unity_open_mcp_manage_tools",
      args: { action: "list_groups" },
    },

    // --- Band 2: read-only family coverage ---
    { label: "list_assets", tool: "unity_open_mcp_list_assets", args: { folder: "Assets", max_per_folder: 10 } },
    {
      label: "read_asset",
      tool: "unity_open_mcp_read_asset",
      args: { asset_path: "Assets/Prefabs/GateTestCube.prefab", profile: "compact" },
    },
    { label: "asmdef_list", tool: "unity_open_mcp_asmdef_list", args: { folder: "Assets" } },
    {
      label: "asmdef_get",
      tool: "unity_open_mcp_asmdef_get",
      args: { asset_path: "Assets/Tests/EditMode/Demo.Tests.EditMode.asmdef" },
    },
    { label: "shader_list_all", tool: "unity_open_mcp_shader_list_all", args: { max_results: 5 } },
    { label: "editor_get_tags", tool: "unity_open_mcp_editor_get_tags" },
    { label: "editor_get_layers", tool: "unity_open_mcp_editor_get_layers" },
    {
      label: "validate_edit",
      tool: "unity_open_mcp_validate_edit",
      args: { paths: ["Assets/Materials/TestMaterial.mat"], profile: "compact" },
    },
    {
      label: "scene_get_data",
      tool: "unity_open_mcp_scene_get_data",
      args: { profile: "compact" },
    },
    {
      label: "scene_get_dirty_summary",
      tool: "unity_open_mcp_scene_get_dirty_summary",
    },
  ];

  if (!readonly) {
    // --- Band 3: safe mutations + cleanup ---
    // GameObject lifecycle in the active (possibly unsaved) scene. paths_hint
    // uses a sentinel; the gate accepts any non-empty hint. The create step
    // stashes its instanceId on the step object so duplicate/modify/parent/
    // destroy can chain off it.
    const goHint = { paths_hint: ["<mcp-smoke>"] };
    const cubeStep = {
      label: "gameobject_create",
      tool: "unity_open_mcp_gameobject_create",
      args: { name: "MCP_Smoke_Cube", primitive_type: "Cube", position: "0,1,0", ...goHint },
      gate: true,
    };
    const dupStep = {
      label: "gameobject_duplicate",
      tool: "unity_open_mcp_gameobject_duplicate",
      gate: true,
      // args resolved at run time from cubeStep's result
      resolveArgs: (ctx) => ({ instance_id: ctx.parentId, ...goHint }),
    };
    const modStep = {
      label: "gameobject_modify",
      tool: "unity_open_mcp_gameobject_modify",
      gate: true,
      resolveArgs: (ctx) => ({
        instance_id: ctx.childId,
        name: "MCP_Smoke_Renamed",
        position: "2,1,0",
        ...goHint,
      }),
    };
    const parentStep = {
      label: "gameobject_set_parent",
      tool: "unity_open_mcp_gameobject_set_parent",
      gate: true,
      resolveArgs: (ctx) => ({
        instance_id: ctx.childId,
        parent_instance_id: ctx.parentId,
        ...goHint,
      }),
    };
    const destroyStep = {
      label: "gameobject_destroy",
      tool: "unity_open_mcp_gameobject_destroy",
      gate: true,
      resolveArgs: (ctx) => ({ instance_id: ctx.parentId, ...goHint }),
    };
    // Wire instanceId handoff: create stores parentId; duplicate stores childId.
    // For gate tools the instanceId lives at mutation.output.instanceId.
    cubeStep.after = (result, ctx) => {
      ctx.parentId =
        pluck(result, "mutation.output.instanceId") ??
        pluck(result, "output.instanceId") ??
        pluck(result, "instanceId");
    };
    dupStep.after = (result, ctx) => {
      ctx.childId =
        pluck(result, "mutation.output.instanceId") ??
        pluck(result, "output.instanceId") ??
        pluck(result, "instanceId");
    };

    steps.push(cubeStep, dupStep, modStep, parentStep, destroyStep);

    // Asset lifecycle: temp folder + material, created then deleted.
    steps.push(
      {
        label: "assets_create_folder",
        tool: "unity_open_mcp_assets_create_folder",
        args: { folders: [{ parent_folder_path: "Assets", new_folder_name: "MCP_Smoke" }], paths_hint: [SMOKE_FOLDER] },
        gate: true,
      },
      {
        label: "material_create",
        tool: "unity_open_mcp_material_create",
        args: { asset_path: SMOKE_MAT, shader_name: "Universal Render Pipeline/Lit", paths_hint: [SMOKE_MAT] },
        gate: true,
      },
      {
        label: "assets_delete",
        tool: "unity_open_mcp_assets_delete",
        args: { paths: [SMOKE_FOLDER], paths_hint: [SMOKE_FOLDER] },
        gate: true,
      },
    );
  }

  return steps;
}

// Dig into a nested object by dotted path, e.g. "output.instanceId".
function pluck(obj, path) {
  return path.split(".").reduce((acc, k) => (acc && typeof acc === "object" ? acc[k] : undefined), obj);
}

// ---------------------------------------------------------------------------
// CLI runner
// ---------------------------------------------------------------------------

function runTool(step, ctx, project) {
  const args = step.resolveArgs ? step.resolveArgs(ctx) : step.args ?? {};
  const argStr = JSON.stringify(args);
  const t0 = Date.now();
  let stdout;
  try {
    // Suppress the CLI's informational stderr (port resolution, route logging)
    // so the table stays clean. JSON goes to stdout.
    stdout = execFileSync(
      process.execPath,
      [CLI_BIN, "run-tool", step.tool, "--project", project, "--json", "--args", argStr],
      { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], maxBuffer: 64 * 1024 * 1024, timeout: 90_000 },
    );
  } catch (err) {
    return { ok: false, ms: Date.now() - t0, error: `CLI failed: ${err.message}`, raw: "" };
  }
  const ms = Date.now() - t0;

  // With --json, the CLI prints ONLY the JSON envelope to stdout (informational
  // port/route lines go to stderr, which is ignored above). So stdout parses
  // directly. Fall back to a brace-matched extraction if anything precedes the
  // JSON (defensive — should not happen with the current CLI).
  let parsed = null;
  try {
    parsed = JSON.parse(stdout);
  } catch {
    const start = stdout.indexOf("{");
    if (start >= 0) {
      // Find the matching closing brace for the outermost object.
      let depth = 0;
      let end = -1;
      for (let i = start; i < stdout.length; i++) {
        if (stdout[i] === "{") depth++;
        else if (stdout[i] === "}") {
          depth--;
          if (depth === 0) {
            end = i;
            break;
          }
        }
      }
      if (end > start) {
        try {
          parsed = JSON.parse(stdout.slice(start, end + 1));
        } catch {
          // fall through to error
        }
      }
    }
  }
  if (!parsed) {
    return { ok: false, ms, error: "no JSON envelope in CLI stdout", raw: stdout.slice(-400) };
  }

  const isError = parsed.isError === true;
  const result = parsed.result ?? {};
  let detail = "";
  let ok = !isError;

  if (step.gate && result.mutation) {
    // Mutation envelope: success is mutation.success AND no gate delta errors.
    const mutSuccess = result.mutation?.success === true;
    const newErrors = result.gate?.delta?.newErrors;
    const gateClean = typeof newErrors !== "number" || newErrors === 0;
    ok = mutSuccess && gateClean;
    detail = `mutation.success=${mutSuccess} gate.newErrors=${newErrors ?? "n/a"}`;
  } else if (isError) {
    detail = result.error?.code ? `error: ${result.error.code}` : "isError=true";
  } else {
    // Direct body: surface a tiny identifying field if present.
    const count = pluck(result, "count");
    const status = pluck(result, "status");
    detail = [status ? `status=${status}` : null, typeof count === "number" ? `count=${count}` : null]
      .filter(Boolean)
      .join(" ") || "ok";
  }

  return { ok, ms, detail, result, raw: parsed };
}

// ---------------------------------------------------------------------------
// main
// ---------------------------------------------------------------------------

function main() {
  const opts = parseArgs(process.argv.slice(2));

  if (!existsSync(CLI_BIN)) {
    console.error(`CLI binary not found at ${CLI_BIN}.`);
    console.error(`Run \`npm run build\` in mcp-server/ first.`);
    process.exit(2);
  }
  if (!existsSync(opts.project)) {
    console.error(`Project not found: ${opts.project}`);
    process.exit(2);
  }

  const steps = buildSuite(opts);
  const selected = opts.only
    ? steps.filter((s) => opts.only.some((needle) => s.label.includes(needle)))
    : steps;

  if (opts.list) {
    console.log("Smoke suite steps:");
    for (const s of selected) {
      console.log(`  ${s.label.padEnd(28)} ${s.tool}${s.gate ? "  [gate]" : ""}`);
    }
    console.log(`\n${selected.length} step(s). Project: ${opts.project}`);
    return;
  }

  console.log(`unity-open-mcp smoke test`);
  console.log(`  project: ${opts.project}`);
  console.log(`  cli:     ${CLI_BIN}`);
  console.log(`  steps:   ${selected.length}${opts.readonly ? " (readonly)" : ""}`);
  console.log("");

  const ctx = {};
  const results = [];
  let pass = 0;
  let fail = 0;

  for (const step of selected) {
    const out = runTool(step, ctx, opts.project);
    if (out.ok) pass++;
    else fail++;
    results.push({ label: step.label, ...out });
    if (step.after) {
      try {
        step.after(out.result ?? {}, ctx);
      } catch {
        // best-effort handoff; downstream steps will fail loudly if missing
      }
    }
    const mark = out.ok ? "✓" : "✗";
    const ms = String(out.ms).padStart(5);
    process.stdout.write(`${mark} ${step.label.padEnd(28)} ${ms}ms  ${out.detail ?? out.error}\n`);
  }

  // Always attempt cleanup of the temp folder even if the delete step was
  // skipped/failed, so a half-run doesn't leave MCP_Smoke behind.
  if (!opts.readonly && !opts.only) {
    cleanupTempFolder(opts.project);
  }

  console.log("");
  console.log(`${pass} passed, ${fail} failed (${selected.length} total)`);

  if (fail > 0) {
    // Surface the first failure's raw envelope to aid debugging.
    const firstFail = results.find((r) => !r.ok);
    if (firstFail && firstFail.raw) {
      console.error(`\nFirst failure (${firstFail.label}) envelope:`);
      console.error(JSON.stringify(firstFail.raw, null, 2).slice(0, 1200));
    }
    process.exit(1);
  }
}

// Best-effort on-disk cleanup of Assets/MCP_Smoke if the delete step didn't run
// or failed. Uses the same CLI so Unity's AssetDatabase stays in sync.
function cleanupTempFolder(project) {
  const args = JSON.stringify({ paths: [SMOKE_FOLDER], paths_hint: [SMOKE_FOLDER] });
  try {
    execFileSync(
      process.execPath,
      [CLI_BIN, "run-tool", "unity_open_mcp_assets_delete", "--project", project, "--json", "--args", args],
      { encoding: "utf8", stdio: ["ignore", "ignore", "ignore"], timeout: 30_000 },
    );
  } catch {
    // Folder may already be gone (delete step succeeded) or never created.
    // Fall back to a raw filesystem removal so a stale folder can't linger.
    const onDisk = resolve(project, SMOKE_FOLDER);
    if (existsSync(onDisk)) {
      try {
        rmSync(onDisk, { recursive: true, force: true });
      } catch {
        // give up — reported via the table
      }
    }
  }
}

main();
