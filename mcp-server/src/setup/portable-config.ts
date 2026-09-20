// Portable (committable) MCP client configuration.
//
// One catalog describing HOW each AI client can be pointed at a Unity project
// without a per-developer absolute path, plus the builders that turn that
// choice into config fields. Shared by the `setup` CLI writer, the docs
// client matrix, and the Hub / bridge configure panels so the three never
// drift into three different snippets.
//
// Four strategies, in order of preference:
//
//   interpolation  the client expands a workspace variable inside the config
//                  (Cursor, VS Code): UNITY_PROJECT_PATH = "${workspaceFolder}/Client".
//   args           the server derives the path from its own spawn cwd:
//                  `--project-from-cwd [--unity-subpath Client]`. Requires the
//                  client to spawn the server with cwd = workspace root.
//   wrapper        a committed shell script resolves the path from its own
//                  location and execs the server. For clients that support
//                  neither of the above (Codex, ZCode).
//   absolute       global/user-level config outside any workspace (Claude
//                  Desktop, Cline, Antigravity): no portable form exists.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { PROJECT_PATH_ENV_VAR } from "../constants.js";

export type ConfigStrategy = "interpolation" | "args" | "wrapper" | "absolute";

/** Unity project root == workspace root, or a subfolder of it. */
export type ProjectLayout = "unity-root" | "monorepo";

export interface PortableClientSupport {
  /** Catalog id, matching the client names used in the setup docs. */
  id: string;
  label: string;
  /** Config file, relative to the workspace root; empty for global/CLI clients. */
  configPath: string;
  strategy: ConfigStrategy;
  /** Workspace variable this client expands, for `interpolation` clients. */
  workspaceVar?: string;
  note: string;
}

const WORKSPACE_FOLDER = "${workspaceFolder}";

/**
 * Client capability matrix. `docs/setup/portable-config.md` publishes the same
 * table; a unit test keeps every setup-writer client present here.
 */
export const PORTABLE_CLIENT_MATRIX: readonly PortableClientSupport[] = [
  {
    id: "cursor",
    label: "Cursor",
    configPath: ".cursor/mcp.json",
    strategy: "interpolation",
    workspaceVar: WORKSPACE_FOLDER,
    note: "Expands ${workspaceFolder} inside env values.",
  },
  {
    id: "claude-code",
    label: "Claude Code",
    configPath: ".mcp.json",
    strategy: "args",
    note: "Spawns MCP servers with the workspace root as the working directory.",
  },
  {
    id: "vscode-copilot",
    label: "VS Code Copilot",
    configPath: ".vscode/mcp.json",
    strategy: "interpolation",
    workspaceVar: WORKSPACE_FOLDER,
    note: "Expands ${workspaceFolder} inside env values.",
  },
  {
    id: "vs-copilot",
    label: "Visual Studio Copilot",
    configPath: ".vs/mcp.json",
    strategy: "interpolation",
    workspaceVar: WORKSPACE_FOLDER,
    note: "Expands ${workspaceFolder} inside env values.",
  },
  {
    id: "opencode",
    label: "OpenCode",
    configPath: "opencode.json",
    strategy: "args",
    note: "Project-local config; the server resolves the path from its spawn cwd.",
  },
  {
    id: "agents",
    label: "Generic agent (.mcp.json)",
    configPath: ".mcp.json",
    strategy: "args",
    note: "Project-local config; the server resolves the path from its spawn cwd.",
  },
  {
    id: "github-copilot-cli",
    label: "GitHub Copilot CLI",
    configPath: ".mcp.json",
    strategy: "args",
    note: "Project-local config; the server resolves the path from its spawn cwd.",
  },
  {
    id: "gemini",
    label: "Gemini CLI",
    configPath: ".gemini/settings.json",
    strategy: "args",
    note: "Project-local config; the server resolves the path from its spawn cwd.",
  },
  {
    id: "kilo-code",
    label: "Kilo Code",
    configPath: ".kilocode/mcp.json",
    strategy: "args",
    note: "Project-local config; the server resolves the path from its spawn cwd.",
  },
  {
    id: "rider",
    label: "Rider (Junie)",
    configPath: ".junie/mcp/mcp.json",
    strategy: "args",
    note: "Project-local config; the server resolves the path from its spawn cwd.",
  },
  {
    id: "zoocode",
    label: "ZooCode",
    configPath: ".roo/mcp.json",
    strategy: "args",
    note: "Project-local config; the server resolves the path from its spawn cwd.",
  },
  {
    id: "unity-ai",
    label: "Unity AI",
    configPath: "UserSettings/mcp.json",
    strategy: "args",
    note: "Config lives inside the Unity project, so the Unity root is the only layout.",
  },
  {
    id: "codex",
    label: "Codex",
    configPath: ".codex/config.toml",
    strategy: "wrapper",
    note: "No workspace interpolation in TOML env tables; use the committed wrapper.",
  },
  {
    id: "zcode",
    label: "ZCode",
    configPath: ".zcode/cli/config.json",
    strategy: "wrapper",
    note: "No workspace interpolation; use the committed wrapper.",
  },
  {
    id: "claude-desktop",
    label: "Claude Desktop",
    configPath: "",
    strategy: "absolute",
    note: "Global config with no workspace notion — absolute path only.",
  },
  {
    id: "cline",
    label: "Cline",
    configPath: "",
    strategy: "absolute",
    note: "Global MCP settings — absolute path only.",
  },
  {
    id: "antigravity",
    label: "Antigravity",
    configPath: "",
    strategy: "absolute",
    note: "Global MCP config — absolute path only.",
  },
];

/** `setup --client <id>` → catalog id. */
const SETUP_CLIENT_TO_CATALOG: Record<string, string> = {
  cursor: "cursor",
  claude: "claude-code",
  opencode: "opencode",
  agents: "agents",
};

export function catalogIdForSetupClient(setupClient: string): string {
  return SETUP_CLIENT_TO_CATALOG[setupClient] ?? setupClient;
}

export function portableSupportFor(catalogId: string): PortableClientSupport | undefined {
  return PORTABLE_CLIENT_MATRIX.find((client) => client.id === catalogId);
}

export interface PortableTargetOptions {
  layout: ProjectLayout;
  /** Unity project folder relative to the workspace root; "" for unity-root. */
  unitySubpath: string;
  /** `unity-open-mcp@<version>` pin for the npx invocation. */
  npmPin: string;
}

/**
 * Server arguments for a portable spawn. `interpolation` clients keep the
 * plain `npx -y <pin>` form and carry the path in `env`; `args` clients get
 * the resolution flags instead.
 */
export function portableServerArgs(
  strategy: ConfigStrategy,
  options: PortableTargetOptions,
): string[] {
  const args = ["-y", options.npmPin];
  if (strategy !== "args") return args;
  args.push("--project-from-cwd");
  if (options.unitySubpath) args.push("--unity-subpath", options.unitySubpath);
  return args;
}

/**
 * The `UNITY_PROJECT_PATH` value a portable config should carry, or
 * `undefined` when the strategy does not use the env var at all (`args`
 * clients resolve from cwd; the wrapper exports the variable itself).
 */
export function portableProjectPathValue(
  support: PortableClientSupport,
  options: PortableTargetOptions,
): string | undefined {
  if (support.strategy !== "interpolation") return undefined;
  const root = support.workspaceVar ?? WORKSPACE_FOLDER;
  return options.unitySubpath ? `${root}/${toPosix(options.unitySubpath)}` : root;
}

/** Env map for a portable entry: `{}` when the path comes from args/wrapper. */
export function portableEnv(
  support: PortableClientSupport,
  options: PortableTargetOptions,
): Record<string, string> {
  const value = portableProjectPathValue(support, options);
  return value ? { [PROJECT_PATH_ENV_VAR]: value } : {};
}

/** Where a wrapper script belongs, relative to the workspace root. */
export function wrapperRelativePath(layout: ProjectLayout): string {
  return layout === "monorepo"
    ? "scripts/mcp/unity-open-mcp.sh"
    : ".unity-open-mcp/mcp-wrapper.sh";
}

export interface WrapperOptions {
  version: string;
  layout: ProjectLayout;
  unitySubpath: string;
}

/**
 * Render the shipped wrapper template with this package's version pin and the
 * project's Unity subfolder. The `..` hops back to the workspace root are
 * derived from {@link wrapperRelativePath}, so the two can never disagree.
 */
export function renderWrapperScript(options: WrapperOptions): string {
  const depth = wrapperRelativePath(options.layout).split("/").length - 1;
  const upward = depth > 0 ? Array.from({ length: depth }, () => "..").join("/") : ".";
  return readWrapperTemplate()
    .replace("__WORKSPACE_FROM_SCRIPT__", upward)
    .replace("__UNITY_SUBPATH__", toPosix(options.unitySubpath))
    .replace("__UNITY_OPEN_MCP_VERSION__", options.version);
}

/**
 * Locate the shipped `mcp-wrapper.sh`. Published builds copy it to
 * `dist/templates/`; a source checkout falls back to `mcp-server/templates/`.
 */
export function resolveWrapperTemplatePath(): string {
  const moduleDir = dirname(fileURLToPath(import.meta.url));
  const candidates = [
    // dist/setup/portable-config.js -> dist/templates/mcp-wrapper.sh
    join(moduleDir, "..", "templates", "mcp-wrapper.sh"),
    // src/setup/portable-config.ts -> mcp-server/templates/mcp-wrapper.sh
    join(moduleDir, "..", "..", "templates", "mcp-wrapper.sh"),
    // dist-test/setup/portable-config.js -> mcp-server/templates/mcp-wrapper.sh
    join(moduleDir, "..", "..", "..", "templates", "mcp-wrapper.sh"),
  ];
  for (const candidate of candidates) {
    try {
      readFileSync(candidate, "utf8");
      return candidate;
    } catch {
      // try the next location
    }
  }
  throw new Error(
    "The packaged MCP wrapper template could not be located. Reinstall unity-open-mcp and retry.",
  );
}

function readWrapperTemplate(): string {
  return readFileSync(resolveWrapperTemplatePath(), "utf8");
}

function toPosix(relative: string): string {
  return relative.split("\\").join("/");
}
