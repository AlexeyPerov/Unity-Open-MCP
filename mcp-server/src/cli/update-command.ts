import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { dirname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

import type { CliCommandResult } from "./commands.js";

const execFileAsync = promisify(execFile);
const PACKAGE_NAME = "unity-open-mcp";
const NPM_LATEST_URL = `https://registry.npmjs.org/${PACKAGE_NAME}/latest`;
const GITHUB_RELEASES_URL =
  "https://api.github.com/repos/AlexeyPerov/Unity-Open-MCP/releases?per_page=30";
const REQUEST_TIMEOUT_MS = 10_000;

export const UPDATE_EXIT = {
  AVAILABLE: 10,
  LOOKUP_FAILED: 11,
  INSTALL_FAILED: 12,
} as const;

export type ReleaseSource = "npm" | "github";
export type InstallMode = "npx" | "global" | "local" | "unknown";

export interface ProcessResult {
  exitCode: number;
  stdout: string;
  stderr: string;
}

export interface UpdateCommandDependencies {
  fetch: typeof globalThis.fetch;
  runProcess: (
    command: string,
    args: string[],
    cwd?: string,
  ) => Promise<ProcessResult>;
  env: NodeJS.ProcessEnv;
  packageRoot: string;
  cwd: string;
  /** Reads a UTF-8 text file; rejects when it does not exist. */
  readTextFile: (path: string) => Promise<string>;
}

export interface UpdateCommandOptions {
  currentVersion: string;
  check: boolean;
  json: boolean;
  dependencies?: Partial<UpdateCommandDependencies>;
}

interface LatestVersion {
  version: string;
  source: ReleaseSource;
  fallbackReason?: string;
}

interface InstallTarget {
  mode: InstallMode;
  cwd?: string;
}

const defaultDependencies: UpdateCommandDependencies = {
  fetch: globalThis.fetch.bind(globalThis),
  runProcess: async (command, args, cwd) => {
    try {
      const result = await execFileAsync(command, args, {
        cwd,
        encoding: "utf8",
        maxBuffer: 4 * 1024 * 1024,
      });
      return { exitCode: 0, stdout: result.stdout, stderr: result.stderr };
    } catch (error) {
      const failed = error as Error & {
        code?: number | string;
        stdout?: string;
        stderr?: string;
      };
      return {
        exitCode: typeof failed.code === "number" ? failed.code : 1,
        stdout: failed.stdout ?? "",
        stderr: failed.stderr ?? failed.message,
      };
    }
  },
  env: process.env,
  packageRoot: resolve(dirname(fileURLToPath(import.meta.url)), "../.."),
  cwd: process.cwd(),
  readTextFile: (path) => readFile(path, "utf8"),
};

export async function runUpdateCommand(
  options: UpdateCommandOptions,
): Promise<CliCommandResult> {
  const deps = { ...defaultDependencies, ...options.dependencies };
  let latest: LatestVersion;
  try {
    latest = await resolveLatestVersion(deps.fetch);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return {
      exitCode: UPDATE_EXIT.LOOKUP_FAILED,
      json: {
        command: "update",
        check: options.check,
        currentVersion: options.currentVersion,
        status: "lookup_failed",
        error: { code: "update_lookup_failed", message },
      },
      human:
        `Could not check for updates: ${message}\n` +
        "No files were changed. Retry when npm or GitHub is reachable.",
      errorLabel: "update_lookup_failed",
    };
  }

  const comparison = compareSemver(latest.version, options.currentVersion);
  if (comparison === null) {
    const message =
      `Cannot compare installed version '${options.currentVersion}' with ` +
      `published version '${latest.version}'.`;
    return {
      exitCode: UPDATE_EXIT.LOOKUP_FAILED,
      json: {
        command: "update",
        check: options.check,
        currentVersion: options.currentVersion,
        latestVersion: latest.version,
        source: latest.source,
        status: "lookup_failed",
        error: { code: "invalid_update_version", message },
      },
      human: `${message}\nNo files were changed.`,
      errorLabel: "invalid_update_version",
    };
  }

  const available = comparison > 0;
  if (options.check) {
    const status = available ? "update_available" : "up_to_date";
    return {
      exitCode: available ? UPDATE_EXIT.AVAILABLE : 0,
      json: {
        command: "update",
        check: options.check,
        currentVersion: options.currentVersion,
        latestVersion: latest.version,
        source: latest.source,
        status,
        updateAvailable: available,
        fallbackReason: latest.fallbackReason,
      },
      human: available
        ? `Update available: ${options.currentVersion} → ${latest.version} (${latest.source}).`
        : `unity-open-mcp ${options.currentVersion} is up to date (${latest.source}).`,
    };
  }

  if (!available) {
    return {
      exitCode: 0,
      json: {
        command: "update",
        check: false,
        currentVersion: options.currentVersion,
        latestVersion: latest.version,
        source: latest.source,
        status: "up_to_date",
        updateAvailable: false,
        fallbackReason: latest.fallbackReason,
        scope: "mcp_server_only",
      },
      human: [
        `unity-open-mcp ${options.currentVersion} is up to date (${latest.source}).`,
        projectUpdateGuidance(),
      ].join("\n"),
    };
  }

  const target = await detectInstallTarget(deps);
  if (target.mode === "npx" || target.mode === "unknown") {
    const invocation = `npx -y ${PACKAGE_NAME}@latest update --check`;
    const reason =
      target.mode === "npx"
        ? "This process is running from npx's cache, which cannot safely replace itself."
        : "The active package is not an npm global or project-local install.";
    return {
      exitCode: 0,
      json: {
        command: "update",
        check: false,
        currentVersion: options.currentVersion,
        latestVersion: latest.version,
        source: latest.source,
        status: "guidance",
        updateAvailable: true,
        installMode: target.mode,
        applied: false,
        nextCommand: invocation,
      },
      human: [
        `Update available: ${options.currentVersion} → ${latest.version} (${latest.source}).`,
        reason,
        `Change the MCP client pin to ${PACKAGE_NAME}@${latest.version}, or verify the latest release with:`,
        `  ${invocation}`,
        projectUpdateGuidance(),
      ].join("\n"),
    };
  }

  const args =
    target.mode === "global"
      ? ["install", "--global", `${PACKAGE_NAME}@${latest.version}`]
      : ["install", `${PACKAGE_NAME}@${latest.version}`];
  const install = await deps.runProcess("npm", args, target.cwd);
  if (install.exitCode !== 0) {
    const detail = lastNonEmptyLine(install.stderr) || "npm exited non-zero";
    return {
      exitCode: UPDATE_EXIT.INSTALL_FAILED,
      json: {
        command: "update",
        check: false,
        currentVersion: options.currentVersion,
        latestVersion: latest.version,
        source: latest.source,
        status: "install_failed",
        installMode: target.mode,
        applied: false,
        error: { code: "update_install_failed", message: detail },
      },
      human:
        `npm could not update ${PACKAGE_NAME} to ${latest.version}: ${detail}\n` +
        "No Unity project or MCP client configuration was changed.",
      errorLabel: "update_install_failed",
    };
  }

  return {
    exitCode: 0,
    json: {
      command: "update",
      check: false,
      currentVersion: options.currentVersion,
      latestVersion: latest.version,
      source: latest.source,
      status: "updated",
      installMode: target.mode,
      applied: true,
    },
    human: [
      `Updated ${PACKAGE_NAME}: ${options.currentVersion} → ${latest.version} (${target.mode} npm install).`,
      "Restart the MCP client so it launches the new server.",
      projectUpdateGuidance(),
    ].join("\n"),
  };
}

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

async function detectInstallTarget(
  deps: UpdateCommandDependencies,
): Promise<InstallTarget> {
  // Comparisons run on a lower-cased copy, but any path handed back to npm
  // (the local install's cwd) must keep the on-disk casing: case-sensitive
  // filesystems reject a lower-cased directory with ENOENT.
  const forwardRoot = forwardSlashes(deps.packageRoot);
  const normalizedRoot = forwardRoot.toLowerCase();
  if (
    deps.env.npm_command === "exec" ||
    deps.env.npm_lifecycle_event === "npx" ||
    normalizedRoot.includes("/_npx/")
  ) {
    return { mode: "npx" };
  }

  const globalRoot = await deps.runProcess("npm", ["root", "--global"]);
  if (globalRoot.exitCode === 0) {
    const normalizedGlobal = normalizePath(globalRoot.stdout.trim());
    if (normalizedGlobal && isWithin(normalizedRoot, normalizedGlobal)) {
      return { mode: "global" };
    }
  }

  // A project-local install is only certain when the owning package.json
  // declares this package. Without that check a global install whose
  // `npm root -g` probe failed would be mistaken for a local one and npm
  // would be run inside the global lib directory.
  const markerIndex = forwardRoot.search(/\/node_modules\//i);
  if (markerIndex > 0) {
    const owner = denormalizePath(forwardRoot.slice(0, markerIndex));
    if (await declaresDependency(deps, owner)) {
      return { mode: "local", cwd: owner };
    }
  }
  return { mode: "unknown", cwd: deps.cwd };
}

async function declaresDependency(
  deps: UpdateCommandDependencies,
  projectDir: string,
): Promise<boolean> {
  try {
    const manifest: unknown = JSON.parse(
      await deps.readTextFile(join(projectDir, "package.json")),
    );
    if (!isRecord(manifest)) return false;
    return ["dependencies", "devDependencies", "optionalDependencies"].some(
      (field) => {
        const section = manifest[field];
        return isRecord(section) && typeof section[PACKAGE_NAME] === "string";
      },
    );
  } catch {
    return false;
  }
}

function projectUpdateGuidance(): string {
  return (
    "This command updates only the MCP server. Next, update both Unity packages and client pins " +
    "from the bridge window's Updates section (or use the setup wizard/manual package path), " +
    "then run `unity-open-mcp ping` or `unity-open-mcp status` to confirm versions."
  );
}

interface Semver {
  major: number;
  minor: number;
  patch: number;
  prerelease: string[];
}

function parseSemver(value: string): Semver | null {
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

function forwardSlashes(path: string): string {
  return path.replaceAll("\\", "/").replace(/\/$/, "");
}

function normalizePath(path: string): string {
  return forwardSlashes(path).toLowerCase();
}

function denormalizePath(path: string): string {
  return sep === "\\" ? path.replaceAll("/", "\\") : path;
}

function isWithin(candidate: string, parent: string): boolean {
  return candidate === parent || candidate.startsWith(`${parent}/`);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function lastNonEmptyLine(value: string): string {
  return (
    value
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter(Boolean)
      .at(-1) ?? ""
  );
}
