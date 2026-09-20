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
import {
  ProjectPathError,
  resolveProjectPath,
  type ProjectPathSource,
} from "./project-path.js";
import {
  PORT_ENV_VAR,
  PROJECT_PATH_ENV_VAR,
  bridgeBaseUrl,
} from "./constants.js";

export interface ResolvedEnv {
  /** Normalized absolute Unity project root. */
  projectPath: string;
  /** Which input produced {@link projectPath} (flag / env / cwd). */
  projectPathSource: ProjectPathSource;
  port: number;
  authToken: string | undefined;
  /** Parsed UNITY_OPEN_MCP_BRIDGE_PORT (or CLI --port override), else undefined.
   *  Threaded into LiveClient so refreshEndpointFromLock respects the same
   *  override precedence as construction (env override ⇒ no lock refresh). */
  envPort: number | undefined;
}

export interface ResolveEnvOptions {
  /** `--project-from-cwd`: derive the Unity project root from the cwd. */
  projectFromCwd?: boolean;
  /** `--unity-subpath <rel>`: Unity project subfolder under the cwd. */
  unitySubpath?: string;
}

/**
 * Resolve the project path / port / auth token from explicit overrides (CLI
 * flags) or the process env (MCP server). Throws when no project path is set —
 * callers print a friendly message and exit non-zero.
 *
 * The project path goes through the shared `resolveProjectPath` resolver, so
 * the CLI accepts exactly what the stdio server accepts: an absolute path, a
 * path relative to the cwd (`--project Client` from a monorepo root), or no
 * path at all with `--project-from-cwd [--unity-subpath Client]`.
 *
 *   projectPath: --project > UNITY_PROJECT_PATH > cwd (+ subpath)
 *   port:        override > UNITY_OPEN_MCP_BRIDGE_PORT > lock file > hash
 *   authToken:   discovered from the same lock as the port (no token with an
 *                explicit port override)
 */
export function resolveEnv(
  projectPathOverride?: string,
  portOverride?: number,
  options: ResolveEnvOptions = {},
): ResolvedEnv {
  let resolvedProject;
  try {
    resolvedProject = resolveProjectPath({
      flagPath: projectPathOverride,
      envPath: process.env[PROJECT_PATH_ENV_VAR],
      cwd: process.cwd(),
      projectFromCwd: options.projectFromCwd,
      unitySubpath: options.unitySubpath,
    });
  } catch (err) {
    throw new ResolveEnvError(
      err instanceof ProjectPathError ? err.message : String(err),
    );
  }
  const projectPath = resolvedProject.absolute;

  const rawEnvPort = process.env[PORT_ENV_VAR];
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

  return {
    projectPath,
    projectPathSource: resolvedProject.source,
    port,
    authToken,
    envPort: portOverride ?? effectiveEnvPort,
  };
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
  const live = new LiveClient(env.port, pingCache, env.authToken, env.projectPath, undefined, env.envPort);
  const batch = new BatchSpawn({ projectPath: env.projectPath });
  // projectPath/envPort let the reader's reconnects re-read the instance lock
  // (the bridge rotates its bearer token on every domain reload), mirroring
  // LiveClient's refreshEndpointFromLock self-heal.
  const eventStream = new BridgeEventStream(
    bridgeBaseUrl(env.port),
    undefined,
    env.authToken,
    env.projectPath,
    env.envPort,
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
