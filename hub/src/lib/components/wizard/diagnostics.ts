import type {
  BridgePingResult,
  BridgeStatusKind,
  McpConfigHeuristic,
  NodeProbe,
  ProjectState,
} from "../../services/config.ts";

/** A single row in the Step 1 diagnostics panel. */
export interface DiagRow {
  id: string;
  label: string;
  ok: boolean;
  /** When `true` the row is informational (not a pass/fail gate). */
  info?: boolean;
  detail?: string;
  remediation?: string;
}

/** Inputs to the diagnostics-row builder. All fields are optional so callers
 *  can pass the live reactive state directly (e.g. a probe that is still
 *  running). */
export interface DiagnosticsInput {
  detection: ProjectState | null;
  nodeProbe: NodeProbe | null;
  nodeProbing: boolean;
  step5BridgeStatus: BridgeStatusKind;
  /** Non-null only after a Step 5 launch attempt. */
  step5LaunchPid: number | null;
}

/** Whether any of the heuristic flags indicates a configured MCP client. */
export function mcpHeuristicAny(h: McpConfigHeuristic): boolean {
  return (
    h.cursor ||
    h.claudeDesktop ||
    h.opencodeGlobal ||
    h.opencodeProject ||
    h.zcodeGlobal ||
    h.zcodeProject
  );
}

/** Build the diagnostics rows for the Step 1 panel from live state. Pure —
 *  no Svelte dependency, safe to unit-test. */
export function diagnosticsRows(input: DiagnosticsInput): DiagRow[] {
  const rows: DiagRow[] = [];
  const d = input.detection;
  // Project layout + Unity version.
  rows.push({
    id: "unity-project",
    label: "Valid Unity project layout",
    ok: !!d?.isValidUnityProject,
    remediation: d?.isValidUnityProject
      ? undefined
      : "Add the project via the Projects tab; the folder needs Assets/ and ProjectSettings/.",
  });
  rows.push({
    id: "unity-version",
    label: "Unity version meets minimum (2022.3 LTS)",
    ok: !!d?.meetsMinUnityVersion,
    detail: d?.unityVersion ?? "unknown",
    remediation: d?.meetsMinUnityVersion
      ? undefined
      : "Install Unity 2022.3 LTS or newer from the Installs tab.",
  });
  // Node.js.
  rows.push({
    id: "node",
    label: `Node.js ${input.nodeProbe?.requiredMajor ?? 18}+`,
    ok: !!input.nodeProbe?.ok,
    detail:
      input.nodeProbe?.version ??
      (input.nodeProbing ? "probing…" : "not detected"),
    remediation: input.nodeProbe?.ok
      ? undefined
      : "Install the LTS from https://nodejs.org/ and restart the Hub.",
  });
  // Manifest writable.
  rows.push({
    id: "manifest-writable",
    label: "Writable Packages/manifest.json",
    ok: !!d?.manifestWritable,
    remediation: d?.manifestWritable
      ? undefined
      : "Check write permissions on the project's Packages/ folder.",
  });
  // Packages installed (informational — installed on Step 3).
  rows.push({
    id: "bridge-installed",
    label: "Bridge package installed",
    ok: !!d?.bridgeInstalled,
    info: true,
    remediation: d?.bridgeInstalled
      ? undefined
      : "Install the bridge on the Unity packages step.",
  });
  rows.push({
    id: "verify-installed",
    label: "Verify package installed",
    ok: !!d?.verifyInstalled,
    info: true,
    remediation: d?.verifyInstalled
      ? undefined
      : "Install verify on the Unity packages step.",
  });
  // MCP configured (informational — configured on Step 4).
  const mcpAny = !!d?.mcpConfigured && mcpHeuristicAny(d.mcpConfigured);
  rows.push({
    id: "mcp-configured",
    label: "MCP client configured",
    ok: mcpAny,
    info: true,
    remediation: mcpAny
      ? undefined
      : "Configure an MCP client on the Configure AI client step.",
  });
  // Bridge reachable — only when Step 5 has run.
  if (
    input.step5BridgeStatus.kind !== "notChecked" ||
    input.step5LaunchPid !== null
  ) {
    rows.push({
      id: "bridge-reachable",
      label: "Bridge reachable (/ping)",
      ok: input.step5BridgeStatus.kind === "ok",
      info: true,
      detail:
        input.step5BridgeStatus.kind === "ok"
          ? "connected"
          : input.step5BridgeStatus.kind === "failed"
            ? input.step5BridgeStatus.message
            : "pending",
      remediation:
        input.step5BridgeStatus.kind === "ok"
          ? undefined
          : "Run the Launch and verify step; check the launch log for errors.",
    });
  }
  return rows;
}

/** Tone for a diagnostics row. */
export function diagTone(row: DiagRow): "ok" | "warn" | "muted" {
  if (row.info && row.ok) return "ok";
  if (row.info && !row.ok) return "muted";
  return row.ok ? "ok" : "warn";
}

/** Human-readable summary of detected MCP clients from a heuristic. */
export function mcpConfiguredSummary(h: McpConfigHeuristic): string {
  if (!mcpHeuristicAny(h)) return "not detected";
  const clients: string[] = [];
  if (h.cursor) clients.push("Cursor");
  if (h.claudeDesktop) clients.push("Claude Desktop");
  if (h.opencodeGlobal) clients.push("OpenCode (global)");
  if (h.opencodeProject) clients.push("OpenCode (project)");
  if (h.zcodeGlobal) clients.push("ZCode (global)");
  if (h.zcodeProject) clients.push("ZCode (project)");
  return `yes (${clients.join(", ")})`;
}
