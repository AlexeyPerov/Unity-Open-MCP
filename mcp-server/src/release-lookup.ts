// Published-release lookups shared by the `update` CLI command and the
// `unity_open_mcp_upgrade` route.
//
// Both callers need the same two facts — "what is the latest trio version?"
// and "does the bridge release tag for version X exist?" — and both must get
// them WITHOUT going through the Unity Editor: the bridge's upgrade tool runs
// on the Editor main thread, where a 10 s registry timeout would freeze the
// Editor and the whole bridge request queue. The lookups therefore live here,
// in the MCP server process, and the bridge only ever receives a resolved
// `target_version`.
//
// Pure `fetch` + hand-rolled semver; no runtime dependencies.

import { isRecord } from "./cli/json-guards.js";

export const PACKAGE_NAME = "unity-open-mcp";
const NPM_LATEST_URL = `https://registry.npmjs.org/${PACKAGE_NAME}/latest`;
const GITHUB_RELEASES_URL =
  "https://api.github.com/repos/AlexeyPerov/Unity-Open-MCP/releases?per_page=30";
// matching-refs is a PREFIX query: asking for bridge-v1.2.3 also lists
// bridge-v1.2.30, so only the exact ref name proves the tag exists. Mirrors
// LatestVersionCheck.ContainsExactBridgeTag on the bridge.
const GITHUB_BRIDGE_TAG_URL =
  "https://api.github.com/repos/AlexeyPerov/Unity-Open-MCP/git/matching-refs/tags/bridge-v";
const REQUEST_TIMEOUT_MS = 10_000;

export type ReleaseSource = "npm" | "github";

export interface LatestVersion {
  version: string;
  source: ReleaseSource;
  fallbackReason?: string;
}

export interface BridgeTagConfirmation {
  confirmed: boolean;
  error?: string;
}

/**
 * Latest stable trio version: npm `latest` dist-tag first, GitHub `v*`
 * releases as the fallback. Throws when neither source answers.
 */
export async function resolveLatestVersion(
  fetchImpl: typeof globalThis.fetch,
): Promise<LatestVersion> {
  let npmFailure = "npm registry lookup failed";
  try {
    const body = await fetchJson(fetchImpl, NPM_LATEST_URL);
    const version = isRecord(body) ? body.version : undefined;
    if (typeof version === "string" && parseSemver(version)) {
      return { version, source: "npm" };
    }
    npmFailure = "npm registry returned no valid version";
  } catch (error) {
    npmFailure = error instanceof Error ? error.message : String(error);
  }

  try {
    const body = await fetchJson(fetchImpl, GITHUB_RELEASES_URL);
    if (!Array.isArray(body)) throw new Error("GitHub returned an invalid release list");
    const versions = body
      .filter(isRecord)
      .filter((release) => release.draft !== true && release.prerelease !== true)
      .map((release) => release.tag_name)
      .filter(
        (tag): tag is string =>
          typeof tag === "string" && /^v\d+\.\d+\.\d+$/.test(tag),
      )
      .map((tag) => tag.slice(1))
      .filter((version) => parseSemver(version) !== null)
      .sort((a, b) => compareSemver(b, a) ?? 0);
    if (versions.length === 0) throw new Error("GitHub has no stable trio release tag");
    return { version: versions[0], source: "github", fallbackReason: npmFailure };
  } catch (error) {
    const githubFailure = error instanceof Error ? error.message : String(error);
    throw new Error(`npm: ${npmFailure}; GitHub: ${githubFailure}`);
  }
}

/**
 * Whether the `bridge-v<version>` release tag exists on GitHub. Never throws:
 * a failed lookup is reported as `confirmed: false` with the reason, because
 * the caller's decision ("do not schedule a UPM re-pin to a tag we cannot
 * prove exists") is the same either way.
 */
export async function confirmBridgeReleaseTag(
  version: string,
  fetchImpl: typeof globalThis.fetch,
): Promise<BridgeTagConfirmation> {
  if (!/^\d+\.\d+\.\d+$/.test(version)) {
    return { confirmed: false, error: "Target version must be a plain X.Y.Z." };
  }
  try {
    const body = await fetchJson(fetchImpl, `${GITHUB_BRIDGE_TAG_URL}${version}`);
    if (!Array.isArray(body)) {
      return { confirmed: false, error: "GitHub returned an invalid ref list." };
    }
    const exact = `refs/tags/bridge-v${version}`;
    return body.some((ref) => isRecord(ref) && ref.ref === exact)
      ? { confirmed: true }
      : { confirmed: false, error: `Release tag bridge-v${version} was not found.` };
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return { confirmed: false, error: `Bridge tag check failed: ${message}` };
  }
}

async function fetchJson(
  fetchImpl: typeof globalThis.fetch,
  url: string,
): Promise<unknown> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
  try {
    const response = await fetchImpl(url, {
      headers: { Accept: "application/json", "User-Agent": "unity-open-mcp-update" },
      signal: controller.signal,
    });
    if (!response.ok) {
      throw new Error(
        `${new URL(url).hostname} returned HTTP ${response.status}`,
      );
    }
    return await response.json();
  } finally {
    clearTimeout(timer);
  }
}

interface Semver {
  major: number;
  minor: number;
  patch: number;
  prerelease: string[];
}

export function parseSemver(value: string): Semver | null {
  const match = value.match(
    /^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/,
  );
  if (!match) return null;
  return {
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3]),
    prerelease: match[4]?.split(".") ?? [],
  };
}

export function compareSemver(a: string, b: string): number | null {
  const left = parseSemver(a);
  const right = parseSemver(b);
  if (!left || !right) return null;
  for (const key of ["major", "minor", "patch"] as const) {
    if (left[key] !== right[key]) return left[key] > right[key] ? 1 : -1;
  }
  if (left.prerelease.length === 0 || right.prerelease.length === 0) {
    if (left.prerelease.length === right.prerelease.length) return 0;
    return left.prerelease.length === 0 ? 1 : -1;
  }
  const length = Math.max(left.prerelease.length, right.prerelease.length);
  for (let index = 0; index < length; index++) {
    const l = left.prerelease[index];
    const r = right.prerelease[index];
    if (l === undefined) return -1;
    if (r === undefined) return 1;
    if (l === r) continue;
    const lNumeric = /^\d+$/.test(l);
    const rNumeric = /^\d+$/.test(r);
    if (lNumeric && rNumeric) return Number(l) > Number(r) ? 1 : -1;
    if (lNumeric !== rNumeric) return lNumeric ? -1 : 1;
    return l > r ? 1 : -1;
  }
  return 0;
}
