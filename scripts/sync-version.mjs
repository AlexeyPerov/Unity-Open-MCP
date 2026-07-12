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
//   node scripts/sync-version.mjs tags <X.Y.Z>            # create v/bridge-v/verify-v tags on HEAD (trio)
//   node scripts/sync-version.mjs tags <X.Y.Z> --hub      # create hub-v tag on HEAD (hub)
//
//   <level> = major | minor | patch
//   <X.Y.Z> = plain major.minor.patch (a leading "v" is tolerated and stripped);
//             pre-release/build metadata are not supported. The version passed to
//             `tags` must match the current source file (version.json / hub/version.json),
//             so a tag never diverges from what's committed.
//
// One bespoke script is the proven minimal pattern; we deliberately avoid
// changesets/lerna/nx for a repo this size.
//
// Requires Node 18+ (uses no runtime dependencies, only node: builtins).

import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

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

// Rewrites a single entry in a package.json `dependencies` object to the new
// version. Used for inter-package pins that must track the trio version — e.g.
// the bridge depends on verify at the same version so a git-URL install of both
// resolves (Unity reads "0.4.1" as >=0.4.1 <0.5.0 for 0.x, so an exact pin is
// the only form that satisfies when both are installed via git URL).
/** @param {string} body @param {string} dep @param {string} v */
function setJsonDependencyVersion(body, dep, v) {
  const re = new RegExp(`("${dep}"\\s*:\\s*")[^"]*(")`);
  return body.replace(re, (_, pre, post) => `${pre}${v}${post}`);
}

// Rewrites every `unity-open-mcp@<X.Y.Z>` pin in a doc to the new version.
// Only matches a numeric suffix — `@latest` and the bare package name
// (`npm i -g unity-open-mcp`) are left untouched, so the script manages only
// docs that have already been pinned and never auto-converts @latest.
/** @param {string} body @param {string} v */
function replaceNpmPin(body, v) {
  return body.replace(
    /(unity-open-mcp@)\d+\.\d+\.\d+/g,
    (_, pre) => `${pre}${v}`,
  );
}

// Rewrites the bare `bridge-v<X.Y.Z>` / `verify-v<X.Y.Z>` literals the wizard
// Packages step shows as the version-pin example (placeholder + hint). Only
// the trio-version-shaped tag suffixes are touched; the Rust planner's actual
// default tags are compile-time-derived, so this keeps the *example* the user
// sees in sync with what gets installed.
/** @param {string} body @param {string} v */
function replaceWizardTags(body, v) {
  return body
    .replace(/(bridge-v)\d+\.\d+\.\d+/g, `$1${v}`)
    .replace(/(verify-v)\d+\.\d+\.\d+/g, `$1${v}`);
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
    file: "packages/bridge/package.json",
    kind: "json-dep",
    description: "bridge → verify dependency pin (must match trio for git-URL install)",
    replace: (b, v) =>
      setJsonDependencyVersion(
        b,
        "com.alexeyperov.unity-open-mcp-verify",
        v,
      ),
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
  {
    file: "docs/setup/manual-setup.md",
    kind: "md-git",
    description: "manual-setup.md git-URL install pins (#bridge-v / #verify-v)",
    replace: (b, v) =>
      b
        .replace(/(#bridge-v)\d+\.\d+\.\d+/g, `$1${v}`)
        .replace(/(#verify-v)\d+\.\d+\.\d+/g, `$1${v}`),
  },
  {
    file: "docs/setup/agent-setup.md",
    kind: "md-git",
    description: "agent-setup.md git-URL install pins (#bridge-v / #verify-v)",
    replace: (b, v) =>
      b
        .replace(/(#bridge-v)\d+\.\d+\.\d+/g, `$1${v}`)
        .replace(/(#verify-v)\d+\.\d+\.\d+/g, `$1${v}`),
  },
  // Wizard UI tag examples — the Packages step shows a `bridge-v<ver>` /
  // `verify-v<ver>` placeholder + hint so the version-pin field's example
  // matches the default the Rust planner derives from version.json. Kept in
  // sync so a stale example never implies a nonexistent tag. The Rust default
  // tags themselves are compile-time-derived (build.rs reads version.json), so
  // this only guards the UI-facing example literals.
  {
    file: "hub/src/lib/components/wizard/WizardStep3Packages.svelte",
    kind: "wizard-tag",
    description:
      "wizard Packages step bridge-v / verify-v tag example literals (placeholder + hint)",
    replace: replaceWizardTags,
  },
  // npm package pins — the unity-open-mcp server shares the trio version, so
  // the @<version> suffix in every install snippet is generated from
  // version.json too. Add a target here for any doc that shows a pinned
  // `npx -y unity-open-mcp@<ver>` or `npx unity-open-mcp@<ver>` invocation.
  {
    file: "docs/setup/manual-setup.md",
    kind: "md-npm",
    description: "manual-setup.md npm server pins (unity-open-mcp@<ver>)",
    replace: replaceNpmPin,
  },
  {
    file: "docs/setup/agent-setup.md",
    kind: "md-npm",
    description: "agent-setup.md npm server pins (unity-open-mcp@<ver>)",
    replace: replaceNpmPin,
  },
  {
    file: "docs/setup/wizard-setup.md",
    kind: "md-npm",
    description: "wizard-setup.md npm server pins (unity-open-mcp@<ver>)",
    replace: replaceNpmPin,
  },
  {
    file: "mcp-server/README.md",
    kind: "md-npm",
    description: "mcp-server/README.md npm server pins (unity-open-mcp@<ver>)",
    replace: replaceNpmPin,
  },
  {
    file: "docs/api/mcp-tools.md",
    kind: "md-npm",
    description: "docs/api/mcp-tools.md npm server pin (unity-open-mcp@<ver>)",
    replace: replaceNpmPin,
  },
  {
    file: "docs/ci/github-actions/unity-verify.yml",
    kind: "md-npm",
    description: "GitHub Actions CI template npm pins (unity-open-mcp@<ver>)",
    replace: replaceNpmPin,
  },
  {
    file: "docs/ci/gitlab-ci/unity-verify.yml",
    kind: "md-npm",
    description: "GitLab CI template npm pins (unity-open-mcp@<ver>)",
    replace: replaceNpmPin,
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
  if (kind === "md-git") {
    // First #bridge-v<X.Y.Z> pin in the doc (setup/*.md git-URL examples).
    const m = body.match(/#bridge-v(\d+\.\d+\.\d+)/);
    return m ? m[1] : undefined;
  }
  if (kind === "wizard-tag") {
    // First bare bridge-v<X.Y.Z> literal in the wizard Packages step
    // (placeholder + hint example).
    const m = body.match(/bridge-v(\d+\.\d+\.\d+)/);
    return m ? m[1] : undefined;
  }
  if (kind === "md-npm") {
    // First unity-open-mcp@<X.Y.Z> pin in the doc (npm install examples).
    const m = body.match(/unity-open-mcp@(\d+\.\d+\.\d+)/);
    return m ? m[1] : undefined;
  }
  if (kind === "json-dep") {
    // The bridge → verify dependency pin (com.alexeyperov.unity-open-mcp-verify).
    const m = body.match(
      /"com\.alexeyperov\.unity-open-mcp-verify"\s*:\s*"([^"]+)"/,
    );
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
// Tags
// ---------------------------------------------------------------------------
// Tag namespaces (see docs/versioning.md §Release channels and tag namespaces):
//   trio → v<X.Y.Z> (triggers npm-publish.yml), plus bridge-v<X.Y.Z> and
//          verify-v<X.Y.Z> so the UPM git-URL install pins resolve.
//   hub  → hub-v<X.Y.Z> (triggers hub-release.yml).
// Existing tags are annotated (`git cat-file -t` → `tag`) with empty message
// bodies; new tags match that convention.

/** @param {string} v @returns {string[]} */
function trioTagNames(v) {
  return [`v${v}`, `bridge-v${v}`, `verify-v${v}`];
}

/** @param {string} v @returns {string[]} */
function hubTagNames(v) {
  return [`hub-v${v}`];
}

/** Runs `git rev-parse --verify --quiet refs/tags/<name>`; true if the tag exists.
 * @param {string} name */
function tagExists(name) {
  try {
    execFileSync("git", ["rev-parse", "--verify", "--quiet", `refs/tags/${name}`], {
      cwd: REPO_ROOT,
      stdio: ["ignore", "pipe", "pipe"],
    });
    // Exit code 0 ⇒ ref resolved ⇒ tag exists.
    return true;
  } catch {
    // Non-zero ⇒ ref did not resolve ⇒ tag does not exist.
    return false;
  }
}

/** Creates annotated tags with empty message bodies on HEAD, matching the
 *  existing convention. Throws on `git` failure (caller reports and exits).
 * @param {string[]} names */
function createTags(names) {
  // `git tag -a -m "" a b c` creates three annotated tags on HEAD in one call.
  // Note: git tag -a -m "" <tags...> fails if any tag name is empty or if there are too many arguments.
  // We create tags one by one to avoid "too many arguments" or other shell-related issues.
  for (const name of names) {
    execFileSync("git", ["tag", "-a", "-m", "", name], {
      cwd: REPO_ROOT,
      stdio: ["ignore", "pipe", "pipe"],
    });
  }
}

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------

const argv = process.argv.slice(2);
const CHECK = argv.includes("--check");
const HUB = argv.includes("--hub");
const bumpIdx = argv.indexOf("bump");
const setIdx = argv.indexOf("set");
const tagsIdx = argv.indexOf("tags");
const isBump = bumpIdx !== -1;
const isSet = setIdx !== -1;
const isTags = tagsIdx !== -1;
const bumpLevel = isBump ? argv[bumpIdx + 1] : undefined;
const setRaw = isSet ? argv[setIdx + 1] : undefined;
const tagsRaw = isTags ? argv[tagsIdx + 1] : undefined;

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
// `tags` takes the same X.Y.Z shape as `set`.
const tagsVersion =
  isTags && typeof tagsRaw === "string" && /^v?\d+\.\d+\.\d+$/.test(tagsRaw)
    ? tagsRaw.replace(/^v/, "")
    : undefined;
if (isTags && tagsVersion === undefined) {
  console.error('Usage: tags <X.Y.Z> where X.Y.Z is plain major.minor.patch');
  process.exit(2);
}
const actionCount = [isBump, isSet, isTags].filter(Boolean).length;
if (actionCount > 1) {
  console.error("bump, set, and tags are mutually exclusive.");
  process.exit(2);
}
if (CHECK && (isBump || isSet || isTags)) {
  console.error("--check is mutually exclusive with bump, set, and tags.");
  process.exit(2);
}

const sourceFile = HUB ? HUB_SOURCE : TRIO_SOURCE;
const targets = HUB ? HUB_TARGETS : TRIO_TARGETS;
const label = HUB ? "Hub app" : "shared trio";

// Tags path: create the release tag(s) for the current source version on HEAD.
// Does not push — pushing v*/hub-v* triggers the irreversible publish/release
// workflows, so the exact `git push origin ...` command is printed for review.
if (isTags) {
  /** @type {string} */
  const tagsVer = /** @type {string} */ (tagsVersion);
  const source = readSourceVersion(sourceFile);
  if (tagsVer !== source) {
    console.error(
      `tags: ${tagsVer} does not match ${sourceFile} (${source}). Commit the version bump before tagging.`,
    );
    process.exit(2);
  }
  const names = HUB ? hubTagNames(tagsVer) : trioTagNames(tagsVer);
  const existing = names.filter(tagExists);
  if (existing.length > 0) {
    console.error(
      `✖ Refusing to create tags — the following already exist: ${existing.join(", ")}`,
    );
    process.exit(1);
  }
  try {
    createTags(names);
  } catch (e) {
    console.error(
      `✖ \`git tag\` failed:${e && typeof e === "object" && "stderr" in e ? `\n${String(e.stderr)}` : ` ${e}`}`,
    );
    process.exit(1);
  }
  console.log(`Created ${names.length} tag${names.length > 1 ? "s" : ""} on HEAD for ${label} ${tagsVer}:`);
  for (const n of names) {
    console.log(`  ${n}`);
  }
  console.log(
    `\nPush to publish (triggers the ${HUB ? "hub-release" : "npm-publish"} workflow):`,
  );
  console.log(`  git push origin ${names.join(" ")}`);
  process.exit(0);
}

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
  const nextNames = HUB ? hubTagNames(next) : trioTagNames(next);
  console.log("\nNext: review the diff, commit, then create release tags.");
  console.log(
    `  git add -A && git commit -m "chore: ${HUB ? "hub bump" : "bump"} to ${next}"`,
  );
  console.log(`  node scripts/sync-version.mjs tags ${next}${HUB ? " --hub" : ""}`);
  if (!HUB) {
    // The trio release needs three tags on the same commit: v* (triggers
    // npm-publish.yml) plus bridge-v* / verify-v* so the UPM git-URL install
    // pins documented in docs/setup/*.md resolve. See docs/versioning.md
    // §Release channels and tag namespaces.
    console.log(`\nTags this will create (all on the same commit):`);
    console.log(`  ${nextNames[0]}          — triggers npm-publish.yml (publishes the MCP server)`);
    console.log(`  ${nextNames[1]}   — resolves the bridge UPM git-URL pin in docs/setup/`);
    console.log(`  ${nextNames[2]}   — resolves the verify UPM git-URL pin in docs/setup/`);
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
