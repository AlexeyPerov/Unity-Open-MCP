// Unity-install auto-discovery for the MCP server.
//
// Ports the *behavior* of the Hub's Rust discovery
// (`hub/src-tauri/src/config/discovery.rs::discover_unity_installations`)
// to TypeScript so `batch-spawn.ts` can find a Unity editor without a hard
// `UNITY_PATH` env requirement. The two processes do not share code today;
// this is an independent, TS-idiomatic port kept in sync with the Hub's
// OS-default paths and `UNITY_HUB` env override.
//
// Resolution precedence (see `resolveUnityPath`):
//   1. `UNITY_PATH` env var (explicit, validated to exist — wins)
//   2. auto-discovered installs, picking by `preferredVersion` prefix match
//      against the running bridge's `unityVersion` when available, else newest
//   3. null (caller surfaces `unity_not_discovered`)

import { existsSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";

/** A discovered Unity install: the editor executable path + its version folder name. */
export interface UnityInstall {
  /** Absolute path to the Unity executable (the thing you spawn). */
  path: string;
  /** Version string as named by the install folder (e.g. "6000.4.0f1", "2022.3.62f2"). */
  version: string;
}

export type UnityPathSource = "env" | "discovered";

export interface ResolvedUnityPath {
  path: string;
  version: string;
  source: UnityPathSource;
}

/**
 * OS-default Unity Hub install roots, matching the Hub's
 * `get_os_default_hub_paths()`. Each holds version-named subfolders
 * (`<root>/<version>/Unity.app|Editor/...`).
 */
export function defaultHubRoots(): string[] {
  if (process.platform === "win32") {
    return [join("C:", "Program Files", "Unity", "Hub", "Editor")];
  }
  if (process.platform === "darwin") {
    return ["/Applications/Unity/Hub/Editor"];
  }
  // Linux + fallback.
  return [join(homedir(), "Unity", "Hub", "Editor")];
}

/**
 * Roots actually scanned for installs: the OS defaults plus an optional
 * `UNITY_HUB` env override (the Hub honors the same env var to point at a
 * non-default install root). Deduped, existing-only.
 */
export function scannedHubRoots(): string[] {
  const roots = new Set<string>();
  for (const r of defaultHubRoots()) roots.add(r);
  const envHub = process.env.UNITY_HUB;
  if (envHub) roots.add(envHub);

  const out: string[] = [];
  for (const r of roots) {
    try {
      if (existsSync(r) && statSync(r).isDirectory()) out.push(r);
    } catch {
      // unreadable / missing — skip
    }
  }
  return out;
}

/**
 * Resolve the Unity executable path for a version-named install folder,
 * per-OS. Returns null when the expected binary is absent (folder exists
 * but is not a valid editor layout). Mirrors the Hub's `is_unity_editor_dir`
 * + path-to-executable logic.
 */
export function executableForInstall(installDir: string): string | null {
  if (process.platform === "darwin") {
    const exe = join(installDir, "Unity.app", "Contents", "MacOS", "Unity");
    return existsSync(exe) ? exe : null;
  }
  if (process.platform === "win32") {
    const exe = join(installDir, "Editor", "Unity.exe");
    return existsSync(exe) ? exe : null;
  }
  // Linux.
  const exe = join(installDir, "Editor", "Unity");
  return existsSync(exe) ? exe : null;
}

/**
 * Scan the OS-default Hub roots (+ `UNITY_HUB` env override) for version-named
 * install folders and return the validated executables, sorted newest-first.
 * Never throws — unreadable/missing dirs are silently skipped (best-effort
 * discovery). De-duplicates by executable path so an env override pointing at
 * the same place as an OS default does not double-list.
 *
 * `roots` is an optional override (test hook) — when omitted the real
 * `scannedHubRoots()` is used. Passing an explicit list keeps unit tests
 * deterministic across machines without monkey-patching env vars.
 */
export function discoverUnityInstalls(roots?: string[]): UnityInstall[] {
  const scanRoots = roots ?? scannedHubRoots();
  const seen = new Map<string, UnityInstall>();
  for (const root of scanRoots) {
    let entries: string[];
    try {
      entries = readdirSync(root);
    } catch {
      continue;
    }
    for (const name of entries) {
      const installDir = join(root, name);
      try {
        if (!statSync(installDir).isDirectory()) continue;
      } catch {
        continue;
      }
      const exe = executableForInstall(installDir);
      if (!exe) continue;
      if (!seen.has(exe)) {
        seen.set(exe, { path: exe, version: name });
      }
    }
  }
  const installs = Array.from(seen.values());
  installs.sort((a, b) => compareUnityVersions(b.version, a.version)); // newest first
  return installs;
}

/**
 * Compare two Unity version strings for sort ordering. Unity versions look
 * like `YYYY.N.PxX` or `6000.N.PxX` with an optional suffix (`a1`, `b2`,
 * `f3`, `p4`, `c5`). We split on non-digit runs, compare numeric parts
 * left-to-right, and fall back to lexicographic for the trailing suffix so
 * `6000.4.0f1` > `6000.4.0b1` (final > beta, matching the Hub's intent of
 * surfacing newer/more-stable releases first). Returns >0 / 0 / <0.
 */
export function compareUnityVersions(a: string, b: string): number {
  if (a === b) return 0;
  const partsA = a.split(/[^0-9]+/).filter(Boolean).map((n) => parseInt(n, 10));
  const partsB = b.split(/[^0-9]+/).filter(Boolean).map((n) => parseInt(n, 10));
  const len = Math.max(partsA.length, partsB.length);
  for (let i = 0; i < len; i++) {
    const av = partsA[i] ?? -1;
    const bv = partsB[i] ?? -1;
    if (av !== bv) return av - bv;
  }
  // Numeric parts equal — compare the trailing suffix lexically (f > b > a).
  return a.localeCompare(b);
}

/**
 * True when `candidateVersion` should be preferred for a project whose
 * running bridge reports `projectVersion`. Matches the full string first,
 * then the leading dotted prefix (`6000.4` matches `6000.4.0f1`), so a
 * patch-level difference still pins the right minor line. Null/empty
 * `projectVersion` matches nothing (caller falls back to newest).
 */
export function versionMatches(candidateVersion: string, projectVersion?: string | null): boolean {
  if (!projectVersion) return false;
  if (candidateVersion === projectVersion) return true;
  // Strip any trailing patch/suffix from the project version and prefix-match.
  // "6000.4.0f1" -> prefix "6000.4" (first two numeric groups).
  const groups = projectVersion.split(/[^0-9]+/).filter(Boolean);
  if (groups.length >= 2) {
    const prefix = groups.slice(0, 2).join(".");
    if (candidateVersion.startsWith(prefix + ".") || candidateVersion.startsWith(prefix)) {
      return true;
    }
  }
  return false;
}

/**
 * Resolve the Unity executable to use for batch operations. Precedence:
 *   1. `UNITY_PATH` env var — validated to exist; source "env". Not run
 *      through discovery so an explicit pin always wins even if the path
 *      points outside the Hub layout.
 *   2. discovered installs — pick by `preferredVersion` match when given,
 *      else the newest (first, since discoverUnityInstalls sorts desc).
 *   3. null — caller surfaces `unity_not_discovered`.
 *
 * `preferredVersion` is typically the running bridge's `unityVersion` read
 * from the instance lock, so on multi-version machines batch picks the same
 * editor the live project uses.
 *
 * `roots` is an optional override (test hook) forwarded to
 * `discoverUnityInstalls` so tests can point at a temp fixture instead of the
 * real machine state.
 */
export function resolveUnityPath(
  preferredVersion?: string | null,
  roots?: string[],
): ResolvedUnityPath | null {
  const envPath = process.env.UNITY_PATH;
  if (envPath) {
    try {
      if (existsSync(envPath) && statSync(envPath).isFile()) {
        return { path: envPath, version: "(env override)", source: "env" };
      }
    } catch {
      // fall through to discovery
    }
  }

  const installs = discoverUnityInstalls(roots);
  if (installs.length === 0) return null;

  if (preferredVersion) {
    const match = installs.find((i) => versionMatches(i.version, preferredVersion));
    if (match) return { path: match.path, version: match.version, source: "discovered" };
  }
  // Newest first already.
  const newest = installs[0];
  return { path: newest.path, version: newest.version, source: "discovered" };
}
