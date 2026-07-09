import type { McpLaunchModeWire } from "../../services/config.ts";

/** Inputs that determine the MCP server launch mode. */
export interface LaunchModeInput {
  /** Explicit "custom mcp-server/dist/index.js" override (advanced field). */
  mcpIndexOverride: string;
  /** True to onboard against a local toolkit checkout; false (npx default). */
  useLocalCheckout: boolean;
  /** True to launch the bare `unity-open-mcp` binary (assumes
   *  `npm i -g unity-open-mcp`) instead of `npx -y unity-open-mcp@latest`. */
  useGlobalInstall: boolean;
}

/**
 * The launch mode the wizard passes to `plan_mcp_config` / `write_mcp_config`.
 *
 * Precedence: the Step 2 advanced override always wins (the explicit
 * "custom mcp-server/dist/index.js" escape hatch), then the toggle picks local
 * vs npx, then the global-install option refines npx → global.
 */
export function effectiveLaunchMode(input: LaunchModeInput): McpLaunchModeWire {
  if (input.mcpIndexOverride.trim().length > 0) return "localOverride";
  if (input.useLocalCheckout) return "local";
  return input.useGlobalInstall ? "global" : "npx";
}
