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
 * present (the wizard pre-fills `UNITY_PROJECT_PATH` from the Step 1
 * project path). `UNITY_PATH` is included for batch-capable flows
 * (M5+) — the wizard exposes a Step 4 advanced toggle to opt in,
 * defaulting to off in M4.
 *
 * Pure functions only — no I/O. Callers serialize the result of
 * `buildXxxMcpEntry` directly into the client config file.
 */

export const MCP_SERVER_KEY = "unity-open-mcp";
export const DEFAULT_BRIDGE_PORT = "19120";

/** The full set of env-var inputs the wizard may pass. `unityPath`
 *  is optional; it is added to the entry only when the user opts
 *  in to batch support (M5+). */
export interface McpEnvInputs {
  /** Absolute path to the Unity project being onboarded. Required. */
  unityProjectPath: string;
  /** Bridge HTTP port. Defaults to `DEFAULT_BRIDGE_PORT` (`"19120"`). */
  bridgePort?: string;
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
 *  change between clients. */
export function buildMcpEnv(inputs: McpEnvInputs): McpEnv {
  const env: McpEnv = {
    UNITY_PROJECT_PATH: inputs.unityProjectPath,
    UNITY_OPEN_MCP_BRIDGE_PORT: inputs.bridgePort ?? DEFAULT_BRIDGE_PORT,
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
 *  CLI-only in M4 — the wizard shows a `claude mcp add` command
 *  instead of writing a config file (questions-4 Q9 = A). */
export type McpClientId =
  | "cursor"
  | "claude-desktop"
  | "claude-code"
  | "opencode-global"
  | "opencode-project"
  | "zcode-global"
  | "zcode-project"
  | "manual";

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
 *  path resolution and any user toggle for project-scoped writes. */
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
      return {
        path: null,
        scope: "none",
        mergeKey: "",
      };
  }
}
