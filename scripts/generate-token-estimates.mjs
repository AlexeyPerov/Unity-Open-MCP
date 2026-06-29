#!/usr/bin/env node
// generate-token-estimates.mjs — codegen the per-tool token-estimate table the
// bridge Editor window renders next to each tool / group / the catalog total.
//
// Single source of truth: the MCP-server tool schemas
// (mcp-server/src/tools/index.ts → ALL_TOOLS) + the canonical group assignment
// (mcp-server/src/capabilities/tool-groups.ts). The bridge is C# and cannot
// read TS at runtime, so this script imports the TS sources at codegen time and
// emits a static lookup table the bridge reads like any other const.
//
// Why codegen (not a hand-maintained list): the catalog has ~290 tools and
// grows every milestone. A second hand-maintained list would drift from the
// real schemas within one release. Importing the live TS source means the
// generated table can never disagree with what an agent actually receives.
//
// Token heuristic: serialize each tool's { name, description, inputSchema } to
// its MCP wire JSON, then estimate tokens as max(1, ceil(jsonLen / 4)). This is
// the standard chars-per-token approximation (≈4 chars/token for English text
// and JSON keys); a real BPE tokenizer is intentionally out of scope (extra
// dependency, marginal accuracy gain for a UI hint). The absolute number is an
// estimate — the value to operators is the RELATIVE cost of one tool vs another
// and the running total as they toggle groups.
//
// Usage:
//   node scripts/generate-token-estimates.mjs          # rewrite the generated .cs
//   node scripts/generate-token-estimates.mjs --check  # read-only; exit 1 if drifted
//
// Requires Node 20.6+ (uses node:module register() + --experimental-strip-types
// to load the TS sources directly — no dist build, no second list). The CI drift
// gate (.github/workflows/version-sync.yml) runs --check on every PR touching
// the tool schemas or this script.
//
// Mirrors the dependency-free philosophy of scripts/sync-version.mjs: zero
// runtime dependencies, only node: builtins.

import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { register } from "node:module";

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const MCP_SERVER = resolve(REPO_ROOT, "mcp-server");
const OUT_FILE = resolve(
  REPO_ROOT,
  "packages/bridge/Editor/UI/BridgeToolTokenEstimates.cs",
);

// ---------------------------------------------------------------------------
// Resolve hook — rewrite local `.js` import specifiers to `.ts` so the TS
// sources load under --experimental-strip-types. The MCP-server source tree
// uses Node16 moduleResolution (emits `.js` import specifiers that point at the
// `.ts` source). Without this hook those specifiers 404 at codegen time. Only
// rewrites relative specifiers — never bare packages (@modelcontextprotocol/sdk,
// node:*) or node_modules. The hook is registered from an inline data URL so no
// temp file is left in the scripts/ tree (an earlier version wrote a sibling
// .mjs loader and never cleaned it up).
// ---------------------------------------------------------------------------
const LOADER_SOURCE = `
export async function resolve(specifier, context, nextResolve) {
  if (
    specifier.endsWith(".js") &&
    !specifier.startsWith("node:") &&
    !specifier.includes("node_modules") &&
    !specifier.startsWith("@")
  ) {
    const tsSpec = specifier.slice(0, -3) + ".ts";
    try {
      return await nextResolve(tsSpec, context);
    } catch {
      // fall through to default resolve
    }
  }
  return nextResolve(specifier, context);
}
`;
register(`data:text/javascript,${encodeURIComponent(LOADER_SOURCE)}`, import.meta.url);

const { ALL_TOOLS } = await import(
  resolve(MCP_SERVER, "src/tools/index.ts")
);
const { groupFor, TOOL_GROUPS } = await import(
  resolve(MCP_SERVER, "src/capabilities/tool-groups.ts")
);

// ---------------------------------------------------------------------------
// Compute the per-tool estimate + group assignment
// ---------------------------------------------------------------------------

/**
 * Estimate the token cost of one tool from its MCP wire representation.
 * @param {{name:string, description?:string, inputSchema?:unknown}} tool
 * @returns {number}
 */
function estimateTokens(tool) {
  const json = JSON.stringify({
    name: tool.name,
    description: tool.description ?? "",
    inputSchema: tool.inputSchema ?? {},
  });
  // ≥ 1 so an empty-schema tool still reports one token (matches the
  // "every tool has a positive count" sanity bound the test pins).
  return Math.max(1, Math.ceil(json.length / 4));
}

/** @type {{name:string, tokens:number, group:string|null}[]} */
const rows = [];
for (const tool of ALL_TOOLS) {
  rows.push({
    name: tool.name,
    tokens: estimateTokens(tool),
    group: groupFor(tool.name),
  });
}
// Stable order so the generated file is deterministic across runs.
rows.sort((a, b) => a.name.localeCompare(b.name));

// ---------------------------------------------------------------------------
// Emit the C# source
// ---------------------------------------------------------------------------

function csharpStringLiteral(s) {
  // C# string literal with the standard escapes. Tool names + group ids are
  // plain ascii identifiers, but escape anyway for safety.
  return `"${String(s).replace(/["\\]/g, "\\$&")}"`;
}

function generate() {
  const lines = [];
  lines.push("// <auto-generated>");
  lines.push(
    "//     Per-tool token estimates for the bridge Editor Tools tab.",
  );
  lines.push(
    "//     Generated by scripts/generate-token-estimates.mjs from the live",
  );
  lines.push(
    "//     MCP-server tool schemas (mcp-server/src/tools/index.ts) + the",
  );
  lines.push(
    "//     canonical group assignment (mcp-server/src/capabilities/tool-groups.ts).",
  );
  lines.push("//");
  lines.push(
    "//     DO NOT EDIT BY HAND. Regenerate with:",
  );
  lines.push("//       node scripts/generate-token-estimates.mjs");
  lines.push(
    "//     The CI drift gate (.github/workflows/version-sync.yml) fails any",
  );
  lines.push(
    "//     PR where this file disagrees with the source schemas.",
  );
  lines.push("// </auto-generated>");
  lines.push("");
  lines.push("using System.Collections.Generic;");
  lines.push("");
  lines.push("namespace UnityOpenMcpBridge");
  lines.push("{");
  lines.push(
    "    /// <summary>",
  );
  lines.push(
    "    /// Per-tool token estimates + group assignments for the bridge Tools tab.",
  );
  lines.push(
    "    /// Each token count is the chars/4 heuristic over the tool's MCP wire JSON",
  );
  lines.push(
    "    /// (name + description + inputSchema) — see scripts/generate-token-estimates.mjs.",
  );
  lines.push(
    "    /// Regenerate, do not hand-edit.",
  );
  lines.push(
    "    /// </summary>",
  );
  lines.push(
    "    internal static class BridgeToolTokenEstimates",
  );
  lines.push("    {");
  lines.push(
    "        // tool name → token estimate. Built once; lookup is O(1).",
  );
  lines.push(
    "        private static readonly Dictionary<string, int> Estimates = new()",
  );
  lines.push("        {");
  for (const r of rows) {
    lines.push(
      `            { ${csharpStringLiteral(r.name)}, ${r.tokens} },`,
    );
  }
  lines.push("        };");
  lines.push("");
  lines.push(
    "        // tool name → group id (null when the tool is an always-visible",
  );
  lines.push(
    "        // meta-tool: capabilities, ping, manage_tools, etc.). Mirrors",
  );
  lines.push(
    "        // tool-groups.ts groupFor() 1:1 so the UI's per-group aggregates",
  );
  lines.push(
    "        // match the MCP server's group catalog exactly.",
  );
  lines.push(
    "        private static readonly Dictionary<string, string> Groups = new()",
  );
  lines.push("        {");
  for (const r of rows) {
    if (r.group == null) continue;
    lines.push(
      `            { ${csharpStringLiteral(r.name)}, ${csharpStringLiteral(
        r.group,
      )} },`,
    );
  }
  lines.push("        };");
  lines.push("");
  lines.push(
    "        /// <summary>Token estimate for a tool, or null when the catalog",
  );
  lines.push(
    "        /// has a tool the codegen did not see (defensive — should not",
  );
  lines.push(
    "        /// happen for known tools; the Tools tab renders \"~?\" for it).",
  );
  lines.push("        /// </summary>");
  lines.push(
    "        public static int? EstimateFor(string name) =>",
  );
  lines.push(
    "            name != null && Estimates.TryGetValue(name, out var n) ? n : (int?)null;",
  );
  lines.push("");
  lines.push(
    "        /// <summary>Group id for a tool, or null when the tool is an",
  );
  lines.push(
    "        /// always-visible meta-tool (not part of any group).</summary>",
  );
  lines.push(
    "        public static string GroupFor(string name) =>",
  );
  lines.push(
    "            name != null && Groups.TryGetValue(name, out var g) ? g : null;",
  );
  lines.push("");
  lines.push(
    "        /// <summary>Human-readable formatting for a token count: ≥1000",
  );
  lines.push(
    "        /// renders as thousands with one decimal (1.2K, 74.6K), below",
  );
  lines.push(
    "        /// 1000 renders as the plain integer. Matches the convention used",
  );
  lines.push(
    "        /// by competing MCP tooling so the figure reads identically.",
  );
  lines.push(
    "        /// </summary>",
  );
  lines.push(
    "        public static string Format(int tokens)",
  );
  lines.push("        {");
  lines.push("            if (tokens >= 1000)");
  lines.push(
    "                return (tokens / 1000.0).ToString(\"0.#\") + \"K\";",
  );
  lines.push("            return tokens.ToString();");
  lines.push("        }");
  lines.push("    }");
  lines.push("}");

  // Trailing newline — matches the repo's C# file convention.
  return lines.join("\n") + "\n";
}

const generated = generate();

// ---------------------------------------------------------------------------
// Write or check
// ---------------------------------------------------------------------------

const argv = process.argv.slice(2);
const CHECK = argv.includes("--check");

if (CHECK) {
  if (!existsSync(OUT_FILE)) {
    console.error(
      `✖ Token-estimate table missing: ${OUT_FILE}\n` +
        "  Run `node scripts/generate-token-estimates.mjs` to create it.",
    );
    process.exit(1);
  }
  const existing = readFileSync(OUT_FILE, "utf8");
  if (existing === generated) {
    const total = rows.reduce((s, r) => s + r.tokens, 0);
    console.log(
      `token-estimates: OK (${rows.length} tools, ~${total} tokens total).`,
    );
    process.exit(0);
  }
  console.error(
    "✖ Token-estimate table drifted from the MCP-server tool schemas.",
  );
  console.error(
    `  target: ${OUT_FILE}\n` +
      "  Fix: run `node scripts/generate-token-estimates.mjs` from the repo root.",
  );
  process.exit(1);
}

// Write path.
const existed = existsSync(OUT_FILE);
writeFileSync(OUT_FILE, generated);
const total = rows.reduce((s, r) => s + r.tokens, 0);
const grouped = rows.reduce((acc, r) => {
  const k = r.group ?? "(meta)";
  acc[k] = (acc[k] ?? 0) + r.tokens;
  return acc;
}, /** @type {Record<string, number>} */ ({}));
console.log(
  `token-estimates: ${existed ? "regenerated" : "created"} ${OUT_FILE}`,
);
console.log(
  `  ${rows.length} tools, ~${total} tokens total across ${TOOL_GROUPS.length} groups.`,
);
const groupSummary = Object.entries(grouped)
  .sort((a, b) => b[1] - a[1])
  .map(([g, t]) => `${g}: ~${t}`)
  .join(", ");
console.log(`  by group: ${groupSummary}`);
