import { copySkillReferences } from "../skill/copy-references.js";
import { existsSync, readdirSync } from "node:fs";
import { readFile, mkdir, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

import {
  clientSkillRelativePath,
  knownClientKeys,
  resolveTemplateSkillPath,
} from "../skill/client-paths.js";
import { PROJECT_PATH_ENV_VAR } from "../constants.js";
import { validateUnityProjectRoot } from "../project-path.js";
import { isRecord } from "./json-guards.js";
import { CodexTomlError, mergeCodexServer, renderCodexServer } from "../setup/codex-toml.js";
import {
  portableEnv,
  portableServerArgs,
  portableSupportFor,
  renderWrapperScript,
  wrapperRelativePath,
  type ConfigStrategy,
  type PortableClientSupport,
  type PortableTargetOptions,
  type ProjectLayout,
} from "../setup/portable-config.js";

const BRIDGE_PACKAGE = "com.alexeyperov.unity-open-mcp-bridge";
const VERIFY_PACKAGE = "com.alexeyperov.unity-open-mcp-verify";
const REPOSITORY_URL = "https://github.com/AlexeyPerov/unity-open-mcp.git";
const SERVER_KEY = "unity-open-mcp";

/** How a client's config file spells the server entry. */
type SetupConfigFormat = "mcpServers" | "opencode" | "vscode" | "zcode" | "codex";

export interface SetupClientSpec {
  /** `--client` value. */
  id: string;
  /** Config file relative to the workspace root, POSIX separators. */
  configPath: string;
  format: SetupConfigFormat;
  /** Skill target key in skills/client-paths.json `clients`. */
  skillKey: string;
  /** Row in the portable-config client matrix. */
  catalogId: string;
}

/**
 * Clients `setup` can write a config for. The skill key follows
 * `mcpClientMapping` in skills/client-paths.json (Codex installs into the
 * shared `.agents/skills`); a unit test keeps the two in step.
 */
export const SETUP_CLIENTS: readonly SetupClientSpec[] = [
  { id: "cursor", configPath: ".cursor/mcp.json", format: "mcpServers", skillKey: "cursor", catalogId: "cursor" },
  { id: "claude", configPath: ".mcp.json", format: "mcpServers", skillKey: "claude", catalogId: "claude-code" },
  { id: "zcode", configPath: ".zcode/config.json", format: "zcode", skillKey: "agents", catalogId: "zcode" },
  { id: "vscode", configPath: ".vscode/mcp.json", format: "vscode", skillKey: "github", catalogId: "vscode-copilot" },
  { id: "codex", configPath: ".codex/config.toml", format: "codex", skillKey: "agents", catalogId: "codex" },
  { id: "opencode", configPath: "opencode.json", format: "opencode", skillKey: "opencode", catalogId: "opencode" },
  { id: "agents", configPath: ".mcp.json", format: "mcpServers", skillKey: "agents", catalogId: "agents" },
];

/** Where the Unity project came from. */
export type SetupProjectSource = "flag" | "workspace" | "cwd" | "cwd+subpath";

/** How many folders above the workspace setup looks for a repository root. */
const REPO_DETECT_DEPTH = 4;

export interface SetupCommandOptions {
  version: string;
  projectPath: string | undefined;
  client: string | undefined;
  skipSkill: boolean;
  dryRun: boolean;
  /**
   * Which portable template family to emit. Defaults to `monorepo` when the
   * Unity root is below the workspace root, otherwise `unity-root`.
   */
  layout?: ProjectLayout;
  /** Repository root the AI client is opened on. Defaults to the Unity root. */
  workspacePath?: string;
  /** Unity project folder relative to the workspace root (for example "Client"). */
  unitySubpath?: string;
  /**
   * Force a committable config with no machine path (`--portable`) or the
   * absolute form (`--no-portable`). Defaults to portable for monorepo
   * layouts and absolute for a plain Unity-root workspace.
   */
  portable?: boolean;
  /** Also write the shipped wrapper script, even when the client does not need it. */
  wrapper?: boolean;
  /** Test/development override; published builds use dist/skill/SKILL.md. */
  skillSourcePath?: string;
  /** Test seams; default to the running process. */
  cwd?: string;
  env?: Record<string, string | undefined>;
  platform?: NodeJS.Platform;
  homeDir?: string;
}

export interface SetupCommandResult {
  exitCode: number;
  json: SetupReport | SetupErrorReport;
  human: string;
  errorLabel?: string;
}

export interface SetupReport {
  version: string;
  project: string;
  /** `flag` (--project), `workspace` (--workspace [+ --unity-subpath]), `cwd`, or `cwd+subpath`. */
  projectSource: SetupProjectSource;
  client: string;
  dryRun: boolean;
  /** Repository root the client config and skill are written under. */
  workspace: string;
  layout: ProjectLayout;
  /** Unity root relative to the workspace, POSIX-separated; "" for unity-root. */
  unitySubpath: string;
  /** True when the written config carries no machine-specific path. */
  portable: boolean;
  configStrategy: ConfigStrategy;
  wrapper: { path: string | null; written: boolean };
  manifest: {
    path: string;
    written: boolean;
    dependencies: Record<string, string>;
  };
  mcpConfig: {
    path: string;
    written: boolean;
    entry: Record<string, unknown>;
  };
  skill: {
    skipped: boolean;
    path: string | null;
    written: boolean;
    bytes: number;
    lines: number;
  };
  warnings: string[];
  userAction: string[];
}

interface SetupErrorReport {
  version: string;
  project: string | null;
  client: string | null;
  dryRun: boolean;
  error: { code: string; message: string };
  warnings: string[];
}

class SetupError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly exitCode: 1 | 2,
  ) {
    super(message);
  }
}

export async function runSetupCommand(
  opts: SetupCommandOptions,
): Promise<SetupCommandResult> {
  try {
    const cwd = opts.cwd ?? process.cwd();
    const client = validateClient(opts.client);
    const { project, source } = resolveSetupProject(opts, cwd);
    const pins = packagePins(opts.version);
    const placement = resolvePlacement(project, client, opts, cwd);
    const warnings = [...placement.warnings];
    const envProject = (opts.env ?? process.env)[PROJECT_PATH_ENV_VAR]?.trim();
    if (envProject && !samePath(resolve(cwd, envProject), project)) {
      warnings.push(
        `${PROJECT_PATH_ENV_VAR} is set to ${envProject} in this shell; setup ignores it and configures ${project}.`,
      );
    }

    const manifestPath = join(project, "Packages", "manifest.json");
    const manifest = await readJsonObject(manifestPath, false);
    const dependencies = manifest.dependencies;
    if (!isRecord(dependencies)) {
      throw new SetupError(
        "invalid_manifest",
        `${manifestPath} must contain a JSON object at 'dependencies'.`,
        1,
      );
    }
    dependencies[BRIDGE_PACKAGE] = pins.bridge;
    dependencies[VERIFY_PACKAGE] = pins.verify;

    // Client config and the agent skill live where the AI client is opened —
    // the workspace root, which equals the Unity root for a plain layout.
    const clientConfig = await planClientConfig(client, project, pins.npm, placement);
    const configPath = clientConfig.path;
    const entry = clientConfig.entry;

    const wrapperPath = placement.emitWrapper
      ? join(placement.workspace, ...wrapperRelativePath(placement.layout).split("/"))
      : null;
    const wrapperBody = placement.emitWrapper
      ? renderWrapperScript({
          version: opts.version,
          layout: placement.layout,
          unitySubpath: placement.unitySubpath,
        })
      : null;

    let skillBytes: Buffer | null = null;
    let skillPath: string | null = null;
    if (!opts.skipSkill) {
      skillPath = join(placement.workspace, clientSkillRelativePath(client.skillKey));
      const source = opts.skillSourcePath ?? resolveBundledSkillPath();
      if (!source) {
        throw new SetupError(
          "skill_not_bundled",
          "The packaged core skill could not be located. Reinstall unity-open-mcp and retry.",
          1,
        );
      }
      try {
        skillBytes = await readFile(source);
      } catch (error) {
        throw ioError(`Could not read bundled skill at ${source}`, error);
      }
    }

    if (!opts.dryRun) {
      await writeJson(manifestPath, manifest);
      await writeText(configPath, clientConfig.body);
      if (skillBytes && skillPath) {
        try {
          await mkdir(dirname(skillPath), { recursive: true });
          await writeFile(skillPath, skillBytes);
          await copySkillReferences(opts.skillSourcePath ?? resolveBundledSkillPath()!, skillPath);
        } catch (error) {
          throw ioError(`Could not write skill to ${skillPath}`, error);
        }
      }
      if (wrapperPath && wrapperBody !== null) {
        try {
          await mkdir(dirname(wrapperPath), { recursive: true });
          await writeFile(wrapperPath, wrapperBody, { mode: 0o755 });
        } catch (error) {
          throw ioError(`Could not write wrapper script to ${wrapperPath}`, error);
        }
      }
    }

    const report: SetupReport = {
      version: opts.version,
      project,
      projectSource: source,
      client: client.id,
      dryRun: opts.dryRun,
      workspace: placement.workspace,
      layout: placement.layout,
      unitySubpath: placement.unitySubpath,
      portable: placement.portable,
      configStrategy: placement.strategy,
      wrapper: {
        path: wrapperPath,
        written: !opts.dryRun && wrapperPath !== null,
      },
      manifest: {
        path: manifestPath,
        written: !opts.dryRun,
        dependencies: {
          [BRIDGE_PACKAGE]: pins.bridge,
          [VERIFY_PACKAGE]: pins.verify,
        },
      },
      mcpConfig: {
        path: configPath,
        written: !opts.dryRun,
        entry,
      },
      skill: {
        skipped: opts.skipSkill,
        path: skillPath,
        written: !opts.dryRun && !opts.skipSkill,
        bytes: skillBytes?.byteLength ?? 0,
        lines: skillBytes ? countLines(skillBytes) : 0,
      },
      warnings,
      userAction: [
        `Open Unity with ${project} and wait for compilation to finish.`,
        "Restart the MCP / AI client so it reloads the updated configuration.",
        ...(client.format === "codex"
          ? [`Trust ${placement.workspace} in Codex — it reads .codex/config.toml only for a trusted project.`]
          : []),
      ],
    };
    return { exitCode: 0, json: report, human: formatSetupReport(report) };
  } catch (error) {
    const setupError = error instanceof SetupError
      ? error
      : new SetupError(
          "setup_failed",
          error instanceof Error ? error.message : String(error),
          1,
        );
    const report: SetupErrorReport = {
      version: opts.version,
      project: opts.projectPath ?? null,
      client: opts.client ?? null,
      dryRun: opts.dryRun,
      error: { code: setupError.code, message: setupError.message },
      warnings: [],
    };
    return {
      exitCode: setupError.exitCode,
      json: report,
      human: `Setup failed: ${setupError.message}`,
      errorLabel: setupError.code,
    };
  }
}

/**
 * The Unity project this run configures. First match wins:
 *
 *   1. `--project <path>` — absolute, or relative to the working directory.
 *   2. `--workspace <path>` (+ `--unity-subpath <rel>`) — the Unity folder
 *      under an explicit repository root.
 *   3. `--unity-subpath <rel>` — a monorepo, run from the repository root.
 *   4. the working directory itself.
 *
 * `UNITY_PROJECT_PATH` is deliberately not an input: an agent's shell can
 * carry a stale value from another project, and setup writes files.
 */
function resolveSetupProject(
  opts: SetupCommandOptions,
  cwd: string,
): { project: string; source: SetupProjectSource } {
  const subpath = normalizeSubpath(opts.unitySubpath);
  const flag = opts.projectPath?.trim();
  const workspace = opts.workspacePath?.trim();
  let project: string;
  let source: SetupProjectSource;
  if (flag) {
    project = resolve(cwd, flag);
    source = "flag";
  } else if (workspace) {
    project = resolve(cwd, workspace, ...subpath.split("/").filter(Boolean));
    source = "workspace";
  } else if (subpath) {
    project = resolve(cwd, ...subpath.split("/"));
    source = "cwd+subpath";
  } else {
    project = resolve(cwd);
    source = "cwd";
  }

  const validation = validateUnityProjectRoot(project);
  if (!validation.valid) {
    const missing = validation.missing.map((m) => `${m}/`).join(", ");
    throw new SetupError(
      "not_unity_project",
      `${project} is not a Unity project root: missing ${missing}. ${notUnityProjectHint(project, source)}`,
      2,
    );
  }
  return { project, source };
}

/** Point at a Unity project just below the folder that missed, when there is one. */
function notUnityProjectHint(searched: string, source: SetupProjectSource): string {
  const found = findUnityProjectsBelow(searched, 2);
  if (found.length > 0) {
    const shown = found.slice(0, 3);
    if (source === "cwd") {
      return `Found a Unity project at ${shown.map((rel) => `${rel}/`).join(", ")} — run again with --unity-subpath ${quoteArg(shown[0])}.`;
    }
    return `Found a Unity project at ${shown.map((rel) => join(searched, rel)).join(", ")} — pass that folder instead.`;
  }
  return source === "flag"
    ? "Pass the folder that contains Assets/, Packages/, and ProjectSettings/."
    : "Run setup in the folder that contains Assets/, Packages/, and ProjectSettings/, or pass --project <path>.";
}

/** Folders that never hold a Unity project worth suggesting. */
const SEARCH_PRUNED = new Set([
  "node_modules", "Library", "Temp", "Logs", "Build", "Builds", "obj", "bin", "dist",
]);

/** Unity roots up to `depth` levels below `root`, as POSIX relative paths. */
function findUnityProjectsBelow(root: string, depth: number): string[] {
  const found: string[] = [];
  const walk = (dir: string, rel: string, level: number) => {
    if (level > depth) return;
    let entries: import("node:fs").Dirent[];
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      if (!e.isDirectory() || e.name.startsWith(".") || SEARCH_PRUNED.has(e.name)) continue;
      const child = join(dir, e.name);
      const childRel = rel ? `${rel}/${e.name}` : e.name;
      if (validateUnityProjectRoot(child).valid) found.push(childRel);
      else walk(child, childRel, level + 1);
    }
  };
  walk(root, "", 1);
  return found.sort();
}

function validateClient(input: string | undefined): SetupClientSpec {
  const known = SETUP_CLIENTS.map((c) => c.id);
  if (!input) {
    throw new SetupError(
      "missing_client",
      `setup requires --client <id>. Known ids: ${known.join(", ")}.`,
      2,
    );
  }
  const spec = SETUP_CLIENTS.find((c) => c.id === input);
  if (spec) return spec;
  if (knownClientKeys().includes(input)) {
    throw new SetupError(
      "unsupported_client_config",
      `Client '${input}' has a skill path but no setup config writer. Configure MCP manually; skill-only is not enough. Setup config writers: ${known.join(", ")}.`,
      2,
    );
  }
  throw new SetupError(
    "unknown_client",
    `Unknown client '${input}'. Known ids: ${known.join(", ")}.`,
    2,
  );
}

/**
 * Where the client config goes and how it names the Unity project.
 *
 * `workspace` is the folder the AI client is opened on. It equals the Unity
 * root in the common case; for a monorepo (`<repo>/Client`) the config and the
 * agent skill belong at the repo root while UPM pins still go to the Unity
 * root. `portable` decides between a committable snippet and the legacy
 * absolute path.
 */
interface SetupPlacement {
  workspace: string;
  layout: ProjectLayout;
  /** POSIX-separated Unity root relative to the workspace; "" for unity-root. */
  unitySubpath: string;
  portable: boolean;
  strategy: ConfigStrategy;
  support: PortableClientSupport | undefined;
  emitWrapper: boolean;
  warnings: string[];
}

function resolvePlacement(
  project: string,
  client: SetupClientSpec,
  opts: SetupCommandOptions,
  cwd: string,
): SetupPlacement {
  const warnings: string[] = [];
  const requestedSubpath = normalizeSubpath(opts.unitySubpath);

  let workspace: string;
  if (opts.workspacePath?.trim()) {
    workspace = resolve(cwd, opts.workspacePath.trim());
  } else if (requestedSubpath) {
    // Derive the workspace by stripping the subpath off the Unity root, so
    // `--project /abs/repo/Client --unity-subpath Client` needs no --workspace.
    workspace = resolve(project, ...requestedSubpath.split("/").map(() => ".."));
  } else {
    workspace = project;
  }

  const derived = relativeSubpath(workspace, project);
  if (derived === undefined) {
    throw new SetupError(
      "project_outside_workspace",
      `--project ${project} is not inside --workspace ${workspace}.`,
      2,
    );
  }
  if (requestedSubpath && requestedSubpath !== derived) {
    throw new SetupError(
      "subpath_mismatch",
      `--unity-subpath '${requestedSubpath}' does not match the Unity root under the workspace ('${derived || "."}').`,
      2,
    );
  }
  const unitySubpath = derived;

  const layout: ProjectLayout =
    opts.layout ?? (unitySubpath ? "monorepo" : "unity-root");
  if (layout === "monorepo" && !unitySubpath) {
    throw new SetupError(
      "missing_unity_subpath",
      "--layout monorepo requires --unity-subpath <relative-path> (or --workspace <repo root>).",
      2,
    );
  }
  if (layout === "unity-root" && unitySubpath) {
    throw new SetupError(
      "layout_conflict",
      `--layout unity-root conflicts with a Unity project at '${unitySubpath}' below the workspace; use --layout monorepo.`,
      2,
    );
  }

  const support = portableSupportFor(client.catalogId);
  const repoRoot = findRepoRoot(workspace, opts.homeDir ?? homedir());
  // Portable by default wherever the config is likely to be committed: a
  // monorepo, or a workspace that is itself a repository root — the same
  // default the bridge window and the Hub wizard use.
  let portable = opts.portable ?? (layout === "monorepo" || repoRoot === workspace);
  if (portable && support?.strategy === "absolute") {
    warnings.push(
      `Client '${client.id}' has no portable configuration form; writing the absolute path.`,
    );
    portable = false;
  }
  if (portable && !support) {
    warnings.push(
      `Client '${client.id}' is not in the portable-config catalog; writing the absolute path.`,
    );
    portable = false;
  }
  if (portable && support?.strategy === "wrapper" && (opts.platform ?? process.platform) === "win32") {
    if (opts.portable === undefined) {
      warnings.push(
        `The portable ${client.id} entry runs a bash wrapper script, which Windows cannot start without Git Bash or WSL; writing the absolute path. Pass --portable to write the wrapper anyway.`,
      );
      portable = false;
    } else {
      warnings.push(
        `The ${client.id} wrapper is a bash script: on Windows it needs Git Bash or WSL on PATH.`,
      );
    }
  }

  // The Unity project sits inside a repository whose root is above the
  // workspace: an AI client opened on that root never reads this config.
  if (repoRoot && repoRoot !== workspace && !opts.workspacePath?.trim()) {
    const rel = relativeSubpath(repoRoot, project);
    if (rel) {
      warnings.push(
        `${project} is inside the repository ${repoRoot}. An AI client opened on ${repoRoot} does not read ${join(workspace, ...client.configPath.split("/"))}. ` +
          `To configure the repository instead, run from ${repoRoot}: npx -y unity-open-mcp@${opts.version} setup --client ${client.id} --unity-subpath ${quoteArg(rel)}`,
      );
    }
  }

  const strategy: ConfigStrategy = portable && support ? support.strategy : "absolute";
  const emitWrapper = (portable && strategy === "wrapper") || opts.wrapper === true;

  return {
    workspace,
    layout,
    unitySubpath,
    portable,
    strategy,
    support,
    emitWrapper,
    warnings,
  };
}

/** `Client`, `Client/`, `.\Client` → `Client`; empty/`.` → "". */
function normalizeSubpath(input: string | undefined): string {
  if (!input) return "";
  const posix = input.split("\\").join("/");
  const segments = posix
    .split("/")
    .map((segment) => segment.trim())
    .filter((segment) => segment.length > 0 && segment !== ".");
  if (segments.some((segment) => segment === "..")) {
    throw new SetupError(
      "invalid_unity_subpath",
      `--unity-subpath must stay inside the workspace (received '${input}').`,
      2,
    );
  }
  return segments.join("/");
}

/**
 * POSIX-separated path from `workspace` down to `project`, "" when they are the
 * same folder, `undefined` when `project` is outside `workspace`.
 */
function relativeSubpath(workspace: string, project: string): string | undefined {
  const rel = relative(workspace, project);
  if (rel === "") return "";
  if (rel.startsWith("..") || isAbsolute(rel)) return undefined;
  return rel.split(sep).join("/");
}

export function packagePins(version: string): {
  npm: string;
  bridge: string;
  verify: string;
} {
  return {
    npm: `unity-open-mcp@${version}`,
    bridge: `${REPOSITORY_URL}?path=packages/bridge#bridge-v${version}`,
    verify: `${REPOSITORY_URL}?path=packages/verify#verify-v${version}`,
  };
}

/**
 * Nearest folder at or above `start` (up to {@link REPO_DETECT_DEPTH} levels)
 * that holds a `.git` entry, stopping before the home directory — a dotfiles
 * repository in `$HOME` must not make every project look committed.
 */
function findRepoRoot(start: string, home: string): string | undefined {
  const boundary = home ? resolve(home) : "";
  let dir = resolve(start);
  for (let level = 0; level <= REPO_DETECT_DEPTH; level++) {
    if (boundary && samePath(dir, boundary)) return undefined;
    if (existsSync(join(dir, ".git"))) return dir;
    const parent = dirname(dir);
    if (parent === dir) return undefined;
    dir = parent;
  }
  return undefined;
}

interface ClientConfigPlan {
  path: string;
  /** The server entry as written (for the report). */
  entry: Record<string, unknown>;
  /** Full file body to write. */
  body: string;
}

async function planClientConfig(
  client: SetupClientSpec,
  project: string,
  npmPin: string,
  placement: SetupPlacement,
): Promise<ClientConfigPlan> {
  const path = join(placement.workspace, ...client.configPath.split("/"));
  const launch = launchFields(project, npmPin, placement);

  if (client.format === "codex") {
    const codexEntry = {
      command: launch.command,
      args: launch.args,
      env: launch.env,
      removeEnv: launch.env[PROJECT_PATH_ENV_VAR] === undefined ? [PROJECT_PATH_ENV_VAR] : [],
    };
    const raw = (await readTextIfExists(path)) ?? "";
    let body: string;
    try {
      body = mergeCodexServer(raw, SERVER_KEY, codexEntry);
    } catch (error) {
      if (!(error instanceof CodexTomlError)) throw error;
      throw new SetupError(
        error.code,
        `${path}: ${error.message} Add this entry by hand:\n${renderCodexServer(SERVER_KEY, codexEntry)}`,
        1,
      );
    }
    return {
      path,
      entry: { enabled: true, command: launch.command, args: launch.args, env: launch.env },
      body,
    };
  }

  const raw = await readTextIfExists(path);
  let config: Record<string, unknown> = {};
  if (raw !== undefined) {
    try {
      config = parseJsonObject(raw);
    } catch (error) {
      if (isJsonc(raw)) {
        const snippet = {};
        mergeClientConfig(snippet, client, launch);
        throw new SetupError(
          "jsonc_config",
          `${path} has comments or trailing commas. setup rewrites the file as plain JSON and would drop them, so it leaves the file unchanged. Add this entry by hand:\n${JSON.stringify(snippet, null, 2)}`,
          1,
        );
      }
      throw new SetupError(
        "invalid_json",
        `${path} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`,
        1,
      );
    }
  }
  const entry = mergeClientConfig(config, client, launch);
  return { path, entry, body: `${JSON.stringify(config, null, 2)}\n` };
}

/**
 * Merge the `unity-open-mcp` entry into an existing client config, preserving
 * sibling servers and any extra keys the user added.
 *
 * A portable re-run REPLACES the absolute `UNITY_PROJECT_PATH` left by an
 * earlier absolute run (and vice versa), because leaving a stale machine path
 * behind would silently keep winning over `--project-from-cwd`.
 */
function mergeClientConfig(
  config: Record<string, unknown>,
  client: SetupClientSpec,
  launch: LaunchFields,
): Record<string, unknown> {
  if (client.format === "opencode") {
    const mcp = ensureObject(config, "mcp");
    const previous = isRecord(mcp[SERVER_KEY]) ? mcp[SERVER_KEY] : {};
    const previousEnvironment = isRecord(previous.environment)
      ? previous.environment
      : {};
    const entry = {
      ...previous,
      type: "local",
      command: [launch.command, ...launch.args],
      enabled: true,
      environment: mergeProjectEnv(previousEnvironment, launch.env),
    };
    mcp[SERVER_KEY] = entry;
    return entry;
  }

  // VS Code (and Visual Studio) read `servers`, ZCode `mcp.servers`; both
  // carry an explicit transport.
  const typed = client.format === "vscode" || client.format === "zcode";
  const servers =
    client.format === "zcode"
      ? ensureObject(ensureObject(config, "mcp"), "servers")
      : ensureObject(config, client.format === "vscode" ? "servers" : "mcpServers");
  const previous = isRecord(servers[SERVER_KEY]) ? servers[SERVER_KEY] : {};
  const previousEnv = isRecord(previous.env) ? previous.env : {};
  const entry = {
    ...(typed ? { type: "stdio" } : {}),
    ...previous,
    command: launch.command,
    args: launch.args,
    env: mergeProjectEnv(previousEnv, launch.env),
  };
  servers[SERVER_KEY] = entry;
  return entry;
}

interface LaunchFields {
  command: string;
  args: string[];
  env: Record<string, string>;
}

function launchFields(
  project: string,
  npmPin: string,
  placement: SetupPlacement,
): LaunchFields {
  if (!placement.portable || !placement.support) {
    return {
      command: "npx",
      args: ["-y", npmPin],
      env: { [PROJECT_PATH_ENV_VAR]: project },
    };
  }
  const target: PortableTargetOptions = {
    layout: placement.layout,
    unitySubpath: placement.unitySubpath,
    npmPin,
  };
  if (placement.strategy === "wrapper") {
    return {
      command: "bash",
      args: [wrapperRelativePath(placement.layout)],
      env: {},
    };
  }
  return {
    command: "npx",
    args: portableServerArgs(placement.strategy, target),
    env: portableEnv(placement.support, target),
  };
}

/**
 * Keep every env key the user added, but let this run own
 * `UNITY_PROJECT_PATH`: set it for absolute/interpolated strategies, drop it
 * for the strategies that resolve the path at runtime.
 */
function mergeProjectEnv(
  previous: Record<string, unknown>,
  next: Record<string, string>,
): Record<string, unknown> {
  const merged: Record<string, unknown> = { ...previous, ...next };
  if (next[PROJECT_PATH_ENV_VAR] === undefined) delete merged[PROJECT_PATH_ENV_VAR];
  return merged;
}

function ensureObject(
  parent: Record<string, unknown>,
  key: string,
): Record<string, unknown> {
  if (parent[key] === undefined) parent[key] = {};
  if (!isRecord(parent[key])) {
    throw new SetupError(
      "invalid_client_config",
      `Client config key '${key}' must be a JSON object.`,
      1,
    );
  }
  return parent[key];
}

async function readJsonObject(
  path: string,
  allowMissing: boolean,
): Promise<Record<string, unknown>> {
  let raw: string;
  try {
    raw = await readFile(path, "utf8");
  } catch (error) {
    if (allowMissing && isNodeError(error) && error.code === "ENOENT") return {};
    throw ioError(`Could not read ${path}`, error);
  }
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!isRecord(parsed)) {
      throw new Error("top level must be a JSON object");
    }
    return parsed;
  } catch (error) {
    throw new SetupError(
      "invalid_json",
      `${path} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`,
      1,
    );
  }
}

async function writeJson(path: string, value: Record<string, unknown>): Promise<void> {
  try {
    await mkdir(dirname(path), { recursive: true });
    await writeFile(path, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  } catch (error) {
    throw ioError(`Could not write ${path}`, error);
  }
}

async function writeText(path: string, body: string): Promise<void> {
  try {
    await mkdir(dirname(path), { recursive: true });
    await writeFile(path, body, "utf8");
  } catch (error) {
    throw ioError(`Could not write ${path}`, error);
  }
}

async function readTextIfExists(path: string): Promise<string | undefined> {
  try {
    return await readFile(path, "utf8");
  } catch (error) {
    if (isNodeError(error) && error.code === "ENOENT") return undefined;
    throw ioError(`Could not read ${path}`, error);
  }
}

function parseJsonObject(raw: string): Record<string, unknown> {
  const parsed: unknown = JSON.parse(raw);
  if (!isRecord(parsed)) throw new Error("top level must be a JSON object");
  return parsed;
}

/**
 * True when `raw` only parses once comments and trailing commas are removed —
 * the JSONC dialect VS Code allows in `.vscode/mcp.json`.
 */
function isJsonc(raw: string): boolean {
  let out = "";
  let inString = false;
  for (let i = 0; i < raw.length; i++) {
    const ch = raw[i];
    if (inString) {
      out += ch;
      if (ch === "\\") out += raw[++i] ?? "";
      else if (ch === '"') inString = false;
    } else if (ch === '"') {
      inString = true;
      out += ch;
    } else if (raw.startsWith("//", i)) {
      while (i < raw.length && raw[i] !== "\n") i++;
      out += "\n";
    } else if (raw.startsWith("/*", i)) {
      const close = raw.indexOf("*/", i + 2);
      i = close < 0 ? raw.length : close + 1;
    } else {
      out += ch;
    }
  }
  try {
    parseJsonObject(out.replace(/,(\s*[}\]])/g, "$1"));
    return true;
  } catch {
    return false;
  }
}

/** Path equality after resolution; case folds on Windows and macOS. */
function samePath(a: string, b: string): boolean {
  const norm = (p: string) => {
    const r = resolve(p);
    return process.platform === "win32" || process.platform === "darwin" ? r.toLowerCase() : r;
  };
  return norm(a) === norm(b);
}

/** Shell-quote an argument that contains whitespace. */
function quoteArg(value: string): string {
  return /\s/.test(value) ? `"${value}"` : value;
}

function resolveBundledSkillPath(): string | null {
  const moduleDir = dirname(fileURLToPath(import.meta.url));
  // Published package: dist/cli/setup-command.js -> dist/skill/SKILL.md.
  const packaged = join(moduleDir, "..", "skill", "SKILL.md");
  // Source-checkout fallback supports direct development invocations.
  return resolveTemplateSkillPath() ?? packaged;
}

function formatSetupReport(report: SetupReport): string {
  const mode = report.dryRun ? "DRY RUN — no files changed" : "Setup complete";
  const skill = report.skill.skipped
    ? "skipped (--skip-skill)"
    : `${report.skill.path} (${report.skill.bytes} bytes, ${report.skill.lines} lines)`;
  const lines = [
    mode,
    `VERSION: ${report.version}`,
    `Project: ${report.project} (from ${report.projectSource})`,
    `Client: ${report.client}`,
    `UPM bridge: ${report.manifest.dependencies[BRIDGE_PACKAGE]}`,
    `UPM verify: ${report.manifest.dependencies[VERIFY_PACKAGE]}`,
    `MCP config: ${report.mcpConfig.path}`,
    `Skill: ${skill}`,
    "Domain packages: not installed",
  ];
  if (report.layout === "monorepo" || report.portable) {
    lines.push(
      `Workspace: ${report.workspace}`,
      `Layout: ${report.layout}${report.unitySubpath ? ` (Unity at ${report.unitySubpath}/)` : ""}`,
    );
  }
  lines.push(
    `Config: ${report.portable ? `portable — safe to commit (${report.configStrategy})` : "absolute path — this machine only"}`,
  );
  if (report.wrapper.path) lines.push(`Wrapper: ${report.wrapper.path}`);
  for (const warning of report.warnings) lines.push(`Warning: ${warning}`);
  lines.push("", "USER ACTION:", ...report.userAction.map((action, i) => `${i + 1}. ${action}`));
  return lines.join("\n");
}

function countLines(bytes: Buffer): number {
  if (bytes.byteLength === 0) return 0;
  const text = bytes.toString("utf8");
  const newlines = (text.match(/\n/g) ?? []).length;
  return text.endsWith("\n") ? newlines : newlines + 1;
}

function isNodeError(error: unknown): error is NodeJS.ErrnoException {
  return error instanceof Error && "code" in error;
}

function ioError(context: string, error: unknown): SetupError {
  const detail = error instanceof Error ? error.message : String(error);
  return new SetupError("io_error", `${context}: ${detail}`, 1);
}
