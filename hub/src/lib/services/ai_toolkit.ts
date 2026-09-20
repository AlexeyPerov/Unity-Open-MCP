/**
 * M4 — AI Setup wizard environment-variable contract.
 *
 * The wizard Step 4 writes a `unity-open-mcp` MCP server entry to one
 * of three client config shapes. The env-var contract is the same
 * across all three (see `specs/packages/mcp-server.md` §Environment
 * variables), but the surrounding envelope differs:
 *
 *  | Client       | Envelope key | Command shape              | Env field       |
 *  |--------------|--------------|----------------------------|-----------------|
 *  | Cursor       | mcpServers   | `command` + `args: [path]` | `env`           |
 *  | Claude Desktop | mcpServers | `command` + `args: [path]` | `env`           |
 *  | OpenCode     | mcp          | `command: [node, path]`    | `environment`   |
 *
 * `UNITY_PROJECT_PATH` and `UNITY_OPEN_MCP_BRIDGE_PORT` are always
 * present. The bridge port is the per-project hash
 * (`20000 + sha256(projectPath) % 10000`, shared with the bridge and
 * the MCP server) — the wizard resolves it via the `resolve_bridge_port`
 * Tauri command before building the entry, so the on-disk writer (Rust)
 * and the preview (TS) always agree. `UNITY_PATH` is included for
 * batch-capable flows (M5+) — the wizard exposes a Step 4 advanced
 * toggle to opt in, defaulting to off in M4.
 *
 * Pure functions only — no I/O. Callers serialize the result of
 * `buildXxxMcpEntry` directly into the client config file.
 */

export const MCP_SERVER_KEY = "unity-open-mcp";

/** The full set of env-var inputs the wizard may pass. `unityPath`
 *  is optional; it is added to the entry only when the user opts
 *  in to batch support (M5+). */
export interface McpEnvInputs {
  /** Absolute path to the Unity project being onboarded. Required. */
  unityProjectPath: string;
  /** Resolved bridge HTTP port. The wizard resolves this via the
   *  `resolve_bridge_port` Tauri command (per-project hash, or an
   *  explicit override from the Step 4 port field) before building
   *  the entry, so the preview and the on-disk writer agree. */
  bridgePort: string;
  /** Absolute path to the Unity Editor executable. Optional; only
   *  emitted when the user opts in to batch routing (M5+). */
  unityPath?: string;
}

/** Resolved env-var map. Always includes `UNITY_PROJECT_PATH` and
 *  `UNITY_OPEN_MCP_BRIDGE_PORT`; `UNITY_PATH` is included only when
 *  `inputs.unityPath` is a non-empty string. */
export type McpEnv = Record<string, string>;

/** Compute the canonical env-var map for a generated MCP entry.
 *  Use this for both Cursor/Claude (`env`) and OpenCode
 *  (`environment`); the key names are the spec contract and never
 *  change between clients. The caller must pass the resolved
 *  `bridgePort` (per-project hash or explicit override). */
export function buildMcpEnv(inputs: McpEnvInputs): McpEnv {
  const env: McpEnv = {
    UNITY_PROJECT_PATH: inputs.unityProjectPath,
    UNITY_OPEN_MCP_BRIDGE_PORT: inputs.bridgePort,
  };
  if (inputs.unityPath && inputs.unityPath.trim().length > 0) {
    env.UNITY_PATH = inputs.unityPath.trim();
  }
  return env;
}

/** Cursor / Claude Desktop MCP server entry. Uses the `command` +
 *  `args` form documented in `specs/architecture/mcp-tools.md`
 *  §M4. The MCP server key in the parent config is `unity-open-mcp`
 *  (see `MCP_SERVER_KEY`); the wizard merges this entry under
 *  `mcpServers[unity-open-mcp]`. */
export interface CursorMcpServerEntry {
  command: "node";
  args: [string];
  env: McpEnv;
}

export function buildCursorMcpEntry(
  mcpIndexPath: string,
  inputs: McpEnvInputs
): CursorMcpServerEntry {
  return {
    command: "node",
    args: [mcpIndexPath],
    env: buildMcpEnv(inputs),
  };
}

/** OpenCode MCP server entry. Distinct from Cursor/Claude:
 *  - root key is `mcp` (not `mcpServers`)
 *  - `command` is an array (`["node", path]`)
 *  - env field is `environment` (not `env`)
 *  - `type: "local"` and `enabled: true` are explicit
 *  See `specs/architecture/mcp-tools.md` §M4 OpenCode example. */
export interface OpenCodeMcpServerEntry {
  type: "local";
  command: [string, string];
  enabled: true;
  environment: McpEnv;
}

export function buildOpenCodeMcpEntry(
  mcpIndexPath: string,
  inputs: McpEnvInputs
): OpenCodeMcpServerEntry {
  return {
    type: "local",
    command: ["node", mcpIndexPath],
    enabled: true,
    environment: buildMcpEnv(inputs),
  };
}

/** The supported MCP client identifiers. `claude-code` is
 *  CLI-only — the wizard shows a `claude mcp add` command
 *  instead of writing a config file. `manual` / `custom` are
 *  clipboard-only. Every other id is backed by a writable config
 *  file (JSON, or TOML for `codex`). The full catalog covers the
 *  Ivan-named client surface; see `skills/client-paths.json`. */
export type McpClientId =
  | "cursor"
  | "claude-desktop"
  | "claude-code"
  | "opencode-global"
  | "opencode-project"
  | "zcode-global"
  | "zcode-project"
  | "manual"
  // --- Ivan-parity breadth ---
  | "cline"
  | "codex"
  | "gemini"
  | "github-copilot-cli"
  | "kilo-code"
  | "rider"
  | "unity-ai"
  | "vscode-copilot"
  | "vs-copilot"
  | "zoocode"
  | "antigravity"
  | "custom";

/** Per-client config-file path shape. `null` means the client is
 *  CLI-only or not backed by a writable JSON file. */
export interface McpClientConfigTarget {
  /** OS-resolved absolute path to the config file the wizard will
   *  merge into. `null` for `claude-code` (CLI only) and `manual`
   *  (copy-to-clipboard only). */
  path: string | null;
  /** Whether the target is a project-scoped file the user might
   *  want to commit to a repo. Cursor + OpenCode offer both a
   *  global and a project-scoped variant; the wizard exposes the
   *  project variant as a toggle (questions-4 Q6 = A). */
  scope: "global" | "project" | "none";
  /** JSON merge key the wizard should use to insert the entry
   *  without clobbering unrelated servers. */
  mergeKey: string;
}

/** Resolve a per-client config-file target. Path resolution is
 *  intentionally minimal here — the wizard uses the existing
 *  `~/.cursor/mcp.json` / `~/.config/opencode/opencode.json`
 *  defaults from `mcp-tools.md` §M4 and the OS-specific Claude
 *  Desktop path; the project-scoped paths are resolved relative
 *  to the project root in Step 1. This function returns the
 *  contract shape only; the wizard's Step 4 owns the platform
 *  path resolution and any user toggle for project-scoped writes.
 *
 *  The full catalog (M27 Plan 5) covers the Ivan-named client
 *  surface. Paths here mirror the Rust `resolve_target_path` so
 *  the TS preview and the on-disk writer agree. */
export function mcpClientConfigTarget(
  client: McpClientId,
  homeDir: string
): McpClientConfigTarget {
  switch (client) {
    case "cursor":
      return {
        path: `${homeDir}/.cursor/mcp.json`,
        scope: "global",
        mergeKey: "mcpServers.unity-open-mcp",
      };
    case "claude-desktop":
      return {
        path: null, // OS-specific; resolved in Step 4
        scope: "global",
        mergeKey: "mcpServers.unity-open-mcp",
      };
    case "claude-code":
      return {
        path: null,
        scope: "none",
        mergeKey: "",
      };
    case "opencode-global":
      return {
        path: `${homeDir}/.config/opencode/opencode.json`,
        scope: "global",
        mergeKey: "mcp.unity-open-mcp",
      };
    case "opencode-project":
      return {
        path: "opencode.json", // resolved relative to project in Step 4
        scope: "project",
        mergeKey: "mcp.unity-open-mcp",
      };
    case "zcode-global":
      return {
        path: `${homeDir}/.zcode/cli/config.json`,
        scope: "global",
        mergeKey: "mcp.servers.unity-open-mcp",
      };
    case "zcode-project":
      return {
        path: ".zcode/cli/config.json", // resolved relative to project in Step 4
        scope: "project",
        mergeKey: "mcp.servers.unity-open-mcp",
      };
    case "manual":
    case "custom":
      return {
        path: null,
        scope: "none",
        mergeKey: "",
      };
    case "cline":
      // VS Code globalStorage; the OS-specific path is resolved in Step 4.
      return {
        path: null,
        scope: "global",
        mergeKey: "mcpServers.unity-open-mcp",
      };
    case "codex":
      return {
        path: ".codex/config.toml",
        scope: "project",
        mergeKey: "mcp_servers.unity-open-mcp",
      };
    case "gemini":
      return {
        path: ".gemini/settings.json",
        scope: "project",
        mergeKey: "mcpServers.unity-open-mcp",
      };
    case "github-copilot-cli":
      return {
        path: ".mcp.json",
        scope: "project",
        mergeKey: "mcpServers.unity-open-mcp",
      };
    case "kilo-code":
      return {
        path: ".kilocode/mcp.json",
        scope: "project",
        mergeKey: "mcpServers.unity-open-mcp",
      };
    case "rider":
      return {
        path: ".junie/mcp/mcp.json",
        scope: "project",
        mergeKey: "mcpServers.unity-open-mcp",
      };
    case "unity-ai":
      return {
        path: "UserSettings/mcp.json",
        scope: "project",
        mergeKey: "mcpServers.unity-open-mcp",
      };
    case "vscode-copilot":
      return {
        path: ".vscode/mcp.json",
        scope: "project",
        mergeKey: "servers.unity-open-mcp",
      };
    case "vs-copilot":
      return {
        path: ".vs/mcp.json",
        scope: "project",
        mergeKey: "servers.unity-open-mcp",
      };
    case "zoocode":
      return {
        path: ".roo/mcp.json",
        scope: "project",
        mergeKey: "mcpServers.unity-open-mcp",
      };
    case "antigravity":
      return {
        path: `${homeDir}/.gemini/antigravity/mcp_config.json`,
        scope: "global",
        mergeKey: "mcpServers.unity-open-mcp",
      };
  }
}

// --- Portable (committable) config ----------------------------------------
//
// A monorepo team wants ONE MCP client config in the repository, with no
// per-developer absolute path in it. Two shapes make that possible; which one
// a client accepts is fixed per client and mirrored in the Rust writer
// (`hub/src-tauri/src/config/mcp_config.rs`) and the `setup` CLI catalog:
//
//   interpolation  the client expands `${workspaceFolder}` inside env values
//   args           the server resolves the path from its own spawn cwd
//                  (`--project-from-cwd [--unity-subpath Client]`)
//   absolute       no portable form: a global config, or a client that needs
//                  the committed wrapper script the `setup` CLI writes
//
// These helpers are the preview side; the Rust writer owns the on-disk merge.

export type PortableStrategy = "interpolation" | "args" | "absolute";

/** Workspace variable the interpolating clients expand. */
export const WORKSPACE_FOLDER_VAR = "${workspaceFolder}";

/**
 * Which portable form a client accepts. `scope` matters: the same client is
 * `absolute` for its global config (no workspace to resolve against) and
 * portable for a project-scoped one.
 */
export function portableStrategy(
  client: McpClientId,
  scope: "global" | "project" | "none"
): PortableStrategy {
  if (scope === "global") return "absolute";
  switch (client) {
    case "cursor":
    case "vscode-copilot":
    case "vs-copilot":
      return "interpolation";
    case "claude-code":
    case "opencode-project":
    case "gemini":
    case "github-copilot-cli":
    case "kilo-code":
    case "rider":
    case "unity-ai":
    case "zoocode":
    case "manual":
    case "custom":
      return "args";
    default:
      return "absolute";
  }
}

/**
 * `${workspaceFolder}` or `${workspaceFolder}/<subpath>` for an
 * interpolation client. `unitySubpath` is the Unity root relative to the
 * workspace, POSIX-separated; empty when they are the same folder.
 */
export function workspaceFolderPath(unitySubpath: string): string {
  const trimmed = unitySubpath.replace(/^\/+|\/+$/g, "");
  return trimmed ? `${WORKSPACE_FOLDER_VAR}/${trimmed}` : WORKSPACE_FOLDER_VAR;
}

/**
 * Extra server arguments an `args`-strategy entry needs. Empty for every
 * other strategy — those carry the path in `env` (or not at all).
 */
export function portableServerArgs(
  strategy: PortableStrategy,
  unitySubpath: string
): string[] {
  if (strategy !== "args") return [];
  const trimmed = unitySubpath.replace(/^\/+|\/+$/g, "");
  return trimmed
    ? ["--project-from-cwd", "--unity-subpath", trimmed]
    : ["--project-from-cwd"];
}

/**
 * Env map for a portable entry. An interpolating client carries the
 * interpolated `UNITY_PROJECT_PATH`; everything else carries nothing.
 *
 * `UNITY_OPEN_MCP_BRIDGE_PORT` is deliberately absent: the port is a hash of
 * the ABSOLUTE project path, so a committed value would be wrong on every
 * other machine. Instance discovery re-derives it there.
 */
export function buildPortableMcpEnv(
  strategy: PortableStrategy,
  unitySubpath: string
): McpEnv {
  return strategy === "interpolation"
    ? { UNITY_PROJECT_PATH: workspaceFolderPath(unitySubpath) }
    : {};
}
