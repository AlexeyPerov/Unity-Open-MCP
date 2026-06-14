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

export const ALL_RESOURCES: Resource[] = [
  healthSummary,
  healthBaseline,
  bridgeStatus,
];
