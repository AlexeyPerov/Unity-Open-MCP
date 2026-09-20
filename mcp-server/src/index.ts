#!/usr/bin/env node

// Entry point for `unity-open-mcp`.
//
// M31 Plan 6 / T6.6 — thin launcher. The only static imports here are the
// light CLI + version helpers. The heavy server module (`./server.js`, which
// imports the ~270-tool `ALL_TOOLS` graph and the full live/batch/router
// stack) is loaded dynamically ONLY when the stdio-server code path is taken.
// This makes `unity-open-mcp --version` / `--help` / no-command fast paths
// skip the ALL_TOOLS graph entirely — the documented exceptional lazy-load
// permitted by mcp-server/AGENTS.md.
//
// `runCli` (from `./cli/cli.js`) is itself light at module top-level: it
// dynamically imports `./commands.js` + `../routers.js` only when a real
// subcommand is dispatched, so importing it here does not pull ALL_TOOLS.

import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { KNOWN_COMMANDS } from "./cli/args.js";
import { runCli } from "./cli/cli.js";
import { readPackageVersion } from "./package-version.js";
import { PROJECT_PATH_ENV_VAR } from "./constants.js";
import {
  notUnityProjectMessage,
  parseServerFlags,
  ProjectPathError,
  resolveProjectPath,
  validateUnityProjectRoot,
} from "./project-path.js";

// Read the version from package.json at runtime so `npm version` (and the
// maintainer-panel version-bump in the Hub) keep the reported server + CLI
// version in sync without editing this source file.
const PACKAGE_VERSION = readPackageVersion();

async function main() {
  // M15 T6.1 — CLI dispatch. When argv[0] is a known subcommand (ping,
  // wait-for-ready, status, run-tool) or an explicit --help/--version, run
  // the thin CLI and exit. Otherwise fall through to the stdio MCP server so
  // a single `bin` works for both MCP clients and scripting.
  const firstArg = process.argv[2];
  const looksLikeCli =
    firstArg !== undefined &&
    (KNOWN_COMMANDS.includes(firstArg) ||
      firstArg === "--help" ||
      firstArg === "-h" ||
      firstArg === "--version" ||
      firstArg === "-V");
  if (looksLikeCli) {
    const outcome = await runCli({ version: PACKAGE_VERSION });
    if (outcome.handled) {
      process.exit(outcome.exitCode);
    }
    // runCli only returns handled:false when argv had no recognized command;
    // in that case fall through to the stdio server below.
  }

  // Portable project-path resolution runs BEFORE the heavy import so a
  // misconfigured spawn fails in milliseconds instead of after the ~270-tool
  // graph is built. `UNITY_PROJECT_PATH` still wins over the flags, so every
  // existing client config behaves exactly as before; `--project-from-cwd`
  // (optionally with `--unity-subpath <rel>`) is what makes a committed,
  // machine-independent config possible.
  const flags = parseServerFlags(process.argv.slice(2));
  if (flags.error) {
    console.error(`unity-open-mcp: ${flags.error}`);
    process.exit(2);
  }
  const projectRoot = resolveStdioProjectPath(flags);

  // M31 Plan 6 / T6.6 — first stdio-server boot: dynamically import the heavy
  // server module. Up to this point (--version / --help / CLI subcommands /
  // no-command fallthrough that didn't match a subcommand) the ALL_TOOLS graph
  // has not been evaluated.
  const { getEnv, createServer } = await import("./server.js");
  const { port, projectPath, authToken, envPort } = getEnv({
    projectPath: projectRoot,
  });
  const server = createServer(projectPath, port, authToken, envPort);
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

/**
 * Resolve + report the Unity project root for the stdio server. A path derived
 * from the spawn cwd is a hard error when it is not a Unity root (the user
 * almost certainly opened the client on the wrong folder, or needs
 * `--unity-subpath`); an explicit `UNITY_PROJECT_PATH` only warns, because
 * that path has always been accepted unchecked and the bridge reports the same
 * problem in more detail once it connects.
 */
function resolveStdioProjectPath(flags: ReturnType<typeof parseServerFlags>): string {
  let resolved;
  try {
    resolved = resolveProjectPath({
      envPath: process.env[PROJECT_PATH_ENV_VAR],
      cwd: process.cwd(),
      projectFromCwd: flags.projectFromCwd,
      unitySubpath: flags.unitySubpath,
    });
  } catch (err) {
    const message = err instanceof ProjectPathError ? err.message : String(err);
    console.error(`unity-open-mcp: ${message}`);
    return process.exit(1);
  }
  console.error(
    `[unity-open-mcp] Unity project resolved to ${resolved.absolute} (source: ${resolved.source})`,
  );
  const validation = validateUnityProjectRoot(resolved.absolute);
  if (!validation.valid) {
    const message = notUnityProjectMessage(resolved, validation);
    if (resolved.source === "env") {
      console.error(`[unity-open-mcp] Warning: ${message}`);
    } else {
      console.error(`unity-open-mcp: ${message}`);
      console.error(
        "unity-open-mcp: pass --unity-subpath <relative-path> when the Unity project is a subfolder of the workspace.",
      );
      return process.exit(1);
    }
  }
  return resolved.absolute;
}

main().catch((err) => {
  console.error("unity-open-mcp fatal:", err);
  process.exit(1);
});
