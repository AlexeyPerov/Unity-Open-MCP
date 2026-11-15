// Package sources for offline script-GUID resolution.
//
// Unity resolves a component's script GUID (m_Script) wherever the script
// lives: under Assets/, or in a package — embedded (a folder under
// Packages/), local (a `file:` reference in Packages/manifest.json), fetched
// into Library/PackageCache (registry and git packages and, from Unity 6,
// built-in ones), or a built-in package read from the Editor install
// (Data/Resources/PackageManager/BuiltInPackages). The offline readers walk
// Assets/ for every GUID; a script GUID Assets/ does not own is looked up
// here, in the package `.cs` / `.dll` metas only (a component's script is
// one or the other), and the walk stops once every wanted GUID is found.
//
// A package the project uses whose files are not on disk where these reads
// look (no Library/ in a fresh clone, a built-in package of a Unity version
// not installed here, a `file:` path that does not exist) makes a script
// GUID that resolves nowhere unverifiable: the readers report it as
// unverified with that reason, not as a missing script.
//
// Nothing here is cached: each call reads the manifest and lists the package
// folders again (the same no-cache contract as the Assets/ walk).

import { open, readFile, readdir, stat } from "node:fs/promises";
import type { Dirent } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { readMetaGUID } from "./primitives.js";
import { skipDirs } from "./types.js";
import { readProjectUnityVersion, resolveUnityPathForProject } from "../unity-install-discovery.js";

/** One package's files on disk. Unity asset paths inside it are `Packages/<name>/…`. */
export interface PackageSource {
  name: string;
  /** Absolute directory holding the package (its package.json). */
  dir: string;
}

export interface PackageSources {
  sources: PackageSource[];
  /** Packages the project uses that are not on disk where offline reads look, with why. */
  missing: Array<{ name: string; why: string }>;
}

export interface PackageScriptHit {
  /** `Packages/<name>/<path inside the package>` of the `.cs` / `.dll` the GUID belongs to. */
  path: string;
  /** Lowercased file name without extension (the component query matches it). */
  name: string;
}

interface ManifestEntry {
  version: string;
  source?: string;
}

async function readJson(path: string): Promise<Record<string, unknown> | null> {
  try {
    const value = JSON.parse(await readFile(path, "utf-8"));
    return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

async function isDirectory(path: string): Promise<boolean> {
  try { return (await stat(path)).isDirectory(); } catch { return false; }
}

async function listDirs(dir: string): Promise<Dirent[] | null> {
  try {
    return (await readdir(dir, { withFileTypes: true })).filter((e) => e.isDirectory() || e.isSymbolicLink());
  } catch {
    return null;
  }
}

/** Unity ignores hidden folders and folders ending in `~` (Documentation~, Samples~). */
function ignoredByUnity(name: string): boolean {
  return name.startsWith(".") || name.endsWith("~");
}

/**
 * The BuiltInPackages folder of the Unity install the project names in
 * ProjectSettings/ProjectVersion.txt, or null when that exact version is not
 * installed here (built-in packages differ per version, so no fallback).
 */
function builtInPackagesDirFor(projectRoot: string): string | null {
  const version = readProjectUnityVersion(projectRoot);
  if (!version) return null;
  const resolution = resolveUnityPathForProject(version);
  if (!resolution.ok) return null;
  const exeDir = dirname(resolution.value.path);
  // macOS: <ver>/Unity.app/Contents/MacOS/Unity → Contents/Resources/…;
  // Windows / Linux: <ver>/Editor/Unity[.exe] → Editor/Data/Resources/….
  return process.platform === "darwin"
    ? join(exeDir, "..", "Resources", "PackageManager", "BuiltInPackages")
    : join(exeDir, "Data", "Resources", "PackageManager", "BuiltInPackages");
}

/**
 * Where the packages a project uses live on disk. The package set is
 * Packages/packages-lock.json (every resolved package, transitive ones
 * included) plus Packages/manifest.json; each is found as an embedded folder,
 * a `file:` directory, a Library/PackageCache folder (`<name>@<hash>` or
 * `<name>`), or a built-in package of the project's Unity install.
 *
 * `builtInPackagesDir`: test seam; undefined looks the install up.
 */
export async function discoverPackageSources(
  projectRoot: string,
  opts: { builtInPackagesDir?: string | null } = {},
): Promise<PackageSources> {
  const packagesDir = join(projectRoot, "Packages");
  const expected = new Map<string, ManifestEntry>();
  const lock = (await readJson(join(packagesDir, "packages-lock.json")))?.dependencies;
  if (lock && typeof lock === "object") {
    for (const [name, value] of Object.entries(lock as Record<string, unknown>)) {
      if (!value || typeof value !== "object") continue;
      const v = value as Record<string, unknown>;
      expected.set(name, {
        version: typeof v.version === "string" ? v.version : "",
        source: typeof v.source === "string" ? v.source : undefined,
      });
    }
  }
  const manifest = (await readJson(join(packagesDir, "manifest.json")))?.dependencies;
  if (manifest && typeof manifest === "object") {
    for (const [name, value] of Object.entries(manifest as Record<string, unknown>)) {
      if (!expected.has(name) && typeof value === "string") expected.set(name, { version: value });
    }
  }

  const sources = new Map<string, string>();
  // Embedded: any folder under Packages/ with a package.json, listed or not.
  for (const entry of (await listDirs(packagesDir)) ?? []) {
    if (ignoredByUnity(entry.name)) continue;
    const dir = join(packagesDir, entry.name);
    const pkg = await readJson(join(dir, "package.json"));
    if (!pkg) continue;
    const name = typeof pkg.name === "string" && pkg.name !== "" ? pkg.name : entry.name;
    if (!sources.has(name)) sources.set(name, dir);
  }

  const cacheDir = join(projectRoot, "Library", "PackageCache");
  const cacheEntries = await listDirs(cacheDir);
  const cached = new Map<string, string[]>();
  for (const entry of cacheEntries ?? []) {
    const at = entry.name.indexOf("@");
    const name = at > 0 ? entry.name.slice(0, at) : entry.name;
    const list = cached.get(name);
    if (list) list.push(join(cacheDir, entry.name));
    else cached.set(name, [join(cacheDir, entry.name)]);
  }

  let builtInDir: string | null | undefined = opts.builtInPackagesDir;
  const missing: Array<{ name: string; why: string }> = [];
  for (const [name, entry] of expected) {
    if (sources.has(name)) continue;
    if (entry.version.startsWith("file:")) {
      const target = resolve(packagesDir, entry.version.slice("file:".length));
      if (await isDirectory(target)) { sources.set(name, target); continue; }
      if (!/\.(tgz|tar\.gz)$/i.test(target)) {
        missing.push({ name, why: `${entry.version} not found` });
        continue;
      }
      // A tarball is extracted into Library/PackageCache: fall through.
    }
    const candidates = cached.get(name);
    if (candidates && candidates.length > 0) {
      sources.set(name, await newest(candidates));
      continue;
    }
    if (entry.source === "builtin") {
      if (builtInDir === undefined) builtInDir = builtInPackagesDirFor(projectRoot);
      const dir = builtInDir ? join(builtInDir, name) : null;
      if (dir && await isDirectory(dir)) { sources.set(name, dir); continue; }
    }
    missing.push({
      name,
      why: cacheEntries === null
        ? "Library/PackageCache does not exist yet (Unity has not resolved the project's packages here)"
        : entry.source === "builtin"
          ? "a built-in package not in Library/PackageCache, and the project's Unity version is not installed here"
          : "not in Library/PackageCache",
    });
  }

  return {
    sources: [...sources].map(([name, dir]) => ({ name, dir })),
    missing,
  };
}

/** The most recently modified of several cache folders for one package (stale copies can linger). */
async function newest(dirs: string[]): Promise<string> {
  if (dirs.length === 1) return dirs[0];
  let best = dirs[0];
  let bestTime = -1;
  for (const dir of dirs) {
    try {
      const t = (await stat(dir)).mtimeMs;
      if (t > bestTime) { best = dir; bestTime = t; }
    } catch { /* skip */ }
  }
  return best;
}

/** One line naming the packages that are not on disk, for an unverified result. */
export function describeMissingPackages(missing: PackageSources["missing"]): string {
  if (missing.length === 0) return "";
  const shown = missing.slice(0, 3).map((m) => `${m.name}: ${m.why}`).join("; ");
  const more = missing.length > 3 ? `; and ${missing.length - 3} more` : "";
  return `${missing.length} package(s) the project uses are not on disk where offline reads look (${shown}${more})`;
}

/** The GUID of a .meta file from its first bytes (the guid is its second line). */
async function readMetaHeadGUID(metaPath: string): Promise<string> {
  let handle;
  try {
    handle = await open(metaPath, "r");
    const buffer = Buffer.alloc(256);
    const { bytesRead } = await handle.read(buffer, 0, buffer.length, 0);
    return readMetaGUID(buffer.toString("utf-8", 0, bytesRead)).toLowerCase();
  } catch {
    return "";
  } finally {
    await handle?.close().catch(() => undefined);
  }
}

const WALK_PERMITS = 64;

/**
 * Find script GUIDs in the given packages: reads only `.cs.meta` and
 * `.dll.meta` files. `wanted` (lowercased GUIDs) stops the walk once all are
 * found; `componentQuery` (a lowercased name substring) additionally collects
 * every `.cs` whose file name contains it, and then walks every package.
 */
export async function findPackageScripts(
  sources: readonly PackageSource[],
  opts: { wanted?: ReadonlySet<string>; componentQuery?: string },
): Promise<Map<string, PackageScriptHit>> {
  const hits = new Map<string, PackageScriptHit>();
  const pending = new Set(opts.wanted ?? []);
  const query = opts.componentQuery;
  if (sources.length === 0 || (pending.size === 0 && query === undefined)) return hits;
  const done = () => pending.size === 0 && query === undefined;

  let active = 0;
  const waiters: Array<() => void> = [];
  const withPermit = async <T>(fn: () => Promise<T>): Promise<T> => {
    while (active >= WALK_PERMITS) await new Promise<void>((r) => waiters.push(r));
    active++;
    try { return await fn(); } finally { active--; waiters.shift()?.(); }
  };

  const visit = async (dir: string, prefix: string): Promise<void> => {
    if (done()) return;
    let entries: Dirent[];
    try { entries = await withPermit(() => readdir(dir, { withFileTypes: true })); } catch { return; }
    await Promise.all(entries.map(async (entry) => {
      if (done() || ignoredByUnity(entry.name)) return;
      const full = join(dir, entry.name);
      let isDir = entry.isDirectory();
      if (entry.isSymbolicLink()) isDir = await isDirectory(full);
      if (isDir) {
        if (!skipDirs.has(entry.name)) await visit(full, `${prefix}/${entry.name}`);
        return;
      }
      const isCs = entry.name.endsWith(".cs.meta");
      if (!isCs && !entry.name.endsWith(".dll.meta")) return;
      const fileName = entry.name.slice(0, -".meta".length);
      const name = fileName.slice(0, fileName.lastIndexOf(".")).toLowerCase();
      const byName = isCs && query !== undefined && name.includes(query);
      if (pending.size === 0 && !byName) return;
      const guid = await withPermit(() => readMetaHeadGUID(full));
      if (guid === "") return;
      if (byName || pending.has(guid)) {
        hits.set(guid, { path: `${prefix}/${fileName}`, name });
        pending.delete(guid);
      }
    }));
  };

  await Promise.all(sources.map((s) => visit(s.dir, `Packages/${s.name}`)));
  return hits;
}

/**
 * Confirm that `Packages/<name>/<rel>` still holds the given GUID by reading
 * that one meta, without a walk. Used to revalidate a resolution made earlier.
 */
export async function packagePathHasGuid(
  sources: readonly PackageSource[],
  assetPath: string,
  guid: string,
): Promise<boolean> {
  if (!assetPath.startsWith("Packages/")) return false;
  const rest = assetPath.slice("Packages/".length);
  for (const source of sources) {
    if (!rest.startsWith(source.name + "/")) continue;
    const rel = rest.slice(source.name.length + 1);
    if (rel.split("/").some((part) => part === ".." || part === "")) return false;
    return (await readMetaHeadGUID(join(source.dir, ...rel.split("/")) + ".meta")) === guid;
  }
  return false;
}
