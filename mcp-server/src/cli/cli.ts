// M15 T6.1 — CLI dispatcher.
//
// This module is the entry point for `unity-open-mcp <command>`. It:
//   1. parses argv (src/cli/args.ts)
//   2. builds the shared router stack (src/routers.ts)
//   3. dispatches to the right command (src/cli/commands.ts)
//   4. prints JSON or human-readable output and sets the exit code
//
// When argv[0] is NOT a known subcommand, runCli returns null — the caller
// (index.ts) then falls through to the stdio MCP server. That keeps a single
// `bin` working for both `npx unity-open-mcp wait-for-ready` and an MCP client
// spawning `node dist/index.js`.

import { parseCliArgs } from "./args.js";
import {
  runPingCommand,
  runWaitForReadyCommand,
  runStatusCommand,
  runRunToolCommand,
  helpText,
  versionText,
  DEFAULT_WAIT_TIMEOUT_MS,
  DEFAULT_POLL_INTERVAL_MS,
  type CliCommandResult,
} from "./commands.js";
import {
  resolveEnv,
  buildRouterStack,
  ResolveEnvError,
  type RouterStack,
} from "../routers.js";

const PING_DEFAULT_TIMEOUT_MS = 5_000;

export interface CliRunOptions {
  /** Package version, used by --version. */
  version: string;
  /** Invocation name for help text (e.g. "unity-open-mcp"). */
  binName?: string;
  /** argv after the node binary + script path. Defaults to process.argv.slice(2). */
  argv?: string[];
}

export interface CliRunOutcome {
  /** True when argv recognized a CLI subcommand (or --help/--version). */
  handled: boolean;
  /** Process exit code; only meaningful when handled === true. */
  exitCode: number;
}

/**
 * Run the CLI. Returns whether the invocation was a CLI subcommand (handled)
 * so index.ts can decide whether to fall through to the stdio server.
 *
 * This function writes to stdout/stderr itself and is meant to be the top of
 * the process. It does NOT call process.exit — the caller does, so tests can
 * drive it without tearing down the test runner.
 */
export async function runCli(opts: CliRunOptions): Promise<CliRunOutcome> {
  const argv = opts.argv ?? process.argv.slice(2);
  const parsed = parseCliArgs(argv);
  const binName = opts.binName ?? "unity-open-mcp";

  // --help / --version short-circuit before any project-path requirement.
  if (parsed.command === "help") {
    process.stdout.write(helpText(binName) + "\n");
    return { handled: true, exitCode: 0 };
  }
  if (parsed.command === "version") {
    process.stdout.write(versionText(opts.version) + "\n");
    return { handled: true, exitCode: 0 };
  }

  // No command → caller falls through to the MCP server.
  if (!parsed.command) {
    return { handled: false, exitCode: 0 };
  }

  if (parsed.error) {
    process.stderr.write(`unity-open-mcp: ${parsed.error}\n\n`);
    process.stderr.write(helpText(binName) + "\n");
    return { handled: true, exitCode: 2 };
  }

  // Build the router stack. Every command needs a project path + resolved
  // bridge port.
  let stack: RouterStack;
  try {
    const env = resolveEnv(parsed.projectPath, parsed.port);
    logResolve(env.port, env.projectPath, env.authToken);
    stack = buildRouterStack(env);
  } catch (err) {
    if (err instanceof ResolveEnvError) {
      process.stderr.write(`unity-open-mcp: ${err.message}\n`);
      return { handled: true, exitCode: 2 };
    }
    throw err;
  }

  let result: CliCommandResult;
  try {
    switch (parsed.command) {
      case "ping":
        result = await runPingCommand(stack, {
          json: parsed.json,
          timeoutMs: parsed.timeoutMs ?? PING_DEFAULT_TIMEOUT_MS,
        });
        break;
      case "wait-for-ready":
        result = await runWaitForReadyCommand(stack, {
          json: parsed.json,
          timeoutMs: parsed.timeoutMs ?? DEFAULT_WAIT_TIMEOUT_MS,
          intervalMs: parsed.intervalMs ?? DEFAULT_POLL_INTERVAL_MS,
        });
        break;
      case "status":
        result = await runStatusCommand(stack, { json: parsed.json });
        break;
      case "run-tool":
        result = await runRunToolCommand(stack, {
          json: parsed.json,
          toolName: parsed.toolName!,
          toolArgs: parsed.toolArgs,
        });
        break;
      default:
        // Unreachable: parsed.command is one of the cases above or help/version.
        process.stderr.write(
          `unity-open-mcp: internal error — unhandled command '${parsed.command}'.\n`,
        );
        return { handled: true, exitCode: 2 };
    }
  } finally {
    // The event stream opens an SSE reader; tear it down so the process can
    // exit cleanly. (run-tool may have started a subscription via pull_events.)
    try {
      stack.eventStream.stop();
    } catch {
      // best-effort
    }
  }

  emitResult(result, parsed.json);
  return { handled: true, exitCode: result.exitCode };
}

function logResolve(port: number, projectPath: string, authToken: string | undefined): void {
  // Match the stdio server's startup log shape so `status`/logs look the same
  // regardless of how the process was launched.
  process.stderr.write(
    `[unity-open-mcp] Bridge port resolved to ${port} for project ${projectPath}\n`,
  );
  if (authToken) {
    process.stderr.write("[unity-open-mcp] Bridge auth token discovered from instance lock.\n");
  }
}

function emitResult(result: CliCommandResult, json: boolean): void {
  const stream = result.exitCode === 0 ? process.stdout : process.stderr;
  if (json) {
    process.stdout.write(JSON.stringify(result.json, null, 2) + "\n");
    return;
  }
  // Human-readable: keep success on stdout, failures on stderr so a CI pipeline
  // can capture the message without mixing it into stdout JSON downstream.
  stream.write(result.human + "\n");
}
