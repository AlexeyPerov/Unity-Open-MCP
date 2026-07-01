// Unity Hub control — installed-editor discovery, available-releases feed,
// install deep link, and Hub CLI install-path management.
//
// This is a **self-contained** module: it uses only Node builtins
// (`node:fs`, `node:child_process`, `node:https`, `node:os`, `node:path`) and
// reuses the existing standalone TS discovery in `unity-install-discovery.ts`.
// It has **no** runtime dependency on anything under `/hub/` (the Tauri
// launcher) — the discovery roots, the Unity download-archive feed, and the
// `unityhub://` deep-link transport are the same *approaches* the Hub launcher
// uses, re-implemented independently here in TS. Provenance comments reference
// the Hub's Rust sources for developer context only (the same convention
// `unity-install-discovery.ts` already follows); no code coupling.
//
// The architecture is two layers, mirroring the pattern established in
// `dialog-dismiss.ts`:
//   - **Pure functions** (`parseInstalledEditors`, `parseAvailableReleases`,
//     `parseInstallPath`, `scanPlaybackEngines`, `resolveHubCliPath`,
//     `listInstalledEditors`) — unit-tested with string/path fixtures.
//   - **Side-effecting layer** (`getInstallPath`, `setInstallPath`,
//     `openInstallDeepLink`, `fetchAvailableReleases`) — each takes an
//     injectable runner/opener/fetcher defaulting to the real impl, so tests
//     inject canned outputs without mocking `node:child_process`.

import { execFileSync } from "node:child_process";
import { existsSync, readdirSync, statSync } from "node:fs";
import { get } from "node:https";
import { homedir } from "node:os";
import { join } from "node:path";

import {
  discoverUnityInstalls,
  scannedHubRoots,
  type UnityInstall,
} from "./unity-install-discovery.js";

// ── Types ──────────────────────────────────────────────────────────

/** An installed Unity editor, enriched with build-target platforms. */
export interface InstalledEditor {
  /** Unity version string as named by the install folder (e.g. "6000.4.0f1"). */
  version: string;
  /** Absolute path to the Unity executable. */
  path: string;
  /** Friendly build-target platform names scanned from `Data/PlaybackEngines/`. */
  platforms: string[];
  /** Release stream inferred from the version suffix: "LTS" | "TECH" | "Beta" | "Alpha" | "". */
  releaseType: string;
}

/** One release entry from Unity's public download-archive feed. */
export interface AvailableRelease {
  version: string;
  /** "LTS" | "Supported" | "TECH" | "Beta" | "Alpha". */
  stream: string;
  /** ISO date (`YYYY-MM-DD`) or null. */
  releaseDate: string | null;
  /** `https://unity.com/releases/editor/whats-new/<version>`. */
  releaseNotesUrl: string;
  /** Changeset hash extracted from the `unityhub://` deep link, or null. */
  changeset: string | null;
}

/** Result of the available-releases fetch — entries + staleness flag. */
export interface AvailableReleasesResult {
  entries: AvailableRelease[];
  /** True when the data is a fallback (offline snapshot), not a live fetch. */
  stale: boolean;
  /** RFC 3339 timestamp of when the data was produced. */
  fetchedAt: string;
}

// ── Pure: playback-engine platform scan ────────────────────────────

/**
 * Mapping from a Unity `Data/PlaybackEngines/<folder>` name to the friendly
 * build-target label. Covers the Windows/macOS/Linux keys Unity ships on all
 * hosts. Unknown folders fall back to the lowercased name so a new platform
 * is never silently dropped. TS port of the Rust
 * `friendly_playback_engine_name`.
 */
export function friendlyPlaybackEngineName(folder: string): string {
  switch (folder.toLowerCase()) {
    case "androidplayer":
      return "Android";
    case "windowsstandalonesupport":
      return "Win64";
    case "linuxstandalonesupport":
    case "linuxstandalone":
      return "Linux64";
    case "osxstandalonesupport":
    case "osxstandalone":
      return "OSX";
    case "webglsupport":
    case "webgl":
      return "WebGL";
    case "metrosupport":
      return "UWP";
    case "iossupport":
    case "iphone-player":
      return "iOS";
    case "appletvsupport":
      return "tvOS";
    case "visionosplayer":
      return "visionOS";
    case "switchplayer":
    case "switchsupport":
      return "Switch";
    case "ps4player":
    case "ps5player":
      return folder.toUpperCase();
    default:
      return folder;
  }
}

/**
 * Resolve the editor install's `Data/` folder. On macOS the editor lives
 * inside `Unity.app/Contents/`; on Windows/Linux it sits in an `Editor/`
 * subfolder (Hub layout) or as a sibling (source build). Returns null when no
 * `Data/` directory exists. TS port of the Rust `editor_data_folder`.
 */
export function editorDataFolder(installDir: string): string | null {
  if (process.platform === "darwin") {
    const candidate = join(installDir, "Unity.app", "Contents", "Data");
    return existsSync(candidate) ? candidate : null;
  }
  const editorData = join(installDir, "Editor", "Data");
  if (existsSync(editorData)) return editorData;
  const siblingData = join(installDir, "Data");
  return existsSync(siblingData) ? siblingData : null;
}

/**
 * Scan an editor install's `Data/PlaybackEngines/` directory and return the
 * friendly platform names of every build target Unity shipped modules for.
 * Returns an empty array when the install is missing the directory (a minimal
 * / custom build). TS port of the Rust `scan_playback_engines`.
 */
export function scanPlaybackEngines(installDir: string): string[] {
  const dataFolder = editorDataFolder(installDir);
  if (!dataFolder) return [];
  const playbackEngines = join(dataFolder, "PlaybackEngines");
  let entries: string[];
  try {
    entries = readdirSync(playbackEngines);
  } catch {
    return [];
  }
  const platforms: string[] = [];
  for (const name of entries) {
    const dirPath = join(playbackEngines, name);
    try {
      if (!statSync(dirPath).isDirectory()) continue;
    } catch {
      continue;
    }
    const friendly = friendlyPlaybackEngineName(name);
    if (!platforms.includes(friendly)) platforms.push(friendly);
  }
  platforms.sort();
  return platforms;
}

// ── Pure: release-stream inference ─────────────────────────────────

/**
 * Returns the Unity version's kind marker character (`a` / `b` / `f` / `p` /
 * `c`) by scanning from the end and returning the last alphabetic character
 * before the trailing digits. Returns `\0` for strings without a kind marker.
 * TS port of the Rust `version_kind_marker`.
 */
function versionKindMarker(version: string): string {
  const chars = [...version];
  let i = chars.length;
  while (i > 0 && isAsciiDigit(chars[i - 1])) i -= 1;
  if (i > 0) {
    const c = chars[i - 1];
    if (isAsciiLetter(c)) return c.toLowerCase();
  }
  return "\0";
}

function isAsciiDigit(c: string): boolean {
  return c >= "0" && c <= "9";
}
function isAsciiLetter(c: string): boolean {
  return (c >= "a" && c <= "z") || (c >= "A" && c <= "Z");
}

/**
 * Release stream inferred from the Unity version suffix. TS port of the Rust
 * `release_type_for`.
 */
export function releaseTypeFor(version: string): string {
  const kind = versionKindMarker(version);
  switch (kind) {
    case "a":
      return "Alpha";
    case "b":
      return "Beta";
    case "f": {
      const lower = version.toLowerCase();
      // Known Unity LTS lines (min supported by the packages is 2022.3 LTS).
      const isKnownLts =
        lower.startsWith("6000.0") ||
        lower.startsWith("2022.3") ||
        lower.startsWith("2021.3") ||
        lower.startsWith("2020.3") ||
        lower.startsWith("2019.4");
      return isKnownLts ? "LTS" : "TECH";
    }
    default:
      return "";
  }
}

// ── Pure: installed-editor discovery orchestration ─────────────────

/**
 * List installed Unity editors by scanning the Hub install roots (+ `UNITY_HUB`
 * env override), enriching each with build-target platforms and a release
 * stream. `roots` is an optional test hook (forwarded to
 * `discoverUnityInstalls`); when omitted the real machine roots are scanned.
 *
 * The primary discovery path is filesystem scanning (matches what the Hub
 * launcher and `unity-install-discovery.ts` do). Never throws.
 */
export function listInstalledEditors(roots?: string[]): InstalledEditor[] {
  const installs: UnityInstall[] = discoverUnityInstalls(roots);
  return installs.map((install) => {
    // The version is the install folder name; the install dir is its parent.
    // `discoverUnityInstalls` returns the executable path — derive the install
    // folder so `scanPlaybackEngines` reads the right `Data/` dir.
    const installDir = installDirFromExe(install.path);
    return {
      version: install.version,
      path: install.path,
      platforms: installDir ? scanPlaybackEngines(installDir) : [],
      releaseType: releaseTypeFor(install.version),
    };
  });
}

/**
 * Derive the install root folder (the version-named dir under the Hub Editor
 * root) from the Unity executable path returned by `discoverUnityInstalls`.
 * Returns null when the path doesn't match the expected Hub layout.
 */
function installDirFromExe(exePath: string): string | null {
  // macOS: <install>/Unity.app/Contents/MacOS/Unity  -> install is 3 up.
  // Windows/Linux: <install>/Editor/Unity[.exe]       -> install is 1 up.
  if (process.platform === "darwin") {
    // Walk up three levels from the executable.
    const parts = exePath.split("/");
    // Drop the trailing filename + MacOS + Contents + Unity.app.
    if (parts.length >= 4) return parts.slice(0, -3).join("/") || null;
    return null;
  }
  // Windows/Linux: <install>/Editor/<binary>.
  const parts = exePath.split(/[\\/]/);
  if (parts.length >= 2) return parts.slice(0, -2).join("/") || null;
  return null;
}

// ── Pure: Hub CLI output parsing ───────────────────────────────────

/** A version + path pair parsed from `Unity Hub --headless editors --installed`. */
export interface ParsedEditor {
  version: string;
  path: string;
}

/**
 * Parse the stdout of `Unity Hub --headless editors --installed`. Each line
 * looks like `2022.3.0f1 , installed at C:\Program Files\Unity\...`. Lines that
 * do not match are ignored. Pure (string in, list out) so it can be unit-tested
 * against captured output.
 */
export function parseInstalledEditors(stdout: string): ParsedEditor[] {
  const editors: ParsedEditor[] = [];
  const lines = stdout.split(/\r?\n/);
  for (const line of lines) {
    if (!line.trim()) continue;
    // `version , installed at PATH` — the comma is optional in some Hub builds.
    const match = line.match(/^([\d.]+\w*)\s*,?\s*installed at\s+(.+)$/i);
    if (match) {
      editors.push({ version: match[1].trim(), path: match[2].trim() });
    }
  }
  return editors;
}

/**
 * Parse the stdout of `Unity Hub --headless install-path`. The Hub normally
 * prints the bare path on its own line; some builds prefix it with a label
 * like `Default install path: /path`. Returns the trimmed path, or null when
 * the output is empty/unparseable. Pure.
 *
 * Strip-rule: a `Label: <path>` prefix is removed only when the text before
 * the colon is a plain word/phrase (alphanumerics + spaces, no path
 * separators) — so a Windows drive path `C:\Program Files\...` (colon at
 * index 1, left side `C`) is left intact.
 */
export function parseInstallPath(stdout: string): string | null {
  const trimmed = stdout.trim();
  if (!trimmed) return null;
  const lines = trimmed.split(/\r?\n/).filter((l) => l.trim());
  const last = lines[lines.length - 1];
  let candidate = last.trim();
  const colonIdx = candidate.indexOf(":");
  if (colonIdx !== -1) {
    const left = candidate.slice(0, colonIdx);
    // A label is alphanumerics + spaces only; a Windows drive letter is a
    // single char, and a real path always contains a separator somewhere in
    // the left side too. Only strip when the left side is a clean label.
    if (left.length > 1 && /^[A-Za-z0-9 ]+$/.test(left)) {
      candidate = candidate.slice(colonIdx + 1).trim();
    }
  }
  return candidate || null;
}

// ── Pure: Unity Hub CLI binary resolution ──────────────────────────

let hubCliPathCache: string | null | undefined;

/**
 * Per-platform default Unity Hub binary locations. The Hub registers itself as
 * the system handler for `unityhub://`, but the headless CLI needs the binary
 * directly. Honors the `UNITY_HUB_PATH` env var (highest precedence). Returns
 * null when the candidate does not exist on disk.
 *
 * macOS: `/Applications/Unity Hub.app/Contents/MacOS/Unity Hub`
 * Windows: `%ProgramFiles%\Unity Hub\Unity Hub.exe`
 * Linux: `$HOME/Unity Hub/Unity Hub` (Hub ships as an AppImage; the extracted
 *   binary path is the common install location).
 */
export function resolveHubCliPath(): string | null {
  if (hubCliPathCache !== undefined) return hubCliPathCache;
  const envPath = process.env.UNITY_HUB_PATH;
  const candidates: string[] = [];
  if (envPath) candidates.push(envPath);
  if (process.platform === "darwin") {
    candidates.push("/Applications/Unity Hub.app/Contents/MacOS/Unity Hub");
  } else if (process.platform === "win32") {
    const programFiles = process.env.ProgramFiles || join("C:", "Program Files");
    candidates.push(join(programFiles, "Unity Hub", "Unity Hub.exe"));
  } else {
    candidates.push(join(homedir(), "Unity Hub", "Unity Hub"));
  }
  const found = candidates.find((p) => {
    try {
      return existsSync(p);
    } catch {
      return false;
    }
  });
  hubCliPathCache = found ?? null;
  return hubCliPathCache;
}

/** Test-only: reset the cached Hub CLI path. */
export function _resetHubCliPathCacheForTests(): void {
  hubCliPathCache = undefined;
}

// ── Pure: available-releases feed parsing ──────────────────────────

/**
 * Map a raw Unity archive `stream` string to our label. Unknown values fall
 * back to "TECH" (a conservative stable-ish default) so a future Unity string
 * is never dropped. Mirrors the Rust `ReleaseStream::from_unity_str`.
 */
export function streamFromUnityStr(raw: string): string {
  switch (raw) {
    case "LTS":
      return "LTS";
    case "SUPPORTED":
      return "Supported";
    case "TECH":
      return "TECH";
    case "BETA":
      return "Beta";
    case "ALPHA":
      return "Alpha";
    default:
      return "TECH";
  }
}

/**
 * Extract the changeset hash from a `unityhub://<version>/<changeset>` deep
 * link. Returns null when the link is missing or malformed. TS port of the
 * Rust `extract_changeset`.
 */
export function extractChangeset(deepLink?: string | null): string | null {
  if (!deepLink) return null;
  const afterScheme = deepLink.startsWith("unityhub://")
    ? deepLink.slice("unityhub://".length)
    : null;
  if (afterScheme === null) return null;
  // The path is `<version>/<changeset>`. Only return a changeset when there is
  // a slash separator with a non-empty trailing segment — a bare
  // `unityhub://<version>` carries no changeset (older Hub builds emit these).
  const slashIdx = afterScheme.lastIndexOf("/");
  if (slashIdx === -1) return null;
  const cs = afterScheme.slice(slashIdx + 1);
  return cs.length > 0 ? cs : null;
}

/**
 * Normalize the archive feed's full ISO timestamp (`2026-06-17T15:09:23.805Z`)
 * down to the date portion (`2026-06-17`). Passes through values that don't
 * look like an ISO date unchanged. TS port of the Rust `normalize_release_date`.
 */
export function normalizeReleaseDate(raw?: string | null): string | null {
  if (!raw) return null;
  if (raw.length >= 10 && raw[4] === "-" && raw[7] === "-") {
    return raw.slice(0, 10);
  }
  return raw;
}

function releaseNotesUrl(version: string): string {
  return `https://unity.com/releases/editor/whats-new/${version}`;
}

/**
 * Wire shape of a single `node` inside the `getUnityReleases` GraphQL response
 * embedded in the archive page. Mirrors the fields Unity publishes.
 */
interface UnityReleaseNode {
  version: string;
  releaseDate?: string;
  unityHubDeepLink?: string;
  stream: string;
}

/** Find the index of the closing `"` of a JSON string literal body. Honors `\"`. */
function findSegmentClose(s: string): number | null {
  let i = 0;
  while (i < s.length) {
    if (s[i] === "\\") {
      i += 2;
      continue;
    }
    if (s[i] === '"') return i;
    i += 1;
  }
  return null;
}

/**
 * Parse the Next.js RSC payload embedded in the Unity download-archive page
 * HTML and return the list of releases. The page emits the GraphQL result as
 * one `self.__next_f.push([1,"31:<json>"])` script segment; we locate that
 * segment, JSON-decode the string literal (un-escaping the inner JSON), strip
 * the `31:` RSC prefix, and deserialize the result. Returns null when no
 * releases segment is found. TS port of the Rust `parse_archive_payload`.
 *
 * Pure (string in, list out) so it can be unit-tested against a captured
 * fixture without touching the network.
 */
export function parseArchivePayload(html: string): AvailableRelease[] | null {
  const marker = 'self.__next_f.push([1,"';
  let i = 0;
  while (true) {
    const rel = html.slice(i).indexOf(marker);
    if (rel === -1) break;
    const abs = i + rel;
    const start = abs + marker.length;
    const end = findSegmentClose(html.slice(start));
    if (end === null) break;
    const rawLiteral = html.slice(start, start + end);
    i = start + end;
    // Decode the JSON string literal.
    let decoded: string;
    try {
      decoded = JSON.parse(`"${rawLiteral}"`);
    } catch {
      continue;
    }
    const jsonStr = decoded.startsWith("31:") ? decoded.slice(3) : null;
    if (!jsonStr || !jsonStr.includes("getUnityReleases")) continue;
    let response: { getUnityReleases?: { edges?: { node: UnityReleaseNode }[] } };
    try {
      response = JSON.parse(jsonStr);
    } catch {
      continue;
    }
    const edges = response.getUnityReleases?.edges;
    if (!Array.isArray(edges)) continue;
    const entries: AvailableRelease[] = edges.map((e) => {
      const node = e.node;
      return {
        version: node.version,
        stream: streamFromUnityStr(node.stream),
        releaseDate: normalizeReleaseDate(node.releaseDate),
        releaseNotesUrl: releaseNotesUrl(node.version),
        changeset: extractChangeset(node.unityHubDeepLink),
      };
    });
    // Sort newest-first by date; entries without a date sort last.
    entries.sort((a, b) => {
      const ad = a.releaseDate ?? "";
      const bd = b.releaseDate ?? "";
      return bd < ad ? -1 : bd > ad ? 1 : 0;
    });
    return entries;
  }
  return null;
}

// ── Bundled offline snapshot (fallback when the network is unreachable) ──

/**
 * Bundled snapshot of recent Unity release streams. NOT the primary source —
 * the offline / network-failure fallback served when the live archive fetch
 * fails. Kept small and corrected to the right streams so we never mislabel a
 * version. Mirrors the Rust `snapshot_entries` (newest-first).
 */
export function snapshotReleases(): AvailableRelease[] {
  return [
    { version: "6000.4.12f1", stream: "Supported", releaseDate: "2026-06-17", releaseNotesUrl: releaseNotesUrl("6000.4.12f1"), changeset: "3ca267ce8005" },
    { version: "6000.3.18f1", stream: "LTS", releaseDate: "2026-06-17", releaseNotesUrl: releaseNotesUrl("6000.3.18f1"), changeset: "5ebeb53e4c07" },
    { version: "6000.5.0f1", stream: "Supported", releaseDate: "2026-06-15", releaseNotesUrl: releaseNotesUrl("6000.5.0f1"), changeset: "88b47c5e7076" },
    { version: "6000.0.32f1", stream: "TECH", releaseDate: "2026-05-14", releaseNotesUrl: releaseNotesUrl("6000.0.32f1"), changeset: null },
    { version: "6000.3.10f1", stream: "LTS", releaseDate: "2026-02-25", releaseNotesUrl: releaseNotesUrl("6000.3.10f1"), changeset: null },
    { version: "6000.3.0f1", stream: "LTS", releaseDate: "2025-12-04", releaseNotesUrl: releaseNotesUrl("6000.3.0f1"), changeset: null },
    { version: "2022.3.62f2", stream: "LTS", releaseDate: "2025-10-03", releaseNotesUrl: releaseNotesUrl("2022.3.62f2"), changeset: "7670c08855a9" },
  ];
}

// ── Side-effecting layer (injectable runners) ──────────────────────

/**
 * Signature of the Hub CLI invocation. Runs the Unity Hub binary in headless
 * mode and returns `{ stdout, stderr, exitCode }`. The default implementation
 * uses `execFileSync`; tests inject a fake.
 */
export type HubCliRunner = (
  args: string[],
  opts?: { timeoutMs?: number },
) => { stdout: string; stderr: string; exitCode: number };

/** Default Hub CLI runner — `execFileSync` against the resolved Hub binary. */
function defaultHubCliRunner(
  args: string[],
  opts?: { timeoutMs?: number },
): { stdout: string; stderr: string; exitCode: number } {
  const hubPath = resolveHubCliPath();
  if (!hubPath) {
    return {
      stdout: "",
      stderr: "",
      exitCode: -1, // signals "not found"; caller maps to `hub_cli_not_found`.
    };
  }
  // Try the modern (3.x) `--headless` form, then the legacy (2.x) `-- --headless`.
  const strategies = [
    { name: "modern", args: ["--headless", ...args] },
    { name: "legacy", args: ["--", "--headless", ...args] },
  ];
  for (const strategy of strategies) {
    try {
      const stdout = execFileSync(hubPath, strategy.args, {
        timeout: opts?.timeoutMs ?? 30_000,
        maxBuffer: 10 * 1024 * 1024,
        encoding: "utf8",
        windowsHide: true,
      });
      return { stdout, stderr: "", exitCode: 0 };
    } catch (err: unknown) {
      const e = err as { stdout?: string; stderr?: string; status?: number; message?: string };
      // ENOENT → Hub gone; surface as not-found.
      if (e.message && /ENOENT/.test(e.message)) {
        return { stdout: "", stderr: "", exitCode: -1 };
      }
      // Non-zero exit but with stdout data — some Hub builds return data despite
      // a non-zero status. Use it if present.
      const out = (e.stdout ?? "").toString().trim();
      if (out) {
        return { stdout: out, stderr: (e.stderr ?? "").toString(), exitCode: e.status ?? 0 };
      }
      // Otherwise try the next strategy.
    }
  }
  return { stdout: "", stderr: "all strategies failed", exitCode: 1 };
}

/** Result of `getInstallPath` — the resolved path or a structured error. */
export interface InstallPathResult {
  path: string | null;
  /** Structured error when the Hub CLI is missing or the call failed. */
  error: { code: string; message: string } | null;
  /** Whether the Hub CLI was used (vs filesystem inference). */
  source: "hub-cli" | "filesystem" | "none";
}

/**
 * Get the default Unity editor install directory. Tries the Hub CLI
 * (`install-path`) first; falls back to inferring from the Hub install roots
 * (`scannedHubRoots`) when the CLI is unavailable. The `roots` override is a
 * test hook forwarded to the filesystem fallback.
 */
export function getInstallPath(opts?: {
  runHubCli?: HubCliRunner;
  roots?: string[];
}): InstallPathResult {
  const runner = opts?.runHubCli ?? defaultHubCliRunner;
  const result = runner(["install-path"]);
  if (result.exitCode === -1) {
    // Hub CLI not found — fall back to filesystem inference.
    const roots = opts?.roots ?? scannedHubRoots();
    if (roots.length > 0) {
      return { path: roots[0], error: null, source: "filesystem" };
    }
    return {
      path: null,
      error: {
        code: "hub_cli_not_found",
        message:
          "Unity Hub CLI not found. Set UNITY_HUB_PATH to the Unity Hub binary " +
          "(e.g. '/Applications/Unity Hub.app/Contents/MacOS/Unity Hub' on macOS, " +
          "'C:\\Program Files\\Unity Hub\\Unity Hub.exe' on Windows).",
      },
      source: "none",
    };
  }
  const parsed = parseInstallPath(result.stdout);
  if (!parsed) {
    return {
      path: null,
      error: {
        code: "install_path_unparseable",
        message: `Could not parse install path from Hub CLI output: ${result.stdout || result.stderr}`,
      },
      source: "hub-cli",
    };
  }
  return { path: parsed, error: null, source: "hub-cli" };
}

/**
 * Set the default Unity editor install directory via the Hub CLI
 * (`install-path --set <path>`). This is the one Hub operation the
 * `unityhub://` deep-link transport cannot perform, so it genuinely requires
 * the headless CLI. Returns a structured `hub_cli_not_found` error when the Hub
 * binary is absent.
 */
export function setInstallPath(
  path: string,
  opts?: { runHubCli?: HubCliRunner },
): { success: boolean; error: { code: string; message: string } | null; output: string } {
  const runner = opts?.runHubCli ?? defaultHubCliRunner;
  const result = runner(["install-path", "--set", path]);
  if (result.exitCode === -1) {
    return {
      success: false,
      error: {
        code: "hub_cli_not_found",
        message:
          "Unity Hub CLI not found. Set UNITY_HUB_PATH to the Unity Hub binary " +
          "(e.g. '/Applications/Unity Hub.app/Contents/MacOS/Unity Hub' on macOS, " +
          "'C:\\Program Files\\Unity Hub\\Unity Hub.exe' on Windows).",
      },
      output: "",
    };
  }
  if (result.exitCode !== 0) {
    return {
      success: false,
      error: {
        code: "set_install_path_failed",
        message: result.stderr || `Hub CLI exited with code ${result.exitCode}`,
      },
      output: result.stdout,
    };
  }
  return { success: true, error: null, output: result.stdout };
}

/**
 * Signature of the OS URL-opener. The default implementation dispatches per
 * platform (`open` / `xdg-open` / `start`); tests inject a fake.
 */
export type UrlOpener = (url: string) => { opened: boolean; error: string | null };

/** Default URL opener — dispatches to the platform handler. */
function defaultUrlOpener(url: string): { opened: boolean; error: string | null } {
  let binary: string;
  let args: string[];
  if (process.platform === "win32") {
    binary = "cmd";
    args = ["/c", "start", "", url];
  } else if (process.platform === "darwin") {
    binary = "open";
    args = [url];
  } else {
    binary = "xdg-open";
    args = [url];
  }
  try {
    execFileSync(binary, args, { encoding: "utf8", windowsHide: true });
    return { opened: true, error: null };
  } catch (err: unknown) {
    const e = err as { message?: string };
    return { opened: false, error: e.message ?? String(err) };
  }
}

/**
 * Build the `unityhub://` deep link for a release. When a changeset is
 * available the link is `unityhub://<version>/<changeset>`, which the Hub
 * resolves to the exact build; without one it is `unityhub://<version>`. Older
 * Hub versions may ignore a changeset-less link, so the caller should fall back
 * to the release-notes URL in that case.
 */
export function buildInstallDeepLink(version: string, changeset?: string | null): string {
  const v = version.trim();
  const cs = changeset && changeset.trim().length > 0 ? changeset.trim() : null;
  return cs ? `unityhub://${v}/${cs}` : `unityhub://${v}`;
}

/**
 * Result of `openInstallDeepLink` — whether the OS handler accepted the link.
 */
export interface OpenDeepLinkResult {
  deepLink: string;
  opened: boolean;
  error: { code: string; message: string } | null;
}

/**
 * Open Unity Hub at its install dialog for `<version>` by firing the
 * `unityhub://` deep link via the OS URL handler. The Hub must be installed
 * and registered as the system handler for the scheme. Single-instance, real
 * progress bar — the install happens inside the Hub, outside this process.
 * There is no in-app completion detection; the caller should re-list editors
 * after the Hub finishes.
 */
export function openInstallDeepLink(
  version: string,
  changeset?: string | null,
  opts?: { openUrl?: UrlOpener },
): OpenDeepLinkResult {
  if (!version.trim()) {
    return {
      deepLink: "",
      opened: false,
      error: { code: "missing_parameter", message: "version is required." },
    };
  }
  const deepLink = buildInstallDeepLink(version, changeset);
  const opener = opts?.openUrl ?? defaultUrlOpener;
  const res = opener(deepLink);
  if (res.opened) {
    return { deepLink, opened: true, error: null };
  }
  return {
    deepLink,
    opened: false,
    error: {
      code: "deep_link_open_failed",
      message:
        `Could not open the Unity Hub install dialog via ${deepLink}. ` +
        `Ensure Unity Hub is installed and registered as the unityhub:// handler. ` +
        (res.error ? `OS error: ${res.error}` : ""),
    },
  };
}

/**
 * Signature of the archive-page fetcher. The default implementation uses
 * `node:https`; tests inject a fake that returns a captured HTML fixture.
 */
export type ArchiveFetcher = (url: string) => Promise<string>;

/** Default archive fetcher — `node:https` GET against Unity's archive page. */
function defaultArchiveFetcher(url: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const req = get(
      url,
      { timeout: 20_000, headers: { "User-Agent": "unity-open-mcp" } },
      (res) => {
        if (res.statusCode && (res.statusCode < 200 || res.statusCode >= 300)) {
          reject(new Error(`archive fetch failed: HTTP ${res.statusCode}`));
          res.resume();
          return;
        }
        const chunks: Buffer[] = [];
        res.on("data", (c: Buffer) => chunks.push(c));
        res.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
      },
    );
    req.on("error", reject);
    req.on("timeout", () => {
      req.destroy(new Error("archive fetch timed out"));
    });
  });
}

/** Public archive URL — the same source the Hub launcher fetches. */
export const ARCHIVE_URL = "https://unity.com/releases/editor/archive";

/**
 * Fetch Unity's public download-archive page and parse the release catalog.
 * Falls back to the bundled snapshot (with `stale: true`) on any network or
 * parse failure, so the call never hard-errors — a network outage surfaces as
 * stale data, not an empty result.
 */
export async function fetchAvailableReleases(opts?: {
  fetcher?: ArchiveFetcher;
}): Promise<AvailableReleasesResult> {
  const fetcher = opts?.fetcher ?? defaultArchiveFetcher;
  try {
    const html = await fetcher(ARCHIVE_URL);
    const entries = parseArchivePayload(html);
    if (entries && entries.length > 0) {
      return { entries, stale: false, fetchedAt: new Date().toISOString() };
    }
  } catch {
    // fall through to snapshot.
  }
  return {
    entries: snapshotReleases(),
    stale: true,
    fetchedAt: new Date().toISOString(),
  };
}
