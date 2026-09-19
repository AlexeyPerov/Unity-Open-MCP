import { readFile, stat, mkdir, writeFile } from "node:fs/promises";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import {
  clientSkillRelativePath,
  knownClientKeys,
  resolveTemplateSkillPath,
} from "../skill/client-paths.js";

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

    const configPath = configPathFor(project, client);
    const config = await readJsonObject(configPath, true);
    const entry = mergeClientConfig(config, client, project, pins.npm);

    let skillBytes: Buffer | null = null;
    let skillPath: string | null = null;
    if (!opts.skipSkill) {
      skillPath = join(project, clientSkillRelativePath(client));
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
        } catch (error) {
          throw ioError(`Could not write skill to ${skillPath}`, error);
        }
      }
    }

    const report: SetupReport = {
      version: opts.version,
      project,
      client,
      dryRun: opts.dryRun,
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
      warnings: [],
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
  for (const required of ["Assets", "Packages", "ProjectSettings"]) {
    try {
      if (!(await stat(join(project, required))).isDirectory()) throw new Error();
    } catch {
      throw new SetupError(
        "not_unity_project",
        `${project} is not a Unity project root: missing ${required}/.`,
        2,
      );
    }
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

function configPathFor(project: string, client: SetupConfigClient): string {
  switch (client) {
    case "cursor":
      return join(project, ".cursor", "mcp.json");
    case "claude":
      return join(project, ".mcp.json");
    case "opencode":
      return join(project, "opencode.json");
    case "agents":
      return join(project, ".mcp.json");
  }
}

function mergeClientConfig(
  config: Record<string, unknown>,
  client: SetupConfigClient,
  project: string,
  npmPin: string,
): Record<string, unknown> {
  if (client === "opencode") {
    const mcp = ensureObject(config, "mcp");
    const previous = isRecord(mcp[SERVER_KEY]) ? mcp[SERVER_KEY] : {};
    const previousEnvironment = isRecord(previous.environment)
      ? previous.environment
      : {};
    const entry = {
      ...previous,
      type: "local",
      command: ["npx", "-y", npmPin],
      enabled: true,
      environment: {
        ...previousEnvironment,
        UNITY_PROJECT_PATH: project,
      },
    };
    mcp[SERVER_KEY] = entry;
    return entry;
  }

  const servers = ensureObject(config, "mcpServers");
  const previous = isRecord(servers[SERVER_KEY]) ? servers[SERVER_KEY] : {};
  const previousEnv = isRecord(previous.env) ? previous.env : {};
  const entry = {
    ...previous,
    command: "npx",
    args: ["-y", npmPin],
    env: {
      ...previousEnv,
      UNITY_PROJECT_PATH: project,
    },
  };
  servers[SERVER_KEY] = entry;
  return entry;
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
  return [
    mode,
    `VERSION: ${report.version}`,
    `Project: ${report.project}`,
    `Client: ${report.client}`,
    `UPM bridge: ${report.manifest.dependencies[BRIDGE_PACKAGE]}`,
    `UPM verify: ${report.manifest.dependencies[VERIFY_PACKAGE]}`,
    `MCP config: ${report.mcpConfig.path}`,
    `Skill: ${skill}`,
    "Domain packages: not installed",
    "",
    "USER ACTION:",
    `1. ${report.userAction[0]}`,
    `2. ${report.userAction[1]}`,
  ].join("\n");
}

function countLines(bytes: Buffer): number {
  if (bytes.byteLength === 0) return 0;
  const text = bytes.toString("utf8");
  const newlines = (text.match(/\n/g) ?? []).length;
  return text.endsWith("\n") ? newlines : newlines + 1;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isNodeError(error: unknown): error is NodeJS.ErrnoException {
  return error instanceof Error && "code" in error;
}

function ioError(context: string, error: unknown): SetupError {
  const detail = error instanceof Error ? error.message : String(error);
  return new SetupError("io_error", `${context}: ${detail}`, 1);
}
