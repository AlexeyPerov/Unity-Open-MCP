import test from "node:test";
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

// ---------------------------------------------------------------------------
// Compile-gate parity for the Unity packages' assembly definitions.
//
// A `versionDefines` symbol exists only in the asmdef that declares it: it
// never reaches a sibling or referencing asmdef. So an asmdef that constrains
// on (or `#if`s on) such a symbol must declare it itself, and a domain test
// asmdef must carry exactly the gate (defineConstraints + versionDefines) of
// the embedded-domain asmdefs it tests — otherwise the tests silently never
// compile where the domain does (or compile empty), or break where it does
// not. Contract: docs/contributing/extensions.md (Embedded domain model).
//
// Reads the C# package tree from disk; skips when run outside the toolkit
// repo (a standalone mcp-server/ install has no packages/).
// ---------------------------------------------------------------------------

interface VersionDefine {
  name: string;
  expression: string;
  define: string;
}

interface Asmdef {
  name: string;
  /** Repo-relative, forward slashes. */
  path: string;
  dir: string;
  references: string[];
  defineConstraints: string[];
  versionDefines: VersionDefine[];
}

function hereDir(): string {
  if (typeof __dirname !== "undefined") return __dirname;
  return dirname(fileURLToPath(import.meta.url));
}

function findToolkitRoot(): string | null {
  let dir = hereDir();
  for (let i = 0; i < 10; i++) {
    if (existsSync(join(dir, "skills", "client-paths.json"))) return dir;
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

/** Files under `dir`, skipping folders Unity does not import (`.x`, `x~`). */
function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name.startsWith(".") || entry.name.endsWith("~") || entry.name === "node_modules") continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) walk(full, out);
    else out.push(full);
  }
  return out;
}

const ROOT = findToolkitRoot();
const PACKAGES = ROOT ? join(ROOT, "packages") : null;
const HAS_PACKAGES = PACKAGES !== null && existsSync(join(PACKAGES, "bridge"));

function toRepoPath(p: string): string {
  return relative(ROOT!, p).split("\\").join("/");
}

function loadAsmdefs(): Asmdef[] {
  return walk(PACKAGES!)
    .filter((p) => p.endsWith(".asmdef"))
    .map((p) => {
      const json = JSON.parse(readFileSync(p, "utf8")) as Partial<Asmdef>;
      return {
        name: json.name ?? "",
        path: toRepoPath(p),
        dir: dirname(p),
        references: json.references ?? [],
        defineConstraints: json.defineConstraints ?? [],
        versionDefines: json.versionDefines ?? [],
      };
    });
}

const isTestAsmdef = (a: Asmdef) => a.path.split("/").includes("Tests");

/** Embedded-domain source asmdefs (bridge sub-assemblies) and community packs. */
const isDomainSource = (a: Asmdef) =>
  !isTestAsmdef(a) &&
  (a.path.startsWith("packages/bridge/Editor/TypedTools/Extensions/") ||
    a.path.startsWith("packages/extensions/"));

/** `A || !B` → [A, B]. */
function constraintSymbols(constraint: string): string[] {
  return constraint
    .split("||")
    .map((s) => s.trim().replace(/^!/, "").trim())
    .filter(Boolean);
}

const vdKey = (v: VersionDefine) => `${v.name} ${v.expression} → ${v.define}`;

/** Symbols the repo defines through versionDefines (any asmdef), plus the
 *  project prefix, so a typo'd gate symbol is caught too. */
function isProjectSymbol(symbol: string, declared: Set<string>): boolean {
  return declared.has(symbol) || symbol.startsWith("UNITY_OPEN_MCP_");
}

test("asmdef gates: every project gate symbol is declared by the asmdef that uses it", { skip: !HAS_PACKAGES }, () => {
  const asmdefs = loadAsmdefs();
  const declared = new Set(asmdefs.flatMap((a) => a.versionDefines.map((v) => v.define)));
  const problems: string[] = [];
  for (const a of asmdefs) {
    const own = new Set(a.versionDefines.map((v) => v.define));
    for (const constraint of a.defineConstraints) {
      for (const symbol of constraintSymbols(constraint)) {
        if (isProjectSymbol(symbol, declared) && !own.has(symbol)) {
          problems.push(`${a.path}: defineConstraints uses ${symbol} but its versionDefines do not declare it`);
        }
      }
    }
  }
  assert.deepEqual(problems, [], problems.join("\n"));
});

test("asmdef gates: a domain test asmdef mirrors the gate of the domain asmdefs it references", { skip: !HAS_PACKAGES }, () => {
  const asmdefs = loadAsmdefs();
  const sources = new Map(asmdefs.filter(isDomainSource).map((a) => [a.name, a]));
  const tests = asmdefs.filter(isTestAsmdef);
  const domainTests = tests.filter((t) => t.references.some((r) => sources.has(r)));
  // Non-vacuous: the bridge ships ~20 domain test assemblies.
  assert.ok(domainTests.length >= 18, `expected the domain test asmdefs, found ${domainTests.length}`);

  const problems: string[] = [];
  for (const t of tests) {
    const tested = t.references.map((r) => sources.get(r)).filter((s): s is Asmdef => s !== undefined);
    const expectedConstraints = new Set(tested.flatMap((s) => s.defineConstraints));
    const expectedDefines = new Set(tested.flatMap((s) => s.versionDefines.map(vdKey)));
    const actualConstraints = new Set(t.defineConstraints);
    const actualDefines = new Set(t.versionDefines.map(vdKey));
    const from = tested.map((s) => s.name).join(", ") || "no domain asmdef";
    for (const c of expectedConstraints) {
      if (!actualConstraints.has(c)) problems.push(`${t.path}: missing defineConstraint ${c} (from ${from})`);
    }
    for (const c of actualConstraints) {
      if (!expectedConstraints.has(c)) {
        problems.push(`${t.path}: extra defineConstraint ${c} — the tests would not compile everywhere ${from} does`);
      }
    }
    for (const v of expectedDefines) {
      if (!actualDefines.has(v)) problems.push(`${t.path}: missing versionDefine ${v} (from ${from})`);
    }
    for (const v of actualDefines) {
      if (!expectedDefines.has(v)) problems.push(`${t.path}: extra versionDefine ${v} (not in ${from})`);
    }
  }
  assert.deepEqual(problems, [], problems.join("\n"));
});

test("asmdef gates: #if on a project gate symbol is declared by the asmdef that compiles the file", { skip: !HAS_PACKAGES }, () => {
  const asmdefs = loadAsmdefs();
  const declared = new Set(asmdefs.flatMap((a) => a.versionDefines.map((v) => v.define)));
  // A script belongs to the asmdef in its nearest ancestor folder.
  const byDir = new Map(asmdefs.map((a) => [a.dir, a]));
  const owner = (file: string): Asmdef | undefined => {
    let dir = dirname(file);
    while (dir.startsWith(PACKAGES!)) {
      const a = byDir.get(dir);
      if (a) return a;
      dir = dirname(dir);
    }
    return undefined;
  };
  const problems: string[] = [];
  let checked = 0;
  for (const file of walk(PACKAGES!).filter((p) => p.endsWith(".cs"))) {
    const a = owner(file);
    if (!a) continue;
    const own = new Set(a.versionDefines.map((v) => v.define));
    const lines = readFileSync(file, "utf8").split(/\r?\n/);
    lines.forEach((line, i) => {
      if (!/^\s*#\s*(el)?if\b/.test(line)) return;
      for (const symbol of line.match(/\b[A-Z][A-Z0-9_]+\b/g) ?? []) {
        if (!isProjectSymbol(symbol, declared)) continue;
        checked++;
        if (!own.has(symbol)) {
          problems.push(`${toRepoPath(file)}:${i + 1}: #if ${symbol} — not declared by ${a.path}, so the code compiles out`);
        }
      }
    });
  }
  assert.ok(checked > 0, "expected #if gates in the domain sources");
  assert.deepEqual(problems, [], problems.join("\n"));
});
