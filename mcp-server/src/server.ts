// M31 Plan 6 / T6.6 — heavy server module, dynamically imported by index.ts.
//
// This module owns the stdio MCP server construction (the `Server`, the
// `ALL_TOOLS` graph, the live/batch/router stack). It is imported lazily by
// `index.ts` so the `--version` / `--help` / no-command fast paths never
// evaluate the ~270-tool surface. Splitting it out keeps `index.ts` a thin
// launcher whose only static imports are the light CLI + version helpers.
//
// Prior to this split, `index.ts` statically imported `ALL_TOOLS` and the full
// router stack at the top of the file, so every `unity-open-mcp --version`
// paid for the whole graph. The createServer + getEnv functions moved here
// unchanged; `index.ts`'s `main()` awaits `import("./server.js")` only on the
// stdio-server code path.

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import {
  ListToolsRequestSchema,
  CallToolRequestSchema,
  ListResourcesRequestSchema,
  ReadResourceRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { ALL_TOOLS } from "./tools/index.js";
import { ALL_RESOURCES } from "./resources/index.js";
import { LiveClient } from "./live-client.js";
import { BatchSpawn } from "./batch-spawn.js";
import { ToolRouter } from "./tool-router.js";
import { PingCache } from "./ping-cache.js";
import { ResourceRouter } from "./resource-router.js";
import { withSchemaDefaults } from "./schema-defaults.js";
import { resolvePort, resolveAuthToken } from "./instance-discovery.js";
import {
  PORT_ENV_VAR,
  PROJECT_PATH_ENV_VAR,
  bridgeBaseUrl,
} from "./constants.js";
// M23 Plan 3 — per-request routing (port override + agent identity).
import { extractRouting } from "./agent-identity.js";
import { BridgeEventStream } from "./event-stream.js";
import type { CallToolResult, Tool } from "@modelcontextprotocol/sdk/types.js";
import { readPackageVersion } from "./package-version.js";
import { getServerInstructions } from "./server-instructions.js";
import {
  ToolSessionState,
  filterVisibleTools,
} from "./tool-session-state.js";

// Read the version from package.json at runtime so `npm version` (and the
// maintainer-panel version-bump in the Hub) keep the reported server + CLI
// version in sync without editing this source file.
const PACKAGE_VERSION = readPackageVersion();

/** Name → tool lookup, built once for default-injection in the CallTool handler. */
const TOOL_BY_NAME = new Map<string, Tool>(
  ALL_TOOLS.map((t) => [t.name, t]),
);

/**
 * Resolve env for the MCP server. UNITY_PROJECT_PATH is mandatory. The port
 * uses M13 T4.3 instance discovery:
 *   1. UNITY_OPEN_MCP_BRIDGE_PORT env var (override wins; users who pin a
 *      port keep working as before)
 *   2. ~/.unity-open-mcp/instances/<hash>.json lock file (when its pid is alive)
 *   3. deterministic hash of the project path (20000 + sha256 % 10000)
 * The resolved port is logged so users can see which bridge was picked.
 */
export function getEnv(): { projectPath: string; port: number; authToken?: string; envPort?: number } {
  const projectPath = process.env[PROJECT_PATH_ENV_VAR];
  if (!projectPath) {
    console.error(
      `unity-open-mcp: ${PROJECT_PATH_ENV_VAR} environment variable is required.`,
    );
    process.exit(1);
  }
  const rawEnvPort = process.env[PORT_ENV_VAR];
  const envPort = rawEnvPort ? parseInt(rawEnvPort, 10) : undefined;
  const resolvedEnvPort =
    rawEnvPort && Number.isInteger(envPort) ? envPort : undefined;
  const port = resolvePort(projectPath, resolvedEnvPort);
  const source =
    rawEnvPort && Number.isInteger(envPort)
      ? "env override"
      : "instance discovery";
  console.error(
    `[unity-open-mcp] Bridge port resolved to ${port} (${source}) for project ${projectPath}`,
  );
  // M14 — auto-discover the bridge's per-session bearer token from the same
  // instance lock we read the port from. Undefined when no live lock exists
  // (older bridge / env port override); in that case the client sends no
  // Authorization header and the bridge must be in authMode "none".
  const authToken = resolveAuthToken(
    projectPath,
    Number.isInteger(envPort) ? envPort : undefined,
  );
  if (authToken) {
    console.error("[unity-open-mcp] Bridge auth token discovered from instance lock.");
  } else {
    console.error(
      "[unity-open-mcp] No bridge auth token discovered (authMode must be \"none\").",
    );
  }
  return { projectPath, port, authToken, envPort: resolvedEnvPort };
}

export function createServer(
  projectPath: string,
  port: number,
  authToken?: string,
  envPort?: number,
): Server {
  const server = new Server(
    { name: "unity-open-mcp", version: PACKAGE_VERSION },
    {
      capabilities: { tools: { listChanged: true }, resources: {} },
      // M22 Plan 1 / T22.1.5 — rich server instructions surfaced via the MCP
      // `initialize` response. Clients may inject this into the system prompt;
      // it covers payload sizing + paging, Unity-API verification workflow,
      // and the mutate→gate→fix loop. Kept clean of internal IDs (no
      // milestone / specs / reference-project handles).
      instructions: getServerInstructions(),
    },
  );

  const pingCache = new PingCache();
  const liveClient = new LiveClient(port, pingCache, authToken, projectPath, undefined, envPort);
  const batchSpawn = new BatchSpawn({ projectPath });
  // M13 T4.4 — one SSE subscription per server process. The MCP server is the
  // only long-lived hop between the bridge and the LLM; a per-process reader
  // amortizes the connection and lets every `unity_senses_pull_events` call
  // share the same buffered queue.
  const eventStream = new BridgeEventStream(
    bridgeBaseUrl(port),
    undefined,
    authToken,
  );
  // Per-session tool-group visibility state. Lives in the MCP server and
  // restores the catalog's default-on groups on every server restart; mutated
  // only by unity_open_mcp_manage_tools and consulted by ListTools.
  const sessionState = new ToolSessionState();
  const notifyToolListChanged = async (): Promise<void> => {
    try {
      await server.notification({ method: "notifications/tools/list_changed" });
    } catch (err) {
      console.error(
        "[unity-open-mcp] Failed to send tools/list_changed notification:",
        err,
      );
    }
  };
  const router = new ToolRouter(
    liveClient,
    batchSpawn,
    projectPath,
    eventStream,
    sessionState,
    notifyToolListChanged,
  );
  const resourceRouter = new ResourceRouter({
    live: liveClient,
    pingCache,
    projectPath,
    port,
  });

  // ListTools filters per the session's active groups. A fresh session sees
  // the two default-on groups (core + gate-and-verify) plus always-visible
  // meta-tools. Activating a group via manage_tools adds its tools to
  // subsequent ListTools responses.
  server.setRequestHandler(ListToolsRequestSchema, async () => ({
    tools: filterVisibleTools(ALL_TOOLS, sessionState),
  }));

  server.setRequestHandler(
    CallToolRequestSchema,
    async (request): Promise<CallToolResult> => {
      const { name, arguments: args } = request.params;
      const callArgs = (args ?? {}) as Record<string, unknown>;
      // M23 Plan 3 — extract per-request routing (port override + agent id)
      // BEFORE schema defaults are applied, so the routing keys (_meta, port)
      // are stripped and never forwarded to the bridge. The default path (no
      // override) is zero-overhead: the default LiveClient + process agent id.
      const routing = extractRouting(callArgs);
      // Fill missing top-level scalar defaults (e.g. timeout_ms) from the
      // tool's documented schema. MCP clients may omit these; without this
      // step every downstream layer falls back to its own hardcoded value and
      // silently contradicts the schema (historically 30s vs run_tests' 60s).
      const tool = TOOL_BY_NAME.get(name);
      const routedArgs = tool
        ? withSchemaDefaults(tool, routing.strippedArgs)
        : routing.strippedArgs;
      // No port override → default router (the common single-bridge case).
      if (routing.portOverride === undefined) {
        return router.route(name, routedArgs);
      }
      // Port override → build a transient LiveClient aimed at the override
      // port. The override bypasses shared session state: it is a fresh client
      // that resolves its own auth token from the override port's instance
      // lock (when one exists). The agent id travels as X-Agent-Id so the
      // target bridge's fair queue can schedule it.
      const overrideAuth = resolveAuthToken(projectPath, routing.portOverride);
      const overrideLive = new LiveClient(
        routing.portOverride,
        pingCache,
        overrideAuth,
        projectPath,
        routing.agentId,
        // A per-request _meta.port override is authoritative exactly like an
        // env-port override: pass it as envPort so refreshEndpointFromLock is
        // a no-op for this transient client (no lock re-resolution).
        routing.portOverride,
      );
      return router.routeOverride(name, routedArgs, overrideLive);
    },
  );

  server.setRequestHandler(ListResourcesRequestSchema, async () => ({
    resources: ALL_RESOURCES,
  }));

  server.setRequestHandler(ReadResourceRequestSchema, async (request) => {
    const { uri } = request.params;
    return resourceRouter.read(uri);
  });

  return server;
}
