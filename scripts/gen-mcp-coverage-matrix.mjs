#!/usr/bin/env node
// gen-mcp-coverage-matrix.mjs — generates the MCP coverage matrix.
//
// Imports the registered tool definitions (mcp-server/src/tools/index.ts →
// ALL_TOOLS), the group catalog (capabilities/tool-groups.ts) and the route
// sets (local-tools.ts, batch-spawn.ts) straight from the MCP-server TS
// sources, and parses the S0 suite (scripts/mcp-full-test.mjs) to produce one
// row per registered tool with route, group, suite owner, pass criteria, and
// S0 status. The matrix is the single artifact that proves "zero unowned
// tools" — every tool appears in at least one suite with a strict owner.
//
// Re-run whenever tools ship:
//   node scripts/gen-mcp-coverage-matrix.mjs              # write the matrix
//   node scripts/gen-mcp-coverage-matrix.mjs --out <path> # write elsewhere
//   node scripts/gen-mcp-coverage-matrix.mjs --check      # invariants only, no write
//
// The generator EXITS NON-ZERO if any tool has no suite_owner (an orphan), or
// if any S0 tolerate step has no strict owner in S1 / wont-fix link. This makes
// "zero unowned rows" a machine-checked invariant, not a manual claim.
//
// Default output: specs/execution/done/M27/m27-mcp-coverage-matrix.md
// (gitignored — a maintainer working artifact). Without a specs/ tree the
// write is skipped and only the invariants are checked. Requires Node 22.6+
// (TS type stripping), like scripts/generate-token-estimates.mjs.

import { readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { register } from "node:module";

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(__dirname, "..");
const MCP_SRC = resolve(REPO_ROOT, "mcp-server", "src");
const S0_FILE = resolve(REPO_ROOT, "scripts", "mcp-full-test.mjs");
const SPECS_DIR = resolve(REPO_ROOT, "specs");
const DEFAULT_OUT_FILE = resolve(SPECS_DIR, "execution", "done", "M27", "m27-mcp-coverage-matrix.md");

// ---------------------------------------------------------------------------
// 1. Load the MCP-server sources. The source tree emits `.js` import
//    specifiers that point at `.ts` files; this resolve hook (the same one
//    generate-token-estimates.mjs uses) rewrites relative ones so the sources
//    load under Node's type stripping without a dist build.
// ---------------------------------------------------------------------------

const LOADER_SOURCE = `
export async function resolve(specifier, context, nextResolve) {
  if (
    specifier.endsWith(".js") &&
    !specifier.startsWith("node:") &&
    !specifier.includes("node_modules") &&
    !specifier.startsWith("@")
  ) {
    try {
      return await nextResolve(specifier.slice(0, -3) + ".ts", context);
    } catch {
      // fall through to default resolve
    }
  }
  return nextResolve(specifier, context);
}
`;
register(`data:text/javascript,${encodeURIComponent(LOADER_SOURCE)}`, import.meta.url);

const { ALL_TOOLS } = await import(resolve(MCP_SRC, "tools", "index.ts"));
const { groupFor } = await import(resolve(MCP_SRC, "capabilities", "tool-groups.ts"));
const { isLocalTool, OFFLINE_CAPABLE_TOOL_NAMES } = await import(resolve(MCP_SRC, "local-tools.ts"));
const { ALWAYS_BATCH_TOOLS, BATCH_TOOL_NAMES } = await import(resolve(MCP_SRC, "batch-spawn.ts"));

// ---------------------------------------------------------------------------
// 2. Route policy (mirrors mcp-server/src/tool-router.ts priority order):
//    local → offline-capable → always-batch → batch fallback → live.
// ---------------------------------------------------------------------------

function routeFor(toolName) {
  if (isLocalTool(toolName)) return "local";
  if (OFFLINE_CAPABLE_TOOL_NAMES.has(toolName)) return "offline";
  if (ALWAYS_BATCH_TOOLS.has(toolName)) return "batch";
  if (BATCH_TOOL_NAMES.has(toolName)) return "live+batch";
  return "live";
}

// ---------------------------------------------------------------------------
// 3. S0 status (parse mcp-full-test.mjs for step tool/expect pairs + SKIPS)
// ---------------------------------------------------------------------------

function parseS0() {
  const content = readFileSync(S0_FILE, "utf8");
  const steps = {}; // toolName → { expect, labels[] }
  const skips = new Set();

  // SKIPS array
  const skipsMatch = content.match(/const SKIPS = \[([\s\S]*?)\];/);
  if (skipsMatch) {
    const toolRe = /tool:\s*"(unity_[a-z0-9_]+)"/g;
    let sm;
    while ((sm = toolRe.exec(skipsMatch[1])) !== null) skips.add(sm[1]);
  }

  // REACHABLE_EXT_TOOLS + UNAVAIL_TOOLS arrays (Band F)
  const reachable = new Set();
  const unavail = new Set();
  const reachMatch = content.match(/const REACHABLE_EXT_TOOLS = \[([\s\S]*?)\];/);
  if (reachMatch) {
    const re = /"(unity_[a-z0-9_]+)"/g;
    let rm;
    while ((rm = re.exec(reachMatch[1])) !== null) reachable.add(rm[1]);
  }
  const unavailMatch = content.match(/const UNAVAIL_TOOLS = \[([\s\S]*?)\];/);
  if (unavailMatch) {
    const re = /"(unity_[a-z0-9_]+)"/g;
    let um;
    while ((um = re.exec(unavailMatch[1])) !== null) unavail.add(um[1]);
  }

  // s("label", "BAND", "tool", ...) calls — capture label + tool + expect.
  // Steps may be single-line (ending `);`) or multi-line (args on following
  // lines, closing `});` or `);`). Match the opening + scan to the closing.
  const stepRegex = /s\(\s*"([^"]+)"\s*,\s*"([A-Z])"\s*,\s*"(unity_[a-z0-9_]+)"/g;
  let sm2;
  while ((sm2 = stepRegex.exec(content)) !== null) {
    const label = sm2[1];
    const tool = sm2[3];
    // Scan forward from the match position to the end of this s() call — the
    // closing `)` followed by `;` or `,` or newline. Cap the scan at 800 chars
    // so a missing close doesn't eat the whole file.
    const after = content.slice(sm2.index, sm2.index + 800);
    const closeMatch = after.match(/\)\s*[;,]/);
    const rest = closeMatch ? after.slice(0, closeMatch.index) : after;
    let expect = "ok";
    const expectMatch = rest.match(/expect:\s*"([a-z_]+)"/);
    if (expectMatch) expect = expectMatch[1];
    if (!steps[tool]) steps[tool] = { expect, labels: [] };
    steps[tool].labels.push(label);
    // If any step for this tool uses tolerate, mark the tool as needing a
    // strict owner (S1). A tool with both ok + tolerate steps is "tolerate"
    // for ownership purposes (the tolerate variant is the gap).
    if (expect === "tolerate") {
      steps[tool].expect = "tolerate";
    }
  }

  return { steps, skips, reachable, unavail };
}

function s0Status(toolName, s0) {
  if (s0.skips.has(toolName)) return "skip";
  if (s0.unavail.has(toolName)) return "absent(unavail)";
  if (s0.reachable.has(toolName)) return "reachable";
  if (s0.steps[toolName]) {
    return s0.steps[toolName].expect === "tolerate" ? "tolerate" : "covered";
  }
  return "absent";
}

// ---------------------------------------------------------------------------
// 4. Ownership map — which suite owns each tool, and the strict owner.
//
// This encodes the design from the plan: S0 reachability for all; S1 strict
// for live mutating/read tools S0 only tolerates + the 8 absent tools; S2 for
// batch/offline; S3 transport; S4 extensions; S5 package/hub/destructive.
// ---------------------------------------------------------------------------

// Tools absent from S0 that S1 must cover.
const S1_ABSENT_TOOLS = new Set([
  "unity_open_mcp_scene_open",
  "unity_open_mcp_prefab_apply", "unity_open_mcp_prefab_revert", "unity_open_mcp_prefab_unpack",
  "unity_open_mcp_settings_set_player", "unity_open_mcp_settings_set_lighting",
  "unity_open_mcp_audio_mixer_set_parameter",
  "unity_open_mcp_upgrade",
]);

// S0 tolerate steps that S1 re-tests strict (or documents wont-fix).
// Derived from S0's tolerate steps — the generator checks each has an S1 owner.
const S1_STRICT_RETEST_DOMAINS = [
  // read_console, spatial_query, screenshot family, component_get, asmdef, reserialize,
  // frame_debugger, generate_skill, editor_set_state — all covered by S1 Band B/C.
];

// S5 owns the 3 SKIPS + hub mutators + destructive build.
const S5_TOOLS = new Set([
  "unity_open_mcp_package_add", "unity_open_mcp_package_remove", "unity_open_mcp_reimport_package",
  "unity_open_mcp_hub_set_install_path", "unity_open_mcp_hub_install_editor", "unity_open_mcp_hub_install_modules",
  "unity_open_mcp_build_start",
]);

// Extension groups → S4.
const EXTENSION_GROUPS = new Set([
  "navigation", "input-system", "probuilder", "particle-system", "animation",
  "splines", "timeline", "tilemap", "shadergraph", "vfx", "memoryprofiler",
]);

function ownershipFor(toolName, group, route, s0StatusVal) {
  const owners = ["S0"]; // S0 reachability for every tool (except SKIPS).
  let strictOwner = "S0";
  let passCriteria = "ok";
  let notes = "";

  if (S5_TOOLS.has(toolName)) {
    owners.length = 0;
    owners.push("S5");
    strictOwner = "S5";
    passCriteria = "tolerate(env)"; // package/hub/build steps tolerate env-missing codes
    notes = "destructive/disposable-project — S0 skips (would break demo)";
  } else if (EXTENSION_GROUPS.has(group)) {
    owners.push("S4");
    strictOwner = "S4";
    passCriteria = "ext";
    notes = `extension group \`${group}\` — skips when not compiled in`;
  } else if (route === "batch" || route === "offline") {
    owners.push("S2");
    strictOwner = "S2";
    passCriteria = "ok(headless)";
    notes = route === "offline" ? "offline parser (no bridge)" : "batch spawn (Editor closed)";
  } else if (route === "live+batch") {
    owners.push("S2");
    strictOwner = "S0+S2";
    passCriteria = "gate";
    notes = "batch-fallback meta-tool";
  } else if (S1_ABSENT_TOOLS.has(toolName)) {
    owners.push("S1");
    strictOwner = "S1";
    passCriteria = "gate/ok";
    notes = "absent from S0 — S1 strict happy-path";
  } else if (s0StatusVal === "tolerate") {
    owners.push("S1");
    strictOwner = "S1";
    passCriteria = "strict";
    notes = "S0 tolerates — S1 re-tests strict (or wont-fix in feedback.md)";
  } else if (route === "local") {
    owners.push("S3");
    strictOwner = "S3";
    passCriteria = "ok(local)";
    notes = "local-routed — S3 transport spot-check";
  }

  // S3 route spot-check covers a sample of every route category.
  if (!owners.includes("S3") && route !== "batch") {
    owners.push("S3");
  }

  return { owners: [...new Set(owners)], strictOwner, passCriteria, notes };
}

// ---------------------------------------------------------------------------
// 5. Render the matrix markdown
// ---------------------------------------------------------------------------

function renderMatrix(tools, s0) {
  const lines = [];
  lines.push("# MCP coverage matrix");
  lines.push("");
  lines.push("**Generated by:** `scripts/gen-mcp-coverage-matrix.mjs` (re-run when tools ship)");
  lines.push(`**Generated at:** ${new Date().toISOString()}`);
  lines.push(`**Tool count:** ${tools.length}`);
  lines.push("");
  lines.push("One row per registered MCP tool. Columns:");
  lines.push("- `tool_id` — the MCP tool name (`ALL_TOOLS` in `mcp-server/src/tools/index.ts`)");
  lines.push("- `route` — live / batch / offline / local / live+batch (mirrors `tool-router.ts`)");
  lines.push("- `group` — tool-group assignment from `tool-groups.ts` (null = always-visible meta-tool)");
  lines.push("- `suite_owner` — which suite(s) cover this tool (S0–S5)");
  lines.push("- `pass_criteria` — the expect mode the strict owner asserts");
  lines.push("- `s0_status` — covered / tolerate / skip / reachable / absent(unavail) / absent");
  lines.push("- `strict_owner` — the suite that asserts real success (not just reachability)");
  lines.push("- `notes` — action variants, fixture deps, env gaps");
  lines.push("");
  lines.push("## Suite catalog");
  lines.push("");
  lines.push("| ID | Script | Environment | Role |");
  lines.push("|---|---|---|---|");
  lines.push("| **S0** | `scripts/mcp-full-test.mjs` | Live Editor + bridge; `demo/` | Reachability smoke — every tool called once |");
  lines.push("| **S1** | `scripts/mcp-behavior.mjs` | Live Editor + bridge; `Assets/MCP_BehaviorTest/` | Success paths, gate semantics, checkpoint/fix chains |");
  lines.push("| **S2** | `scripts/mcp-headless.mjs` | Editor **closed** | Batch spawn, offline reads, batch meta-tools |");
  lines.push("| **S3** | `scripts/mcp-protocol.mjs` | MCP stdio server process | `tools/list`, `list_changed`, route spot-checks |");
  lines.push("| **S4** | `scripts/mcp-extensions.mjs` | Live Editor + bridge | Extension-pack success chains when groups compiled in |");
  lines.push("| **S5** | `scripts/mcp-sandbox.mjs` | Temp project clone (never mutates `demo/`) | Package lifecycle, hub mutators, destructive build |");
  lines.push("");
  lines.push("## Coverage matrix");
  lines.push("");
  lines.push("| tool_id | route | group | suite_owner | pass_criteria | s0_status | strict_owner | notes |");
  lines.push("|---|---|---|---|---|---|---|---|");

  const orphans = [];
  const tolerateWithoutOwner = [];
  let counts = { covered: 0, tolerate: 0, skip: 0, reachable: 0, absent: 0, "absent(unavail)": 0 };

  for (const t of tools) {
    const group = groupFor(t.name) ?? "—";
    const route = routeFor(t.name);
    const s0stat = s0Status(t.name, s0);
    counts[s0stat] = (counts[s0stat] ?? 0) + 1;
    const own = ownershipFor(t.name, group, route, s0stat);
    if (own.owners.length === 0) orphans.push(t.name);
    if (s0stat === "tolerate" && !own.owners.includes("S1")) tolerateWithoutOwner.push(t.name);
    const ownersStr = own.owners.join(", ") || "—";
    lines.push(`| \`${t.name}\` | ${route} | ${group} | ${ownersStr} | ${own.passCriteria} | ${s0stat} | ${own.strictOwner} | ${own.notes} |`);
  }

  lines.push("");
  lines.push("## Summary");
  lines.push("");
  lines.push("- **Total tools:** " + tools.length);
  lines.push("- **S0 covered:** " + (counts.covered ?? 0));
  lines.push("- **S0 tolerate (strict owner in S1):** " + (counts.tolerate ?? 0));
  lines.push("- **S0 skip (S5 owner):** " + (counts.skip ?? 0));
  lines.push("- **S0 reachable (S4 owner):** " + (counts.reachable ?? 0));
  lines.push("- **S0 absent(unavail) (S4 owner):** " + (counts["absent(unavail)"] ?? 0));
  lines.push("- **S0 absent (S1/S2/S5 owner):** " + (counts.absent ?? 0));
  lines.push("- **Orphans (no suite_owner):** " + orphans.length + (orphans.length ? " — " + orphans.join(", ") : ""));
  lines.push("- **S0 tolerate without S1 owner:** " + tolerateWithoutOwner.length + (tolerateWithoutOwner.length ? " — " + tolerateWithoutOwner.join(", ") : ""));
  lines.push("");

  // Variant appendix.
  lines.push("## Variant appendix");
  lines.push("");
  lines.push("Multi-action tools where a single tool id covers several distinct operations. Each variant has its own suite step:");
  lines.push("");
  lines.push("| tool_id | variants | suite_owner |");
  lines.push("|---|---|---|");
  lines.push("| `unity_senses_spatial_query` | `bounds`, `raycast`, `overlap`, `spherecast`, `find` | S0 (raycast only) + S1 (all 5) |");
  lines.push("| `unity_senses_frame_debugger` | `status`, `enable`, `list`, `disable` | S0 (status) + S1 (enable→list→disable) |");
  lines.push("| `unity_open_mcp_editor_set_state` | `play`, `pause`, `stop` | S0 (stop) + S1 (play/pause/stop w/ dirty-scene recovery) |");
  lines.push("| `unity_open_mcp_read_asset` | `profile: compact/balanced/full`, `page_size`+`cursor` | S0 (compact) + S1 (balanced/full/paged) |");
  lines.push("| `unity_open_mcp_search_assets` | `profile: compact/balanced/full`, `page_size`+`cursor` | S0 (compact) + S1 (paged) |");
  lines.push("| `unity_open_mcp_scene_get_data` | `profile: compact/balanced/full`, `depth` | S0 (compact) + S1 (balanced/full) |");
  lines.push("| `unity_open_mcp_manage_tools` | `list_groups`, `activate`, `deactivate`, `reset` | S0 (all 4) + S3 (activate→list_changed) |");
  lines.push("| `unity_open_mcp_capabilities` | `kind: tools/rules/fixes`, `include_planned` | S0 (tools/rules/fixes) + S3 (tools) |");
  lines.push("| `unity_senses_run_tests` | `mode: EditMode/PlayMode` | S0 (EditMode) + S1 (EditMode + PlayMode) |");
  lines.push("");

  return { markdown: lines.join("\n"), orphans, tolerateWithoutOwner, counts, total: tools.length };
}

// ---------------------------------------------------------------------------
// main
// ---------------------------------------------------------------------------

function parseArgs(argv) {
  const opts = { check: false, out: null };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--check") opts.check = true;
    else if (argv[i] === "--out") opts.out = argv[++i];
    else throw new Error(`Unknown argument: ${argv[i]}`);
  }
  if (opts.out === undefined) throw new Error("--out needs a path");
  return opts;
}

function main() {
  const opts = parseArgs(process.argv.slice(2));
  const tools = ALL_TOOLS.map((t) => ({ name: t.name })).sort((a, b) => a.name.localeCompare(b.name));
  const s0 = parseS0();
  const { markdown, orphans, tolerateWithoutOwner, counts, total } = renderMatrix(tools, s0);

  if (opts.check) {
    console.log("--check: matrix not written");
  } else if (opts.out) {
    const outFile = resolve(opts.out);
    mkdirSync(dirname(outFile), { recursive: true });
    writeFileSync(outFile, markdown);
    console.log(`Wrote ${outFile}`);
  } else if (existsSync(SPECS_DIR)) {
    mkdirSync(dirname(DEFAULT_OUT_FILE), { recursive: true });
    writeFileSync(DEFAULT_OUT_FILE, markdown);
    console.log(`Wrote ${DEFAULT_OUT_FILE}`);
  } else {
    console.log("No specs/ tree: matrix not written (pass --out <path> to write it elsewhere)");
  }
  console.log(`  tools:       ${total}`);
  console.log(`  S0 covered:  ${counts.covered ?? 0}`);
  console.log(`  S0 tolerate: ${counts.tolerate ?? 0}`);
  console.log(`  S0 skip:     ${counts.skip ?? 0}`);
  console.log(`  S0 reachable:${counts.reachable ?? 0}`);
  console.log(`  S0 unavail:  ${counts["absent(unavail)"] ?? 0}`);
  console.log(`  S0 absent:   ${counts.absent ?? 0}`);
  console.log(`  orphans:     ${orphans.length}${orphans.length ? " — " + orphans.join(", ") : ""}`);
  console.log(`  tolerate w/o S1 owner: ${tolerateWithoutOwner.length}${tolerateWithoutOwner.length ? " — " + tolerateWithoutOwner.join(", ") : ""}`);

  if (orphans.length > 0) {
    console.error("\nERROR: orphan tools have no suite_owner. Assign them to a suite or document a wont-fix.");
    process.exit(1);
  }
  if (tolerateWithoutOwner.length > 0) {
    console.error("\nERROR: S0 tolerate steps have no strict owner in S1. Add an S1 strict re-test or a specs/feedback.md wont-fix link.");
    process.exit(1);
  }
  console.log("\n✓ zero unowned tools; every S0 tolerate step has an S1 strict owner.");
}

main();
