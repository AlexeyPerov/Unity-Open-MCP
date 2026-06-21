// M15 T6.1 — Shared router-stack construction.
//
// `index.ts` (stdio server) and `src/cli/` (thin CLI) both need the same
// LiveClient + BatchSpawn + ToolRouter wiring. Centralizing it here means a
// CLI command sees the exact same routing decisions an MCP client would — the
// acceptance criterion "run-tool returns the same JSON the MCP server would"
// holds by construction.

import { LiveClient } from "./live-client.js";
import { BatchSpawn } from "./batch-spawn.js";
import { ToolRouter } from "./tool-router.js";
import { PingCache } from "./ping-cache.js";
import { ResourceRouter } from "./resource-router.js";
import { BridgeEventStream } from "./event-stream.js";
import { resolvePort, resolveAuthToken } from "./instance-discovery.js";
import { ToolSessionState } from "./tool-session-state.js";

export interface ResolvedEnv {
  projectPath: string;
  port: number;
  authToken: string | undefined;
}

/**
 * Resolve the project path / port / auth token from explicit overrides (CLI
 * flags) or the process env (MCP server). Throws when no project path is set —
 * callers print a friendly message and exit non-zero.
 *
 * Mirrors the precedence in index.ts#getEnv:
 *   projectPath: override > UNITY_PROJECT_PATH
 *   port:        override > UNITY_OPEN_MCP_BRIDGE_PORT > lock file > hash
 *   authToken:   discovered from the same lock as the port (no token with an
 *                explicit port override)
 */
export function resolveEnv(
  projectPathOverride?: string,
  portOverride?: number,
): ResolvedEnv {
  const projectPath = projectPathOverride ?? process.env.UNITY_PROJECT_PATH;
  if (!projectPath) {
    throw new ResolveEnvError(
      "UNITY_PROJECT_PATH environment variable is required " +
        "(or pass --project <path>).",
    );
  }

  const rawEnvPort = process.env.UNITY_OPEN_MCP_BRIDGE_PORT;
  const envPort = rawEnvPort ? parseInt(rawEnvPort, 10) : undefined;
  const effectiveEnvPort =
    rawEnvPort && Number.isInteger(envPort) ? envPort : undefined;

  const port = resolvePort(
    projectPath,
    portOverride ?? effectiveEnvPort,
  );
  const authToken = resolveAuthToken(
    projectPath,
    portOverride ?? effectiveEnvPort,
  );

  return { projectPath, port, authToken };
}

export class ResolveEnvError extends Error {}

export interface RouterStack {
  live: LiveClient;
  batch: BatchSpawn;
  router: ToolRouter;
  pingCache: PingCache;
  resourceRouter: ResourceRouter;
  eventStream: BridgeEventStream;
  // M18 Plan 2 — per-session tool-group visibility state. Lives here so the
  // CLI (cli/cli.ts) and the stdio server share one store per process; both
  // route through the same ToolRouter which mutates it via manage_tools.
  sessionState: ToolSessionState;
  projectPath: string;
  port: number;
  authToken: string | undefined;
}

/**
 * Build the full router stack from resolved env. The CLI uses this directly;
 * the stdio server wraps the result in an MCP Server (see createServer in
 * index.ts).
 */
export function buildRouterStack(env: ResolvedEnv): RouterStack {
  const pingCache = new PingCache();
  const live = new LiveClient(env.port, pingCache, env.authToken, env.projectPath);
  const batch = new BatchSpawn();
  const eventStream = new BridgeEventStream(
    `http://127.0.0.1:${env.port}`,
    undefined,
    env.authToken,
  );
  const sessionState = new ToolSessionState();
  const router = new ToolRouter(live, batch, env.projectPath, eventStream, sessionState);
  const resourceRouter = new ResourceRouter({
    live,
    pingCache,
    projectPath: env.projectPath,
    port: env.port,
  });
  return {
    live,
    batch,
    router,
    pingCache,
    resourceRouter,
    eventStream,
    sessionState,
    projectPath: env.projectPath,
    port: env.port,
    authToken: env.authToken,
  };
}
