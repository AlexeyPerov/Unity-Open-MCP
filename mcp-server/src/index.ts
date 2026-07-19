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

  // M31 Plan 6 / T6.6 — first stdio-server boot: dynamically import the heavy
  // server module. Up to this point (--version / --help / CLI subcommands /
  // no-command fallthrough that didn't match a subcommand) the ALL_TOOLS graph
  // has not been evaluated.
  const { getEnv, createServer } = await import("./server.js");
  const { port, projectPath, authToken, envPort } = getEnv();
  const server = createServer(projectPath, port, authToken, envPort);
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  console.error("unity-open-mcp fatal:", err);
  process.exit(1);
});
