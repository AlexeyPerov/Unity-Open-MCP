// M13 T4.3 / T4.7 — Per-project instance discovery.
//
// Mirror of the bridge-side InstancePortResolver (packages/bridge/Editor/Bridge/
// InstancePortResolver.cs) + BridgeInstanceLock. The MCP server uses these to
// find the right bridge instance for a given Unity project without any shared
// config:
//
//   1. UNITY_OPEN_MCP_BRIDGE_PORT env var (override wins)
//   2. ~/.unity-agent/instances/<sha256(projectPath)>.json — the bridge's
//      instance lock / heartbeat file. We trust its `port` only when its
//      `pid` is still alive; a stale lock (crashed Unity) falls through.
//   3. deterministic hash of the project path (20000 + sha256 % 10000).
//
// The hash formula MUST match the bridge exactly: first 8 bytes (16 hex chars)
// of SHA256(normalizedPath) as a big-endian 64-bit unsigned integer, mod 10000,
// + 20000. Cross-side consistency is pinned by tests in
// instance-discovery.test.ts and the bridge InstancePortResolverTests.cs.
//
// No external deps (only node:crypto, node:fs, node:os, node:path) so the
// "no runtime deps beyond MCP SDK" rule (mcp-server/AGENTS.md) holds.

import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

export const PORT_RANGE_START = 20000;
export const PORT_RANGE_SIZE = 10000;

/** Editor state values the bridge writes into its lock / heartbeat file. */
export type InstanceState =
  | "idle"
  | "compiling"
  | "reloading"
  | "entering_playmode"
  | "playing"
  | "exiting_playmode";

/** Shape of ~/.unity-agent/instances/<hash>.json (bridge BridgeInstanceLock). */
export interface InstanceLock {
  pid: number;
  port: number;
  /** M14 — per-session bearer token. Optional for back-compat with locks
   *  written by older bridges; when absent the MCP client sends no header
   *  (authMode "none" accepts that). */
  authToken?: string;
  projectPath: string;
  projectHash: string;
  startedAt: string;
  updatedAt: string;
  heartbeatAt: string;
  state: InstanceState;
  isPlaying: boolean;
  isCompiling: boolean;
  bridgeVersion: string;
  unityVersion: string;
}

/**
 * Path normalization applied BEFORE hashing. Mirrors
 * InstancePortResolver.NormalizePath in the bridge:
 *   - backslashes -> forward slashes (Windows paths hash the same cross-platform)
 *   - trailing slashes trimmed
 * No lowercasing — macOS/Linux paths are case-sensitive.
 */
export function normalizePath(projectPath: string): string {
  if (!projectPath) return "";
  let norm = projectPath.replace(/\\/g, "/");
  while (norm.length > 1 && norm.endsWith("/")) {
    norm = norm.slice(0, -1);
  }
  return norm;
}

/** Lowercase hex SHA256 of the normalized project path. */
export function projectHash(projectPath: string): string {
  return createHash("sha256").update(normalizePath(projectPath), "utf8").digest("hex");
}

/**
 * Deterministic port for a project path: 20000 + (sha256(path) % 10000).
 * Uses the first 8 bytes of the hash as a BigInt so the modulo matches the
 * C# UInt64 computation exactly.
 */
export function computePort(projectPath: string): number {
  const hash = projectHash(projectPath);
  const prefix = BigInt("0x" + hash.slice(0, 16));
  return PORT_RANGE_START + Number(prefix % BigInt(PORT_RANGE_SIZE));
}

/** Directory holding one lock file per running bridge instance. */
export function instancesDir(): string {
  return join(homedir(), ".unity-agent", "instances");
}

/** Path to this project's instance lock file. */
export function lockPath(projectPath: string): string {
  return join(instancesDir(), `${projectHash(projectPath)}.json`);
}

/**
 * Read this project's instance lock from disk. Returns null if the file is
 * missing, unreadable, or doesn't parse. Never throws — the caller falls
 * through to the deterministic hash on any failure.
 */
export function readInstanceLock(projectPath: string): InstanceLock | null {
  const path = lockPath(projectPath);
  if (!existsSync(path)) return null;
  let raw: string;
  try {
    raw = readFileSync(path, "utf8");
  } catch {
    return null;
  }
  try {
    return JSON.parse(raw) as InstanceLock;
  } catch {
    return null;
  }
}

/**
 * kill -0 equivalent. Returns true if a process with the given pid exists.
 * Wrapped in try/catch: EPERM (exists but can't be probed) → true,
 * ESRCH (no such process) → false. Mirrors the C# Process.GetProcessById
 * logic in BridgeInstanceLock.IsPidAlive.
 */
export function isPidAlive(pid: number): boolean {
  if (!pid || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code;
    if (code === "EPERM") return true; // exists but we can't probe it
    return false; // ESRCH or anything else → treat as dead
  }
}

/**
 * Resolve the bridge port for a project, with override precedence:
 *   1. explicit envPort (already parsed + validated by the caller)
 *   2. live instance lock's port (only when its pid is alive)
 *   3. deterministic hash
 *
 * @param projectPath absolute Unity project root
 * @param envPort     parsed UNITY_OPEN_MCP_BRIDGE_PORT, or undefined
 */
export function resolvePort(projectPath: string, envPort?: number): number {
  if (typeof envPort === "number" && Number.isInteger(envPort) && envPort >= 1 && envPort <= 65535) {
    return envPort;
  }

  const lock = readInstanceLock(projectPath);
  if (lock && typeof lock.port === "number" && isPidAlive(lock.pid)) {
    return lock.port;
  }

  return computePort(projectPath);
}

/**
 * M14 — Resolve the bridge's per-session bearer token for a project.
 *
 * The token is only discoverable from a live instance lock (the same file we
 * read the port from). When an explicit env port override is in use there is
 * no lock file to read, so this returns undefined — in that case the MCP
 * client sends no Authorization header and the bridge must be in authMode
 * "none" for the request to succeed. Returns undefined when the lock is
 * missing, stale (dead pid), or predates the token field.
 *
 * @param projectPath absolute Unity project root
 * @param envPort     parsed UNITY_OPEN_MCP_BRIDGE_PORT, or undefined. When set,
 *                    skips the lock read (no token to discover).
 */
export function resolveAuthToken(projectPath: string, envPort?: number): string | undefined {
  if (typeof envPort === "number" && Number.isInteger(envPort) && envPort >= 1 && envPort <= 65535) {
    return undefined;
  }
  const lock = readInstanceLock(projectPath);
  if (!lock || !isPidAlive(lock.pid)) return undefined;
  const token = lock.authToken;
  return typeof token === "string" && token.length > 0 ? token : undefined;
}
