// Portable Unity project-path resolution.
//
// One resolver shared by the stdio server bootstrap (`index.ts`), the thin
// CLI (`routers.ts` → `resolveEnv`), and the `setup` config writers. It turns
// whatever the caller has — an env var, a `--project` flag, or nothing but the
// spawn cwd — into ONE absolute Unity project root, so committed MCP client
// config never has to carry a per-machine path.
//
// The module is deliberately light (only `node:fs` + `node:path`): `index.ts`
// imports it statically on the pre-`getEnv()` path, which must not pull the
// heavy ALL_TOOLS graph.

import { existsSync, statSync } from "node:fs";
import * as nodePath from "node:path";

/** Directories that must exist directly under a Unity project root. */
export const UNITY_ROOT_MARKERS = ["Assets", "Packages", "ProjectSettings"] as const;

/**
 * Where the resolved path came from. Reported on stderr at startup so a user
 * staring at a wrong bridge port can see which input won.
 */
export type ProjectPathSource = "flag" | "env" | "cwd" | "cwd+subpath";

export interface ResolvedProjectPath {
  /** Normalized absolute Unity project root. */
  absolute: string;
  source: ProjectPathSource;
}

/**
 * The slice of `node:path` this module uses. Declared as an interface so tests
 * can pass `path.win32` / `path.posix` and assert both platform behaviours
 * from one host. Production callers omit it and get `node:path`.
 */
export interface PathApi {
  isAbsolute(p: string): boolean;
  resolve(...segments: string[]): string;
  normalize(p: string): string;
  join(...segments: string[]): string;
}

export interface ResolveProjectPathOptions {
  /** Explicit override (CLI `--project`). Highest priority. */
  flagPath?: string;
  /** `UNITY_PROJECT_PATH`. Absolute or relative to `cwd`. */
  envPath?: string;
  /** Spawn cwd used to resolve relative inputs. Defaults to `process.cwd()`. */
  cwd?: string;
  /** `--project-from-cwd`: derive the project root from the spawn cwd. */
  projectFromCwd?: boolean;
  /** `--unity-subpath`: relative segment under cwd (monorepo, e.g. "Client"). */
  unitySubpath?: string;
  /** Test seam; defaults to `node:path`. */
  pathApi?: PathApi;
}

export class ProjectPathError extends Error {
  constructor(readonly code: string, message: string) {
    super(message);
    this.name = "ProjectPathError";
  }
}

/**
 * Resolve the Unity project root. First match wins:
 *
 *   1. `flagPath` (CLI `--project`) — an explicit argument beats ambient state.
 *   2. `envPath` (`UNITY_PROJECT_PATH`) — keeps every existing client config
 *      working, and wins over `--project-from-cwd` so a machine-local env
 *      override can still redirect a committed config.
 *   3. `projectFromCwd` (+ optional `unitySubpath`) — the portable path.
 *   4. nothing → `ProjectPathError` with an actionable message.
 *
 * Relative `flagPath` / `envPath` values resolve against `cwd`, so
 * `--project Client` works from a monorepo root. The result is always
 * absolute: bridge port hashing and instance locks are keyed on it.
 *
 * Does NOT touch the filesystem — validation is `validateUnityProjectRoot`,
 * which callers apply according to how strict they need to be.
 */
export function resolveProjectPath(
  options: ResolveProjectPathOptions = {},
): ResolvedProjectPath {
  const p = options.pathApi ?? nodePath;
  const cwd = options.cwd ?? process.cwd();

  const flag = trimmed(options.flagPath);
  if (flag) return { absolute: absolutize(p, cwd, flag), source: "flag" };

  const env = trimmed(options.envPath);
  if (env) return { absolute: absolutize(p, cwd, env), source: "env" };

  if (options.projectFromCwd) {
    const subpath = trimmed(options.unitySubpath);
    if (subpath) {
      if (p.isAbsolute(subpath)) {
        throw new ProjectPathError(
          "subpath_not_relative",
          `--unity-subpath must be relative to the workspace (received '${subpath}').`,
        );
      }
      return {
        absolute: absolutize(p, cwd, subpath),
        source: "cwd+subpath",
      };
    }
    return { absolute: absolutize(p, cwd, "."), source: "cwd" };
  }

  if (trimmed(options.unitySubpath)) {
    throw new ProjectPathError(
      "subpath_without_cwd",
      "--unity-subpath requires --project-from-cwd (or an explicit project path).",
    );
  }

  throw new ProjectPathError("missing_project_path", missingProjectPathMessage());
}

/** Shared wording so the server, the CLI, and setup all suggest the same fix. */
export function missingProjectPathMessage(): string {
  return [
    "No Unity project path. Use one of:",
    "  --project-from-cwd                       run from the Unity project root",
    "  --project-from-cwd --unity-subpath Client  monorepo: Unity project in a subfolder",
    "  --project <path>                         explicit path (absolute or relative)",
    "  UNITY_PROJECT_PATH=<path>                environment variable",
  ].join("\n");
}

export interface UnityRootValidation {
  valid: boolean;
  /** Marker directories that are absent, in `UNITY_ROOT_MARKERS` order. */
  missing: string[];
}

/**
 * Check that `absolute` looks like a Unity project root (`Assets/`,
 * `Packages/`, `ProjectSettings/`). Never throws — callers decide whether a
 * miss is fatal.
 */
export function validateUnityProjectRoot(absolute: string): UnityRootValidation {
  const missing = UNITY_ROOT_MARKERS.filter((marker) => {
    const candidate = nodePath.join(absolute, marker);
    try {
      return !existsSync(candidate) || !statSync(candidate).isDirectory();
    } catch {
      return true;
    }
  });
  return { valid: missing.length === 0, missing };
}

/** One-line explanation of a failed {@link validateUnityProjectRoot}. */
export function notUnityProjectMessage(
  resolved: ResolvedProjectPath,
  validation: UnityRootValidation,
): string {
  return (
    `${resolved.absolute} is not a Unity project root (from ${resolved.source}): ` +
    `missing ${validation.missing.map((m) => `${m}/`).join(", ")}.`
  );
}

/** Parsed portable flags accepted before the stdio server boots. */
export interface ServerFlags {
  projectFromCwd: boolean;
  unitySubpath: string | undefined;
  error: string | undefined;
}

/**
 * Pre-parse the portable project-path flags out of the stdio server's argv.
 * Unknown tokens are ignored on purpose: MCP clients pass through arbitrary
 * extra arguments, and the server has always tolerated them.
 */
export function parseServerFlags(argv: string[]): ServerFlags {
  const flags: ServerFlags = {
    projectFromCwd: false,
    unitySubpath: undefined,
    error: undefined,
  };
  for (let i = 0; i < argv.length; i++) {
    const tok = argv[i];
    if (tok === "--project-from-cwd") {
      flags.projectFromCwd = true;
      continue;
    }
    if (tok === "--unity-subpath") {
      const value = argv[i + 1];
      if (!value || value.startsWith("-")) {
        flags.error = "--unity-subpath requires a relative path (for example: Client).";
        return flags;
      }
      flags.unitySubpath = value;
      i++;
      continue;
    }
    const inline = matchInline(tok, "--unity-subpath");
    if (inline !== undefined) {
      if (inline.length === 0) {
        flags.error = "--unity-subpath requires a relative path (for example: Client).";
        return flags;
      }
      flags.unitySubpath = inline;
      continue;
    }
  }
  return flags;
}

function matchInline(tok: string, flag: string): string | undefined {
  const prefix = `${flag}=`;
  return tok.startsWith(prefix) ? tok.slice(prefix.length) : undefined;
}

function trimmed(value: string | undefined): string | undefined {
  if (typeof value !== "string") return undefined;
  const t = value.trim();
  return t.length > 0 ? t : undefined;
}

/**
 * Resolve `input` against `cwd` and normalize. `p.resolve` already returns an
 * absolute path and strips a trailing separator, which matters because the
 * bridge port hash is taken over this exact string.
 */
function absolutize(p: PathApi, cwd: string, input: string): string {
  return p.isAbsolute(input)
    ? p.normalize(p.resolve(input))
    : p.normalize(p.resolve(cwd, input));
}
