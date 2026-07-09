import type { McpLaunchModeWire } from "../../services/config.ts";

/**
 * The exclusive MCP launch-source mode shown as a single selector on Step 2.
 *
 * Plan 2 collapsed the former checkbox stack (`useLocalCheckout` +
 * `useGlobalInstall` + hidden `mcpIndexOverride` override) into one explicit
 * mode. Custom *is* a mode (it owns the former override path) — there is no
 * hidden mode switch when the override is set.
 */
export type McpLaunchSourceMode = "npx" | "global" | "local" | "custom";

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
 * Resolve the exclusive launch-source mode from the legacy draft fields.
 *
 * Used to hydrate the Step 2 radio selector from a persisted draft (which
 * still stores the three legacy booleans/strings). Precedence mirrors the
 * pre-Plan-2 `effectiveLaunchMode`: override > local > global > npx.
 */
export function resolveLaunchSourceMode(
  input: LaunchModeInput,
): McpLaunchSourceMode {
  if (input.mcpIndexOverride.trim().length > 0) return "custom";
  if (input.useLocalCheckout) return "local";
  if (input.useGlobalInstall) return "global";
  return "npx";
}

/**
 * The wire launch mode the wizard passes to `plan_mcp_config` /
 * `write_mcp_config` for a given exclusive source mode.
 *
 * Plan 2 owns the mode explicitly — there is no precedence ladder. `custom`
 * maps to the former `localOverride` wire mode.
 */
export function wireModeForSourceMode(
  mode: McpLaunchSourceMode,
): McpLaunchModeWire {
  switch (mode) {
    case "npx":
      return "npx";
    case "global":
      return "global";
    case "local":
      return "local";
    case "custom":
      return "localOverride";
  }
}

/**
 * The launch mode the wizard passes to `plan_mcp_config` / `write_mcp_config`.
 *
 * Precedence: the Step 2 advanced override always wins (the explicit
 * "custom mcp-server/dist/index.js" escape hatch), then the toggle picks local
 * vs npx, then the global-install option refines npx → global.
 *
 * @deprecated Prefer {@link resolveLaunchSourceMode} +
 *   {@link wireModeForSourceMode} (Plan 2 exclusive-mode model). This helper
 *   is retained for the draft-hydration path and existing tests.
 */
export function effectiveLaunchMode(input: LaunchModeInput): McpLaunchModeWire {
  return wireModeForSourceMode(resolveLaunchSourceMode(input));
}
