import type { Resource } from "@modelcontextprotocol/sdk/types.js";

export const healthSummary: Resource = {
  uri: "unity-open-mcp://health/summary",
  name: "Health summary",
  mimeType: "application/json",
  description:
    "Cached verify health summary (error/warn/info counts) from the last scan or gate validation.",
};

export const healthBaseline: Resource = {
  uri: "unity-open-mcp://health/baseline",
  name: "Health baseline",
  mimeType: "application/json",
  description:
    "Baseline JSON file summary (schemaVersion, platformProfile, severity counts) read from disk.",
};

export const bridgeStatus: Resource = {
  uri: "unity-open-mcp://bridge/status",
  name: "Bridge status",
  mimeType: "application/json",
  description:
    "Cached snapshot of the last successful bridge /ping response (connection, compile, play mode).",
};

// M18 Plan 2 / T18.2.3 — tool-groups capability resource. Static catalog
// (group ids, descriptions, default-enabled flags) discoverable before the
// first domain tool call. For compiled-state availability and the per-tool
// roster, call unity_open_mcp_capabilities (which embeds the full
// toolGroups block). For session activation state, call
// unity_open_mcp_manage_tools(action="list_groups").
export const toolGroups: Resource = {
  uri: "unity-open-mcp://tool-groups",
  name: "Tool groups",
  mimeType: "application/json",
  description:
    "Tool-group catalog (ids, descriptions, default-enabled flags). " +
    "Call unity_open_mcp_manage_tools to activate a group before using " +
    "its tools; call unity_open_mcp_capabilities for compiled-state " +
    "availability and the per-tool roster.",
};

export const ALL_RESOURCES: Resource[] = [
  healthSummary,
  healthBaseline,
  bridgeStatus,
  toolGroups,
];
