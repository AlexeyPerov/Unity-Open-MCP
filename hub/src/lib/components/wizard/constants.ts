import type { McpClientId } from "../../services/ai_toolkit.ts";
import type { McpClientWire } from "../../services/config.ts";

/**
 * Wizard step identifiers, in navigation order. Step IDs (`step0`…`done`) are
 * part of the persisted draft + preset contract — do not rename them.
 */
export type StepId =
  | "step0"
  | "step1"
  | "step2"
  | "step3"
  | "step4"
  | "step4b"
  | "step5"
  | "done";

/** Ordered list of wizard steps. */
export const STEP_ORDER: StepId[] = [
  "step0",
  "step1",
  "step2",
  "step3",
  "step4",
  "step4b",
  "step5",
  "done",
];

/** User-visible step titles. Plan 2 renamed step1 from "Project detection" to
 *  "Preflight" so the title reflects the ownership model (environment gate, not
 *  a fixable diagnostic panel). */
export const STEP_TITLES: Record<StepId, string> = {
  step0: "Setup preset",
  step1: "Preflight",
  step2: "MCP server source",
  step3: "Unity packages",
  step4: "Configure AI client",
  step4b: "Agent skill (optional)",
  step5: "Launch Unity and verify bridge",
  done: "Setup complete",
};

/** Index of a step within {@link STEP_ORDER}, or -1 when unknown. */
export function stepIndex(id: StepId): number {
  return STEP_ORDER.indexOf(id);
}

/** Title for a step id. Falls back to the id itself. */
export function stepLabel(id: StepId): string {
  return STEP_TITLES[id] ?? id;
}

/** A single MCP client option in the grouped picker. */
export interface McpClientOption {
  id: McpClientId;
  label: string;
  kind: "file" | "cli" | "clipboard";
  /** Category for the grouped picker: IDE-backed agents, CLI agents, or the
   *  manual/clipboard fallbacks. */
  category: "ide" | "cli" | "manual";
  /** Tooltip describing the config format family + popular agents that share
   *  it, so users picking one client understand the format they are committing
   *  to. */
  sharedWith?: string;
  /** Plan 3 — when `true`, the client surfaces in the short Popular list on
   *  the client step's first viewport. The full catalog stays reachable behind
   *  "Show all clients". */
  popular?: boolean;
}

/** Catalog of MCP client options rendered by the Step 4 picker. */
export const MCP_CLIENT_OPTIONS: McpClientOption[] = [
  // --- IDE / editor agents (file-backed) ---
  {
    id: "cursor",
    label: "Cursor",
    kind: "file",
    category: "ide",
    popular: true,
    sharedWith:
      "Format: mcpServers JSON at ~/.cursor/mcp.json (global) or .cursor/mcp.json (project).",
  },
  {
    id: "claude-desktop",
    label: "Claude Desktop",
    kind: "file",
    category: "ide",
    popular: true,
    sharedWith:
      "Format: mcpServers JSON (Claude Desktop config). Same envelope as Cursor; shared by Claude Desktop and Cursor.",
  },
  {
    id: "cline",
    label: "Cline (VS Code)",
    kind: "file",
    category: "ide",
    sharedWith:
      "Format: mcpServers JSON in VS Code globalStorage (cline_mcp_settings.json). Skill installs to .cline/skills/.",
  },
  {
    id: "vscode-copilot",
    label: "VS Code Copilot",
    kind: "file",
    category: "ide",
    popular: true,
    sharedWith:
      "Format: servers JSON at .vscode/mcp.json (project). Uses the `servers` key, not `mcpServers`.",
  },
  {
    id: "vs-copilot",
    label: "Visual Studio Copilot",
    kind: "file",
    category: "ide",
    sharedWith: "Format: servers JSON at .vs/mcp.json (project).",
  },
  {
    id: "zoocode",
    label: "ZooCode",
    kind: "file",
    category: "ide",
    sharedWith:
      "Format: mcpServers JSON at .roo/mcp.json (project). Skill installs to .roo/skills/.",
  },
  {
    id: "kilo-code",
    label: "Kilo Code",
    kind: "file",
    category: "ide",
    sharedWith:
      "Format: mcpServers JSON at .kilocode/mcp.json (project). Skill installs to .kilocode/skills/.",
  },
  {
    id: "rider",
    label: "Rider (Junie)",
    kind: "file",
    category: "ide",
    sharedWith:
      "Format: mcpServers JSON at .junie/mcp/mcp.json (project). Skill installs to .junie/skills/.",
  },
  {
    id: "unity-ai",
    label: "Unity AI",
    kind: "file",
    category: "ide",
    sharedWith: "Format: mcpServers JSON at UserSettings/mcp.json (project).",
  },
  {
    id: "antigravity",
    label: "Antigravity",
    kind: "file",
    category: "ide",
    sharedWith:
      "Format: mcpServers JSON at ~/.gemini/antigravity/mcp_config.json (global). Skill installs to .agent/skills/.",
  },
  {
    id: "gemini",
    label: "Gemini CLI",
    kind: "file",
    category: "cli",
    sharedWith:
      "Format: mcpServers JSON at .gemini/settings.json (project). Skill installs to .gemini/skills/.",
  },
  {
    id: "codex",
    label: "Codex",
    kind: "file",
    category: "cli",
    sharedWith:
      "Format: TOML at .codex/config.toml (project). Emits a [mcp_servers.unity-open-mcp] table.",
  },
  {
    id: "opencode-global",
    label: "OpenCode (global)",
    kind: "file",
    category: "ide",
    sharedWith:
      "Format: mcp + $schema JSON (~/.config/opencode/opencode.json). Shared by: OpenCode and Opencode.",
  },
  {
    id: "opencode-project",
    label: "OpenCode (project)",
    kind: "file",
    category: "ide",
    sharedWith:
      "Format: mcp + $schema JSON (project-local opencode.json). Shared by: OpenCode and Opencode.",
  },
  {
    id: "zcode-global",
    label: "ZCode (global)",
    kind: "file",
    category: "ide",
    sharedWith:
      "Format: mcp.servers + type:stdio JSON (~/.zcode/cli/config.json). Skill installs to .agents/skills/. Shared by: ZCode.",
  },
  {
    id: "zcode-project",
    label: "ZCode (project)",
    kind: "file",
    category: "ide",
    sharedWith:
      "Format: mcp.servers + type:stdio JSON (project-local .zcode/cli/config.json). Skill installs to .agents/skills/. Shared by: ZCode.",
  },
  // --- CLI agents ---
  {
    id: "claude-code",
    label: "Claude Code (CLI only)",
    kind: "cli",
    category: "cli",
    popular: true,
    sharedWith:
      "CLI-only: renders a `claude mcp add` command (no config file is written). Skill installs to .claude/skills/.",
  },
  {
    id: "github-copilot-cli",
    label: "GitHub Copilot CLI",
    kind: "file",
    category: "cli",
    sharedWith:
      "Format: mcpServers JSON at .mcp.json (project, shared with Claude Code). Run `copilot` from the project root.",
  },
  // --- Manual / clipboard fallbacks ---
  {
    id: "manual",
    label: "Manual / copy JSON",
    kind: "clipboard",
    category: "manual",
    popular: true,
    sharedWith:
      "Copy a JSON snippet to paste into any MCP client manually. No file is written by the wizard.",
  },
  {
    id: "custom",
    label: "Custom / other",
    kind: "clipboard",
    category: "manual",
    sharedWith:
      "Copy a JSON snippet for any MCP client not listed above. Installs the skill into every known client folder.",
  },
];

/** Labels for the three client-picker categories. */
export const CLIENT_CATEGORY_LABELS: Record<"ide" | "cli" | "manual", string> = {
  ide: "Editor / IDE agents",
  cli: "CLI agents",
  manual: "Manual",
};

/** Human-readable label for an MCP client id. */
export function mcpClientLabel(id: McpClientId): string {
  return MCP_CLIENT_OPTIONS.find((o) => o.id === id)?.label ?? id;
}

/** Config-format family (file-backed / cli command / clipboard snippet) for a
 *  client id. */
export function clientKind(id: McpClientId): "file" | "cli" | "clipboard" {
  return MCP_CLIENT_OPTIONS.find((o) => o.id === id)?.kind ?? "file";
}

/** Wire-key form expected by the Rust MCP config writer for a client id. */
export function clientToWire(id: McpClientId): McpClientWire {
  switch (id) {
    case "cursor":
      return "cursor";
    case "claude-desktop":
      return "claudeDesktop";
    case "claude-code":
      return "claudeCode";
    case "opencode-global":
      return "opencodeGlobal";
    case "opencode-project":
      return "opencodeProject";
    case "zcode-global":
      return "zcodeGlobal";
    case "zcode-project":
      return "zcodeProject";
    case "manual":
      return "manual";
    case "cline":
      return "cline";
    case "codex":
      return "codex";
    case "gemini":
      return "gemini";
    case "github-copilot-cli":
      return "githubCopilotCli";
    case "kilo-code":
      return "kiloCode";
    case "rider":
      return "rider";
    case "unity-ai":
      return "unityAi";
    case "vscode-copilot":
      return "vsCopilot";
    case "vs-copilot":
      return "vsCopilot";
    case "zoocode":
      return "zoocode";
    case "antigravity":
      return "antigravity";
    case "custom":
      return "custom";
  }
}
