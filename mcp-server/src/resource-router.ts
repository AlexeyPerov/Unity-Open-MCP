import { readFile, stat } from "node:fs/promises";
import { resolve } from "node:path";
import type { ReadResourceResult } from "@modelcontextprotocol/sdk/types.js";
import type { LiveClient } from "./live-client.js";
import type { PingCache } from "./ping-cache.js";

const DEFAULT_BASELINE_PATH = "CI/unity-agent-baseline.json";

export interface ResourceHandlerDeps {
  live: LiveClient;
  pingCache: PingCache;
  projectPath: string;
  port: number;
}

interface BaselineFileData {
  schemaVersion: number;
  platformProfile: string;
  summary: { error: number; warn: number; info: number };
}

interface ParsedBaseline {
  asOf: string;
  schemaVersion: number;
  platformProfile: string;
  summary: { error: number; warn: number; info: number };
}

function toTextResource(
  uri: string,
  json: unknown,
): ReadResourceResult {
  return {
    contents: [
      {
        uri,
        mimeType: "application/json",
        text: JSON.stringify(json),
      },
    ],
  };
}

function noDataSummary() {
  return {
    status: "no_data" as const,
    asOf: null,
    summary: null,
    nextStep:
      "Run unity_agent_scan_paths or a gated mutation to populate the cache.",
  };
}

async function readBaselineFile(
  filePath: string,
): Promise<ParsedBaseline | null> {
  let content: string;
  try {
    content = await readFile(filePath, "utf-8");
  } catch {
    return null;
  }

  let data: BaselineFileData;
  try {
    data = JSON.parse(content) as BaselineFileData;
  } catch {
    return null;
  }

  let mtime: Date;
  try {
    const s = await stat(filePath);
    mtime = s.mtime;
  } catch {
    return null;
  }

  return {
    asOf: mtime.toISOString(),
    schemaVersion: data.schemaVersion,
    platformProfile: data.platformProfile,
    summary: data.summary,
  };
}

export async function handleHealthSummary(
  deps: ResourceHandlerDeps,
): Promise<ReadResourceResult> {
  const json = await deps.live.readResource("unity-agent://health/summary");
  return toTextResource("unity-agent://health/summary", json);
}

export function handleHealthBaseline(
  deps: ResourceHandlerDeps,
): Promise<ReadResourceResult> {
  return (async () => {
    const baselinePath = resolve(deps.projectPath, DEFAULT_BASELINE_PATH);
    const parsed = await readBaselineFile(baselinePath);

    if (parsed === null) {
      return toTextResource("unity-agent://health/baseline", {
        status: "no_baseline",
        asOf: null,
        baselinePath: DEFAULT_BASELINE_PATH,
        nextStep: "Run unity_agent_baseline_create.",
      });
    }

    return toTextResource("unity-agent://health/baseline", {
      status: "ok",
      asOf: parsed.asOf,
      baselinePath: DEFAULT_BASELINE_PATH,
      schemaVersion: parsed.schemaVersion,
      platformProfile: parsed.platformProfile,
      summary: parsed.summary,
    });
  })();
}

export function handleBridgeStatus(
  deps: ResourceHandlerDeps,
): ReadResourceResult {
  const snapshot = deps.pingCache.get();

  if (!snapshot) {
    return toTextResource("unity-agent://bridge/status", {
      status: "no_data",
      asOf: null,
      connected: false,
    });
  }

  return toTextResource("unity-agent://bridge/status", {
    status: "ok",
    asOf: snapshot.asOf,
    connected: snapshot.connected,
    projectPath: snapshot.projectPath,
    bridgePort: deps.port,
    compiling: snapshot.compiling,
    isPlaying: snapshot.isPlaying,
  });
}

export class ResourceRouter {
  private deps: ResourceHandlerDeps;

  constructor(deps: ResourceHandlerDeps) {
    this.deps = deps;
  }

  async read(uri: string): Promise<ReadResourceResult> {
    switch (uri) {
      case "unity-agent://health/summary":
        return handleHealthSummary(this.deps);
      case "unity-agent://health/baseline":
        return handleHealthBaseline(this.deps);
      case "unity-agent://bridge/status":
        return handleBridgeStatus(this.deps);
      default:
        return toTextResource(uri, {
          status: "no_data",
          error: `Unknown resource URI: ${uri}`,
        });
    }
  }
}

export { DEFAULT_BASELINE_PATH, noDataSummary };
