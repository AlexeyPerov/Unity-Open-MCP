import { copySkillReferences } from "../skill/copy-references.js";
import { readFile, mkdir, writeFile } from "node:fs/promises";
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
import {
  catalogIdForSetupClient,
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
const CONFIG_CLIENTS = ["cursor", "claude", "opencode", "agents"] as const;

type SetupConfigClient = (typeof CONFIG_CLIENTS)[number];

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
    const project = await validateProject(opts.projectPath);
    const client = validateClient(opts.client);
    const pins = packagePins(opts.version);
    const placement = resolvePlacement(project, client, opts);
    const warnings = [...placement.warnings];

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
    const configPath = configPathFor(placement.workspace, client);
    const config = await readJsonObject(configPath, true);
    const entry = mergeClientConfig(config, client, project, pins.npm, placement);

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
      skillPath = join(placement.workspace, clientSkillRelativePath(client));
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
      await writeJson(configPath, config);
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
      client,
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

async function validateProject(input: string | undefined): Promise<string> {
  if (!input) {
    throw new SetupError(
      "missing_project",
      "setup requires --project <absolute Unity project path>.",
      2,
    );
  }
  if (!isAbsolute(input)) {
    throw new SetupError(
      "project_not_absolute",
      `--project must be absolute (received '${input}').`,
      2,
    );
  }
  const project = resolve(input);
  const validation = validateUnityProjectRoot(project);
  if (!validation.valid) {
    throw new SetupError(
      "not_unity_project",
      `${project} is not a Unity project root: missing ${validation.missing.map((m) => `${m}/`).join(", ")}.`,
      2,
    );
  }
  return project;
}

function validateClient(input: string | undefined): SetupConfigClient {
  const known = knownClientKeys();
  if (!input) {
    throw new SetupError(
      "missing_client",
      `setup requires --client <id>. Known ids: ${known.join(", ")}.`,
      2,
    );
  }
  if (!known.includes(input)) {
    throw new SetupError(
      "unknown_client",
      `Unknown client '${input}'. Known ids: ${known.join(", ")}.`,
      2,
    );
  }
  if (!(CONFIG_CLIENTS as readonly string[]).includes(input)) {
    throw new SetupError(
      "unsupported_client_config",
      `Client '${input}' has a skill path but no setup config writer. Configure MCP manually; skill-only is not enough. Setup config writers: ${CONFIG_CLIENTS.join(", ")}.`,
      2,
    );
  }
  return input as SetupConfigClient;
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
  client: SetupConfigClient,
  opts: SetupCommandOptions,
): SetupPlacement {
  const warnings: string[] = [];
  const requestedSubpath = normalizeSubpath(opts.unitySubpath);

  let workspace: string;
  if (opts.workspacePath) {
    if (!isAbsolute(opts.workspacePath)) {
      throw new SetupError(
        "workspace_not_absolute",
        `--workspace must be absolute (received '${opts.workspacePath}').`,
        2,
      );
    }
    workspace = resolve(opts.workspacePath);
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

  const support = portableSupportFor(catalogIdForSetupClient(client));
  // Portable by default exactly where the absolute path is the real problem:
  // a monorepo whose config file is not inside the Unity project.
  let portable = opts.portable ?? layout === "monorepo";
  if (portable && support?.strategy === "absolute") {
    warnings.push(
      `Client '${client}' has no portable configuration form; writing the absolute path.`,
    );
    portable = false;
  }
  if (portable && !support) {
    warnings.push(
      `Client '${client}' is not in the portable-config catalog; writing the absolute path.`,
    );
    portable = false;
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

function configPathFor(workspace: string, client: SetupConfigClient): string {
  switch (client) {
    case "cursor":
      return join(workspace, ".cursor", "mcp.json");
    case "claude":
      return join(workspace, ".mcp.json");
    case "opencode":
      return join(workspace, "opencode.json");
    case "agents":
      return join(workspace, ".mcp.json");
  }
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
  client: SetupConfigClient,
  project: string,
  npmPin: string,
  placement: SetupPlacement,
): Record<string, unknown> {
  const launch = launchFields(project, npmPin, placement);

  if (client === "opencode") {
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

  const servers = ensureObject(config, "mcpServers");
  const previous = isRecord(servers[SERVER_KEY]) ? servers[SERVER_KEY] : {};
  const previousEnv = isRecord(previous.env) ? previous.env : {};
  const entry = {
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
    `Project: ${report.project}`,
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
      `Config: ${report.portable ? `portable — safe to commit (${report.configStrategy})` : "absolute path"}`,
    );
  }
  if (report.wrapper.path) lines.push(`Wrapper: ${report.wrapper.path}`);
  for (const warning of report.warnings) lines.push(`Warning: ${warning}`);
  lines.push(
    "",
    "USER ACTION:",
    `1. ${report.userAction[0]}`,
    `2. ${report.userAction[1]}`,
  );
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
