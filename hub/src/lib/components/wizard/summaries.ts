import type {
  BridgePingResult,
  BridgeStatusKind,
  McpConfigHeuristic,
  ProjectState,
} from "../../services/config.ts";
import type { McpClientId } from "../../services/ai_toolkit.ts";

/** A tone + label pair rendered by StatusChip in the Done summary. */
export interface ToneLabel {
  tone: "ok" | "warn" | "muted" | "info" | "missing";
  label: string;
}

/** Packages-installed summary for the Done screen. */
export function packagesSummary(d: ProjectState | null): ToneLabel {
  if (!d) return { tone: "missing", label: "unknown" };
  if (d.bridgeInstalled && d.verifyInstalled) {
    return { tone: "ok", label: "installed" };
  }
  if (d.bridgeInstalled || d.verifyInstalled) {
    return { tone: "warn", label: "partial" };
  }
  return { tone: "missing", label: "not installed" };
}

/** Inputs to {@link mcpSummary}. */
export interface McpSummaryInput {
  detection: ProjectState | null;
  /** `true` when the Step 4 write action just wrote a config file. */
  mcpWritten: boolean;
  /** Currently-selected MCP client. */
  mcpClient: McpClientId;
}

/** MCP-configured summary for the Done screen. */
export function mcpSummary(input: McpSummaryInput): ToneLabel {
  const h: McpConfigHeuristic | undefined = input.detection?.mcpConfigured;
  if (!h) return { tone: "muted", label: "not detected" };
  if (
    h.cursor ||
    h.claudeDesktop ||
    h.opencodeGlobal ||
    h.opencodeProject ||
    h.zcodeGlobal ||
    h.zcodeProject
  ) {
    return { tone: "ok", label: "configured" };
  }
  if (input.mcpWritten) {
    return { tone: "ok", label: "written" };
  }
  if (input.mcpClient === "claude-code") {
    return { tone: "warn", label: "cli command" };
  }
  if (input.mcpClient === "manual") {
    return { tone: "warn", label: "manual" };
  }
  return { tone: "warn", label: "not configured" };
}

/** Step 5 item state mirrors the wizard's local Step5ItemState. */
export type Step5ItemState = "pending" | "running" | "ok" | "failed";

/** Inputs to {@link launchSummary} / {@link bridgeSummary}. */
export interface LaunchSummaryInput {
  /** Per-item state for the Step 5 launch + ping items. */
  launch: Step5ItemState;
  ping: Step5ItemState;
  bridgeStatus: BridgeStatusKind;
  launchPid: number | null;
}

/** Unity-launched summary for the Done screen. */
export function launchSummary(input: LaunchSummaryInput): ToneLabel {
  if (input.launch === "ok" && input.ping === "ok") {
    return { tone: "ok", label: "ok" };
  }
  if (input.launch === "ok") {
    return { tone: "warn", label: "ok · bridge pending" };
  }
  if (input.launch === "failed") {
    return { tone: "warn", label: "failed" };
  }
  if (input.bridgeStatus.kind === "notChecked" && input.launchPid === null) {
    return { tone: "muted", label: "not run" };
  }
  return { tone: "muted", label: "pending" };
}

/** Bridge-verified summary for the Done screen. */
export function bridgeSummary(status: BridgeStatusKind): ToneLabel {
  if (status.kind === "ok") {
    return {
      tone: "ok",
      label: status.connected ? "connected" : "responded",
    };
  }
  if (status.kind === "failed") {
    return { tone: "warn", label: "failed" };
  }
  return { tone: "muted", label: "not checked" };
}

/** Tone for a Step 5 checklist item. */
export function pingStatusTone(
  state: Step5ItemState,
): "ok" | "warn" | "muted" | "info" {
  if (state === "ok") return "ok";
  if (state === "failed") return "warn";
  if (state === "running") return "info";
  return "muted";
}

/** Human-readable description of a `/ping` result's error/status fields. */
export function describePingErrorMessage(
  result: BridgePingResult | null,
): string {
  if (!result) return "—";
  if (result.ok) return `connected=${result.connected} in ${result.durationMs} ms`;
  if (result.errorMessage) return `${result.errorKind}: ${result.errorMessage}`;
  return result.errorKind || "failed";
}
