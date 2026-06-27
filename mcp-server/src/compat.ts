// Runtime version-compatibility check between the npm MCP server and the live
// Unity bridge.
//
// The npm MCP server, the bridge Unity package, and the verify Unity package
// ship as a tightly-coupled trio on ONE shared version (see <repo>/version.json
// and docs/versioning.md). The bridge reports its version on /ping as
// `bridgeVersion` (BridgeSession.BridgeVersion, which the sync script keeps
// equal to the bridge package.json version). The server reads its own version
// from its package.json at runtime (package-version.ts). This module compares
// the two and produces an advisory result — we WARN and continue rather than
// hard-failing, so a mixed pair never silently misbehaves but also never blocks
// a user who knows what they are doing. The check is advisory by design.
//
// The matching rule follows the pre-1.0 semver convention used across the repo:
// while the major version is 0, the MINOR digit is the breaking axis. So for
// `0.x`, both sides must agree on the full `0.minor` pair to be considered
// compatible; patch differences (0.5.0 vs 0.5.1) are compatible and only warn.
// Once the project reaches 1.0, the major digit becomes the breaking axis (the
// standard reading).
//
// Escape hatch: setting UNITY_OPEN_MCP_SKIP_VERSION_CHECK=1 in the environment
// fully suppresses the warning. See docs/versioning.md.

import { readPackageVersion } from "./package-version.js";

/**
 * The trio version this server belongs to. Resolved once at module load from
 * package.json (same value the bridge package.json should carry). We do not
 * re-read per call — package.json does not change while the process runs.
 */
export const SHARED_VERSION: string = readPackageVersion();

/** When non-empty, the version check is suppressed entirely. */
export function isVersionCheckSuppressed(): boolean {
  return process.env.UNITY_OPEN_MCP_SKIP_VERSION_CHECK === "1";
}

export interface CompatResult {
  /** true when the pair is considered compatible (patch-only or equal). */
  ok: boolean;
  serverVersion: string;
  bridgeVersion: string;
  /** Human-readable guidance; present when not ok or when the pair differs at all. */
  message: string;
}

/**
 * Parse `X.Y.Z` (ignoring any prerelease/build metadata) into a tuple, or
 * `undefined` when the string is not a recognizable version. Used by the
 * comparison below; not a full semver implementation on purpose.
 */
function parseLoose(v: string): [number, number, number] | undefined {
  const m = /^(\d+)\.(\d+)\.(\d+)/.exec(v);
  if (!m) return undefined;
  return [Number(m[1]), Number(m[2]), Number(m[3])];
}

/**
 * Compare the running server's version against the bridge's reported version.
 *
 * Rule (pre-1.0): for `0.x`, the whole `0.minor` pair must match. Patch
 * differences are compatible (warn only). After 1.0, the major digit must
 * match. Unparseable bridge versions are treated as compatible with a warning
 * (an ancient bridge that predates bridgeVersion reporting shouldn't be hard
 * blocked) — this keeps the check forward-compatible with older installs.
 *
 * Always returns a structured result; the caller decides whether to print it.
 * The result is advisory regardless — the connection proceeds either way.
 */
export function checkBridgeCompat(
  bridgeVersion: string,
  serverVersion: string = SHARED_VERSION,
): CompatResult {
  const server = parseLoose(serverVersion);
  const bridge = parseLoose(bridgeVersion);

  if (!bridge || !server) {
    return {
      ok: true,
      serverVersion,
      bridgeVersion,
      message:
        `unity-open-mcp: could not compare versions ` +
        `(server ${serverVersion}, bridge ${bridgeVersion}). ` +
        `Continuing; if tools misbehave, align both to the same release.`,
    };
  }

  const equal = serverVersion === bridgeVersion;
  if (equal) {
    return {
      ok: true,
      serverVersion,
      bridgeVersion,
      message: "",
    };
  }

  // Determine the breaking axis. Pre-1.0 (major === 0 on either side) uses the
  // minor digit as the breaking axis; otherwise the major digit. We take the
  // max of both majors so a 0.x server talking to a 1.0 bridge is treated as
  // a major mismatch (which it is).
  const breakingAxis: "major" | "minor" =
    server[0] === 0 || bridge[0] === 0 ? "minor" : "major";

  const majorMismatch = server[0] !== bridge[0];
  const minorMismatch = server[1] !== bridge[1];
  const incompatible =
    breakingAxis === "major" ? majorMismatch : majorMismatch || minorMismatch;

  if (!incompatible) {
    // Patch-only difference — compatible, advisory only.
    return {
      ok: true,
      serverVersion,
      bridgeVersion,
      message:
        `unity-open-mcp: version drift (server ${serverVersion}, bridge ${bridgeVersion}). ` +
        `Compatible (patch difference only). To silence this, align both releases.`,
    };
  }

  // Determine which side is older so the message can name the exact fix.
  const serverNewer =
    server[0] > bridge[0] ||
    (server[0] === bridge[0] && server[1] > bridge[1]) ||
    (server[0] === bridge[0] && server[1] === bridge[1] && server[2] > bridge[2]);

  const older = serverNewer ? "bridge" : "server";
  const targetVersion = serverNewer ? serverVersion : bridgeVersion;

  const fix =
    older === "bridge"
      ? `In Unity: Window → Package Manager → update the bridge package ` +
        `(tag bridge-v${targetVersion}).`
      : `Run: npm i -g unity-open-mcp@${targetVersion}`;

  return {
    ok: false,
    serverVersion,
    bridgeVersion,
    message:
      `unity-open-mcp: INCOMPATIBLE versions — server ${serverVersion}, bridge ${bridgeVersion}.\n` +
      `  The ${older} is older. To fix: ${fix}\n` +
      `  (Set UNITY_OPEN_MCP_SKIP_VERSION_CHECK=1 to suppress this warning.)`,
  };
}

/** True when the bridge version differs from the server at all. Used by callers
 *  that only want to know whether to render a compat line. */
export function versionsDiffer(
  bridgeVersion: string,
  serverVersion: string = SHARED_VERSION,
): boolean {
  return bridgeVersion !== serverVersion;
}
