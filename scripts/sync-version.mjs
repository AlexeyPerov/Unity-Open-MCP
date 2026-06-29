#!/usr/bin/env node
// sync-version.mjs — single-source-of-truth version sync for the monorepo.
//
// There are two independent version lines, each with ONE source file:
//
//   1. The SHARED TRIO version (npm MCP server + bridge Unity pkg + verify
//      Unity pkg). Source of truth: <repo>/version.json. These three ship
//      breaking changes together and must stay on the same number.
//
//   2. The HUB APP version (Unity Hub Pro desktop app), on its own independent
//      release cadence. Source of truth: <repo>/hub/version.json.
//
// Every other place a version string appears is GENERATED from one of those two
// files by this script. Never hand-edit a generated target — bump the source and
// run `node scripts/sync-version.mjs`. The CI gate (version-sync.yml) fails any
// PR where a generated target has drifted from its source.
//
// Usage:
//   node scripts/sync-version.mjs                # rewrite all trio targets from version.json
//   node scripts/sync-version.mjs --check        # read-only; exit 1 if any trio target drifted
//   node scripts/sync-version.mjs --hub          # rewrite all HUB targets from hub/version.json
//   node scripts/sync-version.mjs --check --hub  # read-only drift check for the HUB
//   node scripts/sync-version.mjs bump <level>            # bump version.json + sync trio
//   node scripts/sync-version.mjs bump <level> --hub      # bump hub/version.json + sync hub
//   node scripts/sync-version.mjs set <X.Y.Z>             # set version.json to <X.Y.Z> + sync trio
//   node scripts/sync-version.mjs set <X.Y.Z> --hub       # set hub/version.json to <X.Y.Z> + sync hub
//
//   <level> = major | minor | patch
//   <X.Y.Z> = plain major.minor.patch (a leading "v" is tolerated and stripped);
//             pre-release/build metadata are not supported.
//
// One bespoke script is the proven minimal pattern; we deliberately avoid
// changesets/lerna/nx for a repo this size.
//
// Requires Node 18+ (uses no runtime dependencies, only node: builtins).

import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");

// ---------------------------------------------------------------------------
// Source-of-truth files
// ---------------------------------------------------------------------------

const TRIO_SOURCE = "version.json";
const HUB_SOURCE = "hub/version.json";

// ---------------------------------------------------------------------------
// Target registry
// ---------------------------------------------------------------------------
// Each target names a file (relative to repo root) and a `replace(content)`
// function that returns the content with the version swapped in. Targets are
// pure functions of (fileContent, newVersion) — they never depend on each other.
//
// To add a new version surface: append an entry to TRIO_TARGETS or HUB_TARGETS.
// The `--check` and `bump` paths pick it up automatically.
// ---------------------------------------------------------------------------

/** @param {string} body @param {string} v */
function setJsonVersion(body, v) {
  return body.replace(
    /("version"\s*:\s*")[^"]*(")/,
    (_, pre, post) => `${pre}${v}${post}`,
  );
}

// Bridge C# constant — kept in sync with packages/bridge/package.json so /ping
// reports the package version rather than a hand-edited literal.
const TRIO_TARGETS = [
  {
    file: "mcp-server/package.json",
    kind: "json",
    description: "npm MCP server package.json",
    replace: (b, v) => setJsonVersion(b, v),
  },
  {
    file: "packages/bridge/package.json",
    kind: "json",
    description: "bridge Unity package.json",
    replace: (b, v) => setJsonVersion(b, v),
  },
  {
    file: "packages/verify/package.json",
    kind: "json",
    description: "verify Unity package.json",
    replace: (b, v) => setJsonVersion(b, v),
  },
  {
    file: "packages/bridge/Editor/Bridge/BridgeSession.cs",
    kind: "cs",
    description: "BridgeSession.BridgeVersion constant (reported by /ping)",
    replace: (b, v) =>
      b.replace(
        /(public static string BridgeVersion => ")[^"]*(")/,
        (_, pre, post) => `${pre}${v}${post}`,
      ),
  },
  {
    file: "packages/bridge/Editor/Bridge/BridgeHttpServer.cs",
    kind: "cs",
    description: "/ping 503 fallback literal (pre-init body)",
    replace: (b, v) =>
      b.replace(
        /(bridgeVersion\\":\\")[^\\]*(\\")/,
        (_, pre, post) => `${pre}${v}${post}`,
      ),
  },
];

const HUB_TARGETS = [
  {
    file: "hub/src-tauri/tauri.conf.json",
    kind: "json",
    description: "Hub Tauri config",
    replace: (b, v) => setJsonVersion(b, v),
  },
  {
    file: "hub/src-tauri/Cargo.toml",
    kind: "toml",
    description: "Hub Rust crate",
    replace: (b, v) =>
      b.replace(
        /(^version\s*=\s*")[^"]*(")/m,
        (_, pre, post) => `${pre}${v}${post}`,
      ),
  },
  {
    file: "hub/package.json",
    kind: "json",
    description: "Hub npm package.json",
    replace: (b, v) => setJsonVersion(b, v),
  },
];

// ---------------------------------------------------------------------------
// Core operations
// ---------------------------------------------------------------------------

/** @param {string} rel @returns {string} */
function abs(rel) {
  return resolve(REPO_ROOT, rel);
}

/** @param {string} rel @returns {string} */
function read(rel) {
  return readFileSync(abs(rel), "utf8");
}

/** @param {string} sourceFile @returns {string} */
function readSourceVersion(sourceFile) {
  const body = read(sourceFile);
  const parsed = JSON.parse(body);
  if (typeof parsed.version !== "string" || !parsed.version) {
    throw new Error(`No "version" string in ${sourceFile}`);
  }
  return parsed.version;
}

/**
 * @param {string} sourceFile
 * @param {Array} targets
 * @param {"write"|"check"} mode
 * @returns {{ changed: Array, drifted: Array, missing: Array }} per-target results
 */
function syncTargets(sourceFile, targets, mode) {
  const want = readSourceVersion(sourceFile);
  /** @type {Array<{file:string, description:string, from?:string, to:string}>} */
  const changed = [];
  /** @type {Array<{file:string, description:string, from:string, want:string}>} */
  const drifted = [];
  /** @type {Array<{file:string, description:string}>} */
  const missing = [];

  for (const t of targets) {
    const p = abs(t.file);
    if (!existsSync(p)) {
      missing.push({ file: t.file, description: t.description });
      continue;
    }
    const original = readFileSync(p, "utf8");
    const updated = t.replace(original, want);
    if (updated === original) continue; // already in sync
    const from = extractVersion(original, t.kind);
    if (mode === "write") {
      writeFileSync(p, updated);
      changed.push({ file: t.file, description: t.description, from, to: want });
    } else {
      drifted.push({ file: t.file, description: t.description, from, want });
    }
  }
  return { changed, drifted, missing };
}

/** Best-effort extraction of the current version from a target file body, for
 *  reporting only (the replace functions do the real work).
 * @param {string} body
 * @param {string} kind
 * @returns {string | undefined}
 */
function extractVersion(body, kind) {
  if (kind === "cs") {
    // Match the first C# string literal following a version-shaped pattern.
    const m = body.match(/=>\s*"([^"]+)"/) || body.match(/"([\d.]+)"/);
    return m ? m[1] : undefined;
  }
  if (kind === "toml") {
    const m = body.match(/^version\s*=\s*"([^"]+)"/m);
    return m ? m[1] : undefined;
  }
  // json
  const m = body.match(/"version"\s*:\s*"([^"]+)"/);
  return m ? m[1] : undefined;
}

// ---------------------------------------------------------------------------
// Bump
// ---------------------------------------------------------------------------

/**
 * @param {string} v
 * @param {"major"|"minor"|"patch"} level
 * @returns {string}
 */
function bumpSemver(v, level) {
  const m = /^(\d+)\.(\d+)\.(\d+)/.exec(v);
  if (!m) {
    throw new Error(`Source version ${v} is not X.Y.Z — cannot bump.`);
  }
  let [major, minor, patch] = [Number(m[1]), Number(m[2]), Number(m[3])];
  if (level === "major") {
    major += 1;
    minor = 0;
    patch = 0;
  } else if (level === "minor") {
    minor += 1;
    patch = 0;
  } else {
    patch += 1;
  }
  return `${major}.${minor}.${patch}`;
}

/** @param {string} sourceFile @param {string} newVersion */
function writeSource(sourceFile, newVersion) {
  const original = read(sourceFile);
  const updated = setJsonVersion(original, newVersion);
  if (updated === original) return;
  writeFileSync(abs(sourceFile), updated);
}

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------

const argv = process.argv.slice(2);
const CHECK = argv.includes("--check");
const HUB = argv.includes("--hub");
const bumpIdx = argv.indexOf("bump");
const setIdx = argv.indexOf("set");
const isBump = bumpIdx !== -1;
const isSet = setIdx !== -1;
const bumpLevel = isBump ? argv[bumpIdx + 1] : undefined;
const setRaw = isSet ? argv[setIdx + 1] : undefined;

if (isBump && !["major", "minor", "patch"].includes(String(bumpLevel))) {
  console.error("Usage: bump <level> where level is major | minor | patch");
  process.exit(2);
}
// A leading "v" is tolerated and stripped; pre-release/build metadata are rejected.
const setVersion =
  isSet && typeof setRaw === "string" && /^v?\d+\.\d+\.\d+$/.test(setRaw)
    ? setRaw.replace(/^v/, "")
    : undefined;
if (isSet && setVersion === undefined) {
  console.error('Usage: set <X.Y.Z> where X.Y.Z is plain major.minor.patch');
  process.exit(2);
}
if (isBump && isSet) {
  console.error("bump and set are mutually exclusive.");
  process.exit(2);
}
if (CHECK && (isBump || isSet)) {
  console.error("--check is mutually exclusive with bump and set.");
  process.exit(2);
}

const sourceFile = HUB ? HUB_SOURCE : TRIO_SOURCE;
const targets = HUB ? HUB_TARGETS : TRIO_TARGETS;
const label = HUB ? "Hub app" : "shared trio";

// Bump/set path: update the source first, then sync.
if (isBump || isSet) {
  const current = readSourceVersion(sourceFile);
  const next = isBump
    ? bumpSemver(current, /** @type {"major"|"minor"|"patch"} */ (bumpLevel))
    : /** @type {string} */ (setVersion);
  writeSource(sourceFile, next);
  const { changed, missing } = syncTargets(sourceFile, targets, "write");
  const verb = isBump ? "Bumped" : "Set";
  console.log(`${verb} ${label}: ${current} → ${next}`);
  console.log(`  source: ${sourceFile}`);
  for (const c of changed) {
    console.log(`  ${c.file}${c.from ? ` (${c.from} → ${c.to})` : ""}`);
  }
  for (const m of missing) {
    console.warn(`  ⚠  missing: ${m.file} (${m.description})`);
  }
  console.log("\nNext: review the diff, then commit and tag.");
  if (HUB) {
    console.log("  git add -A && git commit -m \"chore: hub bump to " + next + "\"");
    console.log(`  git tag hub-v${next} && git push origin hub-v${next}`);
  } else {
    console.log(`  git add -A && git commit -m "chore: bump to ${next}"`);
    console.log(`  git tag v${next} && git push origin v${next}`);
  }
  process.exit(0);
}

// Sync / check path.
const mode = CHECK ? "check" : "write";
const result = syncTargets(sourceFile, targets, mode);

if (mode === "write") {
  if (result.changed.length === 0 && result.missing.length === 0) {
    console.log(`${label}: already in sync at ${readSourceVersion(sourceFile)}.`);
  } else {
    console.log(`${label}: synced to ${readSourceVersion(sourceFile)}.`);
    for (const c of result.changed) {
      console.log(`  ${c.file}${c.from ? ` (${c.from} → ${c.to})` : ""}`);
    }
  }
  for (const m of result.missing) {
    console.warn(`  ⚠  missing target: ${m.file} (${m.description})`);
  }
  process.exit(0);
}

// --check
if (result.drifted.length === 0) {
  console.log(`${label}: OK (all targets match ${readSourceVersion(sourceFile)}).`);
  process.exit(0);
}
console.error(`✖ ${label} version drift detected. Source ${sourceFile} = ${readSourceVersion(sourceFile)}.`);
for (const d of result.drifted) {
  console.error(`  ${d.file}: ${d.from ?? "<unmatched>"} (expected ${d.want})`);
}
console.error(
  `\nFix: run \`node scripts/sync-version.mjs${HUB ? " --hub" : ""}\` from the repo root.`,
);
process.exit(1);
