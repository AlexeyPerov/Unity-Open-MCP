#!/usr/bin/env node
// switch-project-version.mjs — move a CONSUMING project onto a Unity Open MCP release.
//
// This is the mirror image of sync-version.mjs. That script owns version
// strings inside THIS repository (generated from version.json); this one
// rewrites the version pins a *user's* project carries:
//
//   1. Agent / MCP client configs — every `unity-open-mcp@<X.Y.Z>` npm pin, in
//      whatever client config files the project actually has (`.cursor/mcp.json`,
//      `.codex/config.toml`, `.zcode/cli/config.json`, `.mcp.json`,
//      `.vscode/mcp.json`, `opencode.json`, …). The scan is content-driven
//      rather than catalog-driven so a client we have not enumerated yet is
//      still covered.
//   2. UPM package pins — `#bridge-v<X.Y.Z>` / `#verify-v<X.Y.Z>` git-URL
//      fragments in `Packages/manifest.json`.
//   3. `Packages/packages-lock.json` — the same git-URL fragments, the
//      bridge → verify dependency pin, and the now-stale resolved `hash` for
//      the two entries (dropped so UPM re-resolves against the new tag instead
//      of reusing the old commit).
//
// The Unity project does NOT have to sit at the path you pass: the scan
// descends into subdirectories (`<repo>/Client`, `<repo>/unity/Client`, …), so
// pointing at the repository root updates the agent configs at the top and the
// UPM pins below in one pass.
//
// Usage:
//   node scripts/switch-project-version.mjs <project-path> <X.Y.Z> [options]
//
//   --dry-run       Report what would change; write nothing.
//   --up <N>        Also rewrite agent configs in up to N ancestor directories
//                   above <project-path> (default 0). Use this when you point
//                   at the Unity project itself and the client configs live in
//                   the repository root above it. Ancestor scans only look
//                   inside agent config directories, so a sibling project is
//                   never touched.
//   --depth <N>     How deep to descend below <project-path> (default 4).
//   --keep-lock     Leave Packages/packages-lock.json untouched (Unity will
//                   re-resolve it on the next open).
//   --json          Machine-readable report on stdout instead of the summary.
//   -h, --help      Show usage.
//
// <X.Y.Z> is plain major.minor.patch; a leading "v" is tolerated and stripped.
// Every rewrite is idempotent, so re-running against the same version is a
// no-op. Markdown and other prose are deliberately not rewritten — a
// changelog or review note that mentions an old version should keep saying so.
//
// Requires Node 18+ (node: builtins only, no dependencies).

import { readFileSync, writeFileSync, readdirSync, statSync, existsSync } from "node:fs";
import { dirname, resolve, join, relative, basename, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { homedir } from "node:os";

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");

// ---------------------------------------------------------------------------
// Scan policy
// ---------------------------------------------------------------------------

/** Files we are willing to open. Config formats only — see the note about
 *  markdown in the header. */
const SCANNED_EXTENSIONS = [".json", ".jsonc", ".json5", ".toml", ".yaml", ".yml"];

/** Directory names never worth descending into: VCS metadata, Unity-generated
 *  trees (Library holds PackageCache — copies of the very packages we are
 *  re-pinning), build output, and dependency caches. `Assets` /
 *  `ProjectSettings` are pruned because no MCP client or UPM manifest lives
 *  there and they are the two biggest trees in a real project. */
const PRUNED_DIRS = new Set([
  ".git", ".svn", ".hg", ".jj",
  "Library", "Temp", "Logs", "Build", "Builds", "Assets", "ProjectSettings",
  "PackageCache", "node_modules", "obj", "bin", "dist", "target",
  ".gradle", ".venv", ".idea", ".cache",
]);

/** Agent config directories an ancestor scan may descend into. An ancestor
 *  holds sibling projects we must not touch, so its walk is restricted to
 *  these instead of the general prune list. */
const AGENT_CONFIG_DIRS = new Set([
  ".cursor", ".codex", ".zcode", ".claude", ".agents", ".vscode", ".vs",
  ".junie", ".gemini", ".kilocode", ".roo", ".github", ".config",
]);

/** Skip pathologically large files — a pin never lives in one, and reading it
 *  would just stall the scan. */
const MAX_FILE_BYTES = 8 * 1024 * 1024;

const BRIDGE_PACKAGE_ID = "com.alexeyperov.unity-open-mcp-bridge";
const VERIFY_PACKAGE_ID = "com.alexeyperov.unity-open-mcp-verify";

// ---------------------------------------------------------------------------
// Pin rewriters
// ---------------------------------------------------------------------------
// Each rewriter is a pure (body, version) → { body, from, count } function.
// `from` is the first pre-existing version it saw (for the report); `count` is
// how many literals it touched, changed or not.

/** @typedef {{ id: string, label: string, rewrite: (body: string, v: string) => { body: string, versions: string[] } }} Rewriter */

/** Collect every distinct version a pattern matches, then rewrite them all.
 * @param {string} body
 * @param {RegExp} re global regex whose group 1 is the literal prefix and
 *                    group 2 the X.Y.Z (and optional group 3 a suffix)
 * @param {string} v
 */
function rewritePattern(body, re, v) {
  /** @type {string[]} */
  const versions = [];
  const out = body.replace(re, (...args) => {
    // String.replace hands the callback (match, p1…pn, offset, source), so the
    // capture groups are everything between the match and the trailing pair —
    // reading a fixed arity would pick up the offset as a "group".
    const groups = args.slice(1, -2);
    const [pre = "", found = "", post = ""] = groups;
    versions.push(found);
    return `${pre}${v}${post}`;
  });
  return { body: out, versions };
}

/** @type {Rewriter[]} */
const REWRITERS = [
  {
    id: "npm",
    label: "npm pin",
    // `unity-open-mcp@0.8.4` in an MCP client config's args / command.
    // `@latest` and the bare package name are intentionally not matched.
    rewrite: (body, v) =>
      rewritePattern(body, /(unity-open-mcp@)(\d+\.\d+\.\d+)/g, v),
  },
  {
    id: "upm-tag",
    label: "UPM git pin",
    // `…?path=packages/bridge#bridge-v0.8.4` in manifest.json / packages-lock.json.
    rewrite: (body, v) =>
      rewritePattern(body, /(#(?:bridge|verify)-v)(\d+\.\d+\.\d+)/g, v),
  },
  {
    id: "verify-dep",
    label: "verify dependency pin",
    // The bridge's declared dependency on verify, recorded in packages-lock.json.
    // Must track the trio version or a git-URL install of both fails to resolve.
    rewrite: (body, v) =>
      rewritePattern(
        body,
        new RegExp(`("${VERIFY_PACKAGE_ID.replace(/\./g, "\\.")}"\\s*:\\s*")(\\d+\\.\\d+\\.\\d+)(")`, "g"),
        v,
      ),
  },
];

// ---------------------------------------------------------------------------
// packages-lock.json — stale resolved hash
// ---------------------------------------------------------------------------
// UPM records the commit it resolved a git dependency to as `hash`. Rewriting
// only the `version` string would leave manifest and lock agreeing on the new
// tag while the lock still names the OLD commit, and Unity would happily reuse
// it — the upgrade would silently not happen. Dropping `hash` for the two
// entries we re-pinned forces a re-resolve; UPM writes the new value back on
// the next open.

/** @param {string} s */
function escapeRegex(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** Locate the object value of `"key": { … }` by brace matching (string-aware).
 * The key must be followed by an object, not a string: `packages-lock.json`
 * mentions the verify package id twice — once as the bridge entry's dependency
 * (a version string) and once as its own top-level entry — and only the latter
 * is the span we want.
 * @param {string} body @param {string} key
 * @returns {{ start: number, end: number } | null} span of the `{ … }`, or null
 */
function findObjectSpan(body, key) {
  const m = new RegExp(`"${escapeRegex(key)}"\\s*:\\s*\\{`).exec(body);
  if (!m) return null;
  const open = m.index + m[0].length - 1;
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let i = open; i < body.length; i++) {
    const c = body[i];
    if (inString) {
      if (escaped) escaped = false;
      else if (c === "\\") escaped = true;
      else if (c === '"') inString = false;
      continue;
    }
    if (c === '"') inString = true;
    else if (c === "{") depth++;
    else if (c === "}") {
      depth--;
      if (depth === 0) return { start: open, end: i + 1 };
    }
  }
  return null;
}

/** Remove the `"hash": "…"` property from a package entry, preserving the
 *  surrounding formatting (handles hash as last or middle property).
 * @param {string} body @param {string} packageId
 * @returns {{ body: string, removed: boolean }}
 */
function stripLockHash(body, packageId) {
  const span = findObjectSpan(body, packageId);
  if (!span) return { body, removed: false };
  const entry = body.slice(span.start, span.end);
  // Trailing property: drop the preceding comma with it.
  let next = entry.replace(/,[ \t]*\r?\n[ \t]*"hash"[ \t]*:[ \t]*"[^"]*"/, "");
  if (next === entry) {
    // Middle property: drop it plus its own trailing comma and newline.
    next = entry.replace(/[ \t]*"hash"[ \t]*:[ \t]*"[^"]*",[ \t]*\r?\n/, "");
  }
  if (next === entry) return { body, removed: false };
  return { body: body.slice(0, span.start) + next + body.slice(span.end), removed: true };
}

/** Read the `version` string of a package entry, if present.
 * @param {string} body @param {string} packageId
 */
function lockEntryVersion(body, packageId) {
  const span = findObjectSpan(body, packageId);
  if (!span) return null;
  const m = /"version"\s*:\s*"([^"]*)"/.exec(body.slice(span.start, span.end));
  return m ? m[1] : null;
}

// ---------------------------------------------------------------------------
// File processing
// ---------------------------------------------------------------------------

/**
 * @typedef {{ path: string, changes: Array<{ id: string, label: string, from: string[], count: number }>,
 *             alreadyCurrent: boolean, hashesDropped: number }} FileResult
 */

/** Apply every rewriter (plus the lock-hash pass) to one file.
 * @param {string} absPath
 * @param {string} version
 * @param {{ dryRun: boolean, keepLock: boolean }} opts
 * @returns {FileResult | null} null when the file carries no pin at all
 */
function processFile(absPath, version, opts) {
  if (opts.keepLock && basename(absPath) === "packages-lock.json") return null;
  let original;
  try {
    original = readFileSync(absPath, "utf8");
  } catch {
    return null; // unreadable (permissions, race) — nothing to report
  }

  let body = original;
  /** @type {FileResult["changes"]} */
  const changes = [];
  let sawPin = false;

  for (const r of REWRITERS) {
    const res = r.rewrite(body, version);
    if (res.versions.length === 0) continue;
    sawPin = true;
    const stale = res.versions.filter((v) => v !== version);
    if (stale.length > 0) {
      changes.push({
        id: r.id,
        label: r.label,
        from: [...new Set(stale)],
        count: stale.length,
      });
    }
    body = res.body;
  }

  if (!sawPin) return null;

  // Lock files only: drop the resolved hash of any entry whose version string
  // we just moved, so UPM re-resolves instead of reusing the old commit.
  let hashesDropped = 0;
  if (basename(absPath) === "packages-lock.json") {
    for (const id of [BRIDGE_PACKAGE_ID, VERIFY_PACKAGE_ID]) {
      if (lockEntryVersion(original, id) === lockEntryVersion(body, id)) continue;
      const res = stripLockHash(body, id);
      if (res.removed) {
        body = res.body;
        hashesDropped++;
      }
    }
    if (hashesDropped > 0) {
      changes.push({
        id: "lock-hash",
        label: "stale resolved hash dropped",
        from: [],
        count: hashesDropped,
      });
    }
  }

  if (body !== original && !opts.dryRun) {
    writeFileSync(absPath, body);
  }

  return {
    path: absPath,
    changes,
    alreadyCurrent: changes.length === 0,
    hashesDropped,
  };
}

// ---------------------------------------------------------------------------
// Directory walking
// ---------------------------------------------------------------------------

/** @param {string} name */
function isScannedFile(name) {
  const lower = name.toLowerCase();
  return SCANNED_EXTENSIONS.some((ext) => lower.endsWith(ext));
}

/** @param {string} name */
function isPrunedDir(name) {
  // `.venv`, `.venv-fbx`, … are all dependency caches.
  return PRUNED_DIRS.has(name) || name.startsWith(".venv");
}

/**
 * Collect scannable files under `root`.
 * @param {string} root
 * @param {{ maxDepth: number, agentDirsOnly: boolean }} opts
 *        agentDirsOnly restricts descent to AGENT_CONFIG_DIRS — used for
 *        ancestor scans, where the siblings of our project also live.
 * @returns {string[]} absolute file paths
 */
function collectFiles(root, opts) {
  /** @type {string[]} */
  const found = [];
  /** @param {string} dir @param {number} depth */
  const walk = (dir, depth) => {
    /** @type {import("node:fs").Dirent[]} */
    let entries;
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch {
      return; // unreadable directory — skip quietly
    }
    for (const e of entries) {
      const child = join(dir, e.name);
      if (e.isDirectory()) {
        // Dirent.isDirectory() is false for symlinks, so links are skipped and
        // the walk cannot cycle.
        if (depth >= opts.maxDepth) continue;
        if (isPrunedDir(e.name)) continue;
        if (opts.agentDirsOnly && depth === 0 && !AGENT_CONFIG_DIRS.has(e.name)) continue;
        walk(child, depth + 1);
      } else if (e.isFile() && isScannedFile(e.name)) {
        try {
          if (statSync(child).size > MAX_FILE_BYTES) continue;
        } catch {
          continue;
        }
        found.push(child);
      }
    }
  };
  walk(root, 0);
  return found;
}

/** Directories that hold a Unity project (a `Packages/manifest.json`).
 * @param {string[]} files absolute paths from collectFiles
 * @returns {string[]}
 */
function unityProjectRoots(files) {
  /** @type {Set<string>} */
  const roots = new Set();
  for (const f of files) {
    if (basename(f) !== "manifest.json") continue;
    const packagesDir = dirname(f);
    if (basename(packagesDir) !== "Packages") continue;
    roots.add(dirname(packagesDir));
  }
  return [...roots].sort();
}

/** Ancestor directories of `root`, nearest first, excluding the home directory
 *  and the filesystem root. Home-scoped client configs (`~/.cursor/mcp.json`)
 *  are a machine-wide surface, not this project's — they stay out of scope.
 * @param {string} root @param {number} levels
 */
function ancestorsOf(root, levels) {
  /** @type {string[]} */
  const out = [];
  const home = resolve(homedir());
  let current = root;
  for (let i = 0; i < levels; i++) {
    const parent = dirname(current);
    if (parent === current) break; // filesystem root
    if (parent === home) break;
    out.push(parent);
    current = parent;
  }
  return out;
}

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------

const USAGE = `Usage: node scripts/switch-project-version.mjs <project-path> <X.Y.Z> [options]

  --dry-run     Report what would change; write nothing.
  --up <N>      Also rewrite agent configs in up to N ancestor directories
                above <project-path> (default 0).
  --depth <N>   How deep to descend below <project-path> (default 4).
  --keep-lock   Leave Packages/packages-lock.json untouched.
  --json        Machine-readable report instead of the human summary.
  -h, --help    Show this message.

Example:
  node scripts/switch-project-version.mjs ~/work/my-game 0.9.0`;

/** @param {string[]} argv */
function parseArgs(argv) {
  /** @type {string[]} */
  const positional = [];
  let dryRun = false;
  let keepLock = false;
  let json = false;
  let up = 0;
  let depth = 4;

  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--dry-run") dryRun = true;
    else if (a === "--keep-lock") keepLock = true;
    else if (a === "--json") json = true;
    else if (a === "-h" || a === "--help") return { help: true };
    else if (a === "--up" || a === "--depth") {
      const raw = argv[++i];
      const n = Number(raw);
      if (!Number.isInteger(n) || n < 0 || n > 12) {
        return { error: `${a} expects an integer 0..12 (got ${raw ?? "nothing"}).` };
      }
      if (a === "--up") up = n;
      else depth = n;
    } else if (a.startsWith("-")) {
      return { error: `Unknown option ${a}.` };
    } else {
      positional.push(a);
    }
  }

  if (positional.length !== 2) {
    return { error: "Expected exactly two arguments: <project-path> <X.Y.Z>." };
  }
  const [rawPath, rawVersion] = positional;
  if (!/^v?\d+\.\d+\.\d+$/.test(rawVersion)) {
    return { error: `"${rawVersion}" is not a plain major.minor.patch version.` };
  }
  return {
    projectPath: resolve(rawPath),
    version: rawVersion.replace(/^v/, ""),
    dryRun,
    keepLock,
    json,
    up,
    depth,
  };
}

const parsed = parseArgs(process.argv.slice(2));

if ("help" in parsed) {
  console.log(USAGE);
  process.exit(0);
}
if ("error" in parsed) {
  console.error(`✖ ${parsed.error}\n\n${USAGE}`);
  process.exit(2);
}

const { projectPath, version, dryRun, keepLock, json, up, depth } = parsed;

if (!existsSync(projectPath) || !statSync(projectPath).isDirectory()) {
  console.error(`✖ ${projectPath} is not a directory.`);
  process.exit(1);
}

// This repository's own version strings are generated from version.json — the
// pins below are a *consumer* surface. Warn rather than refuse: a maintainer
// may legitimately be testing against a fixture inside the tree.
const insideToolkit =
  projectPath === REPO_ROOT || projectPath.startsWith(REPO_ROOT + sep);

// --- scan -------------------------------------------------------------------

const rootFiles = collectFiles(projectPath, { maxDepth: depth, agentDirsOnly: false });
const ancestorDirs = ancestorsOf(projectPath, up);
/** @type {string[]} */
const ancestorFiles = [];
for (const dir of ancestorDirs) {
  // Depth 3 reaches `.junie/mcp/mcp.json`; agentDirsOnly keeps sibling
  // projects out of the walk.
  ancestorFiles.push(...collectFiles(dir, { maxDepth: 3, agentDirsOnly: true }));
}

const unityRoots = unityProjectRoots(rootFiles);

/** @type {FileResult[]} */
const results = [];
for (const f of [...rootFiles, ...ancestorFiles]) {
  const r = processFile(f, version, { dryRun, keepLock });
  if (r) results.push(r);
}

const updated = results.filter((r) => !r.alreadyCurrent);
const current = results.filter((r) => r.alreadyCurrent);

// A project pointed at the Unity folder itself keeps its client configs one
// level up, and the bridge window treats an ancestor config as configuring this
// project too. Probe read-only so the summary can name the flag that would
// catch them instead of silently leaving a stale pin behind.
/** @type {string[]} */
const missedAbove = [];
if (up === 0) {
  for (const dir of ancestorsOf(projectPath, 4)) {
    for (const f of collectFiles(dir, { maxDepth: 3, agentDirsOnly: true })) {
      try {
        const body = readFileSync(f, "utf8");
        const m = /unity-open-mcp@(\d+\.\d+\.\d+)/.exec(body);
        if (m && m[1] !== version) missedAbove.push(f);
      } catch {
        /* unreadable — ignore */
      }
    }
  }
}

// --- report -----------------------------------------------------------------

/** @param {string} p */
function display(p) {
  const rel = relative(projectPath, p);
  return rel.startsWith("..") ? p : rel;
}

if (json) {
  console.log(
    JSON.stringify(
      {
        projectPath,
        version,
        dryRun,
        unityProjects: unityRoots.map(display),
        files: results.map((r) => ({
          path: display(r.path),
          alreadyCurrent: r.alreadyCurrent,
          changes: r.changes,
        })),
        hintAncestorConfigs: missedAbove,
      },
      null,
      2,
    ),
  );
  process.exit(0);
}

console.log(`Unity Open MCP → ${version}${dryRun ? "  (dry run — nothing written)" : ""}`);
console.log(`  project: ${projectPath}`);
console.log(
  `  Unity project${unityRoots.length === 1 ? "" : "s"}: ${
    unityRoots.length ? unityRoots.map((r) => display(r) || ".").join(", ") : "(none found)"
  }`,
);
if (ancestorDirs.length > 0) {
  console.log(`  ancestors scanned: ${ancestorDirs.join(", ")}`);
}
console.log("");

if (insideToolkit) {
  console.warn(
    "  ⚠  This path is inside the Unity Open MCP repository. Version strings in\n" +
      "     this tree are generated from version.json — use\n" +
      "     `node scripts/sync-version.mjs` for the toolkit itself.\n",
  );
}

// One column per report, sized to the longest path but capped so a deeply
// nested ancestor path does not push the change columns off the terminal.
// Anything longer than the cap gets its own line.
const PATH_COL_MAX = 52;
const pathCol = Math.min(
  PATH_COL_MAX,
  Math.max(24, ...results.map((r) => display(r.path).length + 2)),
);

/** @param {string} label @param {string[]} rows */
function printRows(label, rows) {
  if (label.length + 2 > pathCol) {
    console.log(`  ${label}`);
    for (const row of rows) console.log(`  ${" ".repeat(pathCol)}${row}`);
    return;
  }
  rows.forEach((row, i) => {
    console.log(`  ${(i === 0 ? label : "").padEnd(pathCol)}${row}`);
  });
}

for (const r of updated) {
  printRows(
    display(r.path),
    r.changes.map((c) => {
      const arrow = c.from.length > 0 ? `${c.from.join(", ")} → ${version}` : "removed";
      const times = c.count > 1 ? ` ×${c.count}` : "";
      return `${c.label.padEnd(28)}${arrow}${times}`;
    }),
  );
}
if (updated.length > 0) console.log("");

for (const r of current) {
  printRows(display(r.path), [`already at ${version}`]);
}
if (current.length > 0) console.log("");

if (keepLock) {
  console.log(
    "  --keep-lock: packages-lock.json left alone — UPM re-resolves it on the\n" +
      "  next Unity open because the manifest no longer matches.\n",
  );
}

if (results.length === 0) {
  console.log("  No unity-open-mcp version pins found under this path.");
  console.log(
    `  Check the path, raise --depth (currently ${depth}), or see docs/setup/manual-setup.md\n` +
      "  if the project is not set up yet.\n",
  );
}

if (missedAbove.length > 0) {
  console.log("  Agent configs with a stale pin live ABOVE this path:");
  for (const f of missedAbove) console.log(`    ${f}`);
  console.log(`  Re-run with --up 2 to include them.\n`);
}

const verb = dryRun ? "would be updated" : "updated";
console.log(
  `${updated.length} file${updated.length === 1 ? "" : "s"} ${verb}, ` +
    `${current.length} already current.`,
);

if (!dryRun && updated.length > 0) {
  console.log("\nNext:");
  console.log("  • Restart your AI client(s) — most read MCP config only at startup.");
  if (updated.some((r) => r.changes.some((c) => c.id === "upm-tag" || c.id === "lock-hash"))) {
    console.log("  • Reopen Unity so UPM re-resolves the bridge / verify packages.");
  }
  console.log("  • Confirm with `npx unity-open-mcp status --project <unity-project>`.");
}
