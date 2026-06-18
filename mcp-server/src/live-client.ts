import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import type { MutationEnvelope } from "./gate-error.js";
import type { PingCache } from "./ping-cache.js";
import { deriveIsError } from "./gate-error.js";
import { existsSync, readFileSync, unlinkSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import {
  pollAndDismissLaunchErrors,
  readDismissConfig,
  type PollAndDismissOptions,
} from "./dialog-dismiss.js";
import { readInstanceLock, classifyInstance } from "./instance-discovery.js";

const MAX_COMPILE_WAIT_MS = 120_000;
const COMPILE_POLL_INTERVAL_MS = 2_000;
const PING_TIMEOUT_MS = 5_000;

const DIRECT_RESPONSE_TOOLS: ReadonlySet<string> = new Set([
  "unity_open_mcp_validate_edit",
  "unity_open_mcp_checkpoint_create",
  "unity_open_mcp_delta",
  "unity_open_mcp_find_references",
  "unity_open_mcp_scan_paths",
  // Compact drill-down reads return a structured model JSON directly; the MCP
  // ToolRouter applies the compression module on top (compressible-router.ts).
  "unity_open_mcp_read_asset",
  "unity_open_mcp_search_assets",
  // Test runner returns { status, runId } directly; LiveClient polls the
  // results file and returns the final result.
  "unity_senses_run_tests",
  // Agent senses (non-mutating): return tool JSON directly.
  "unity_senses_screenshot",
  "unity_senses_read_console",
  "unity_senses_profiler_capture",
  "unity_senses_profiler_memory",
  "unity_senses_profiler_rendering",
  "unity_senses_spatial_query",
]);

interface PingResponse {
  connected: boolean;
  projectPath: string | null;
  unityVersion: string | null;
  bridgeVersion: string;
  mode: string;
  compiling: boolean;
  isPlaying: boolean;
}

interface HttpErrorBody {
  error: {
    code: string;
    message: string;
  };
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function makeErrorResult(message: string, detail?: unknown): CallToolResult {
  return {
    content: [
      {
        type: "text",
        text: JSON.stringify(
          detail ?? { error: { code: "bridge_error", message } },
        ),
      },
    ],
    isError: true,
  };
}

const OFFLINE_HINT =
  "Ensure the Unity Editor is open with the Agent Bridge running.";

export class LiveClient implements Router {
  private baseUrl: string;
  private pingCache: PingCache;
  private dismissEnabled: boolean;
  private dismissTimeoutMs: number;
  private dismissIntervalMs: number;
  /** M14 — per-session bearer token auto-discovered from the instance lock.
   *  Undefined when no live lock was found (older bridge / env port override);
   *  in that case no Authorization header is sent and the bridge must be in
   *  authMode "none" for requests to succeed. */
  private authToken: string | undefined;
  /** Absolute Unity project path, used to read the instance lock mid-session
   *  for dead-bridge detection. Optional so existing callers/tests that don't
   *  exercise the fail-fast path keep working; when absent, a /ping failure
   *  falls back to the original bridge_offline behavior. */
  private projectPath: string | undefined;

  constructor(
    port: number,
    pingCache: PingCache,
    authToken?: string,
    projectPath?: string,
  ) {
    this.baseUrl = `http://127.0.0.1:${port}`;
    this.pingCache = pingCache;
    this.authToken = authToken;
    this.projectPath = projectPath;
    // M13 T4.5 — launch-errors / Safe Mode dialog auto-dismissal. Resolved
    // once at construction; the env vars do not change mid-process. The
    // feature is enabled by default (Unity-MCP parity) and runs concurrently
    // with every compile/bridge readiness wait so it ticks on the same stall
    // points agents hit, not only at process spawn.
    const dismissCfg = readDismissConfig();
    this.dismissEnabled = dismissCfg.enabled;
    this.dismissTimeoutMs = dismissCfg.timeoutMs;
    this.dismissIntervalMs = dismissCfg.intervalMs;
  }

  async isLiveAvailable(): Promise<boolean> {
    try {
      const res = await this.fetchWithTimeout("/ping", { method: "GET" });
      if (res.status === 503) return true;
      if (!res.ok) return false;
      const body = (await res.json()) as PingResponse;
      this.pingCache.record(body);
      return body.connected;
    } catch {
      return false;
    }
  }

  async route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    if (toolName === "unity_open_mcp_ping") {
      return this.handlePing();
    }
    if (toolName === "unity_senses_run_tests") {
      return this.handleRunTests(args);
    }
    return this.handleToolCall(toolName, args);
  }

  private async handlePing(): Promise<CallToolResult> {
    try {
      const res = await this.fetchWithTimeout("/ping", { method: "GET" });
      const body: PingResponse = await res.json();
      this.pingCache.record(body);
      return {
        content: [{ type: "text", text: JSON.stringify(body) }],
        isError: false,
      };
    } catch {
      return makeErrorResult(
        `Bridge is not reachable at ${this.baseUrl}. ${OFFLINE_HINT}`,
        {
          error: {
            code: "bridge_offline",
            message: `Cannot connect to bridge at ${this.baseUrl}`,
          },
        },
      );
    }
  }

  private async handleToolCall(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const readyError = await this.ensureReady();
    if (readyError) return readyError;

    return this.postTool(toolName, args, true);
  }

  private async handleRunTests(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const readyError = await this.ensureReady();
    if (readyError) return readyError;

    const startResult = await this.postTool(
      "unity_senses_run_tests",
      args,
      true,
    );

    if (startResult.isError) return startResult;

    const text =
      startResult.content[0]?.type === "text"
        ? startResult.content[0].text
        : "";
    let body: { status?: string; runId?: string; mode?: string };
    try {
      body = JSON.parse(text);
    } catch {
      return startResult;
    }

    if (body.status !== "started" || !body.runId) {
      return startResult;
    }

    return this.pollTestResults(body.runId, args);
  }

  private async pollTestResults(
    runId: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const timeoutMs =
      typeof args.timeout_ms === "number" ? args.timeout_ms : 60_000;
    const deadline = Date.now() + timeoutMs;
    // Check before sleeping: an EditMode run often finishes within a second or
    // two, and the previous fixed 1s-before-first-check + 1s interval added up
    // to seconds of dead time per call. Back off 250ms -> 1s so fast runs return
    // promptly without hammering the filesystem on long ones.
    const minIntervalMs = 250;
    const maxIntervalMs = 1_000;

    const resultsPath = join(
      homedir(),
      ".unity-open-mcp",
      `test-results-${runId}.json`,
    );

    let intervalMs = minIntervalMs;
    while (Date.now() < deadline) {
      try {
        if (existsSync(resultsPath)) {
          const content = readFileSync(resultsPath, "utf-8");
          try {
            unlinkSync(resultsPath);
          } catch {
            // best-effort cleanup
          }

          let isError = false;
          try {
            const parsed = JSON.parse(content);
            isError = parsed.status === "error";
          } catch {
            // unparseable → treat as error
            isError = true;
          }

          return {
            content: [{ type: "text", text: content }],
            isError,
          };
        }
      } catch {
        // continue polling
      }

      await sleep(intervalMs);
      intervalMs = Math.min(intervalMs * 2, maxIntervalMs);
    }

    return makeErrorResult(
      `Test results for run ${runId} were not available within ` +
        `${timeoutMs / 1000}s. The test run may still be in progress or ` +
        "the bridge may have lost the callback.",
      {
        error: {
          code: "test_results_timeout",
          message: `Test results poll timed out after ${timeoutMs / 1000}s`,
        },
      },
    );
  }

  private async postTool(
    toolName: string,
    args: Record<string, unknown>,
    retryOn503: boolean,
  ): Promise<CallToolResult> {
    try {
      const timeoutMs =
        typeof args.timeout_ms === "number" ? args.timeout_ms : 60_000;
      const res = await this.fetchWithTimeout(
        `/tools/${toolName}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json; charset=utf-8" },
          body: JSON.stringify(args),
        },
        timeoutMs + 10_000,
      );

      if (res.status === 503 && retryOn503) {
        const compileError = await this.waitForCompile();
        if (compileError) return compileError;
        return this.postTool(toolName, args, false);
      }

      if (!res.ok) {
        const body = (await res
          .json()
          .catch(() => null)) as HttpErrorBody | null;
        return makeErrorResult(
          body?.error?.message ?? `Bridge returned HTTP ${res.status}`,
          body ?? {
            error: {
              code: "bridge_http_error",
              message: `HTTP ${res.status}`,
            },
          },
        );
      }

      if (DIRECT_RESPONSE_TOOLS.has(toolName)) {
        const directBody = (await res.json().catch(() => null)) as Record<
          string,
          unknown
        > | null;
        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(directBody ?? {}),
            },
          ],
          isError: directBody?.error != null,
        };
      }

      const body = (await res.json()) as MutationEnvelope;
      return {
        content: [{ type: "text", text: JSON.stringify(body) }],
        isError: deriveIsError(body),
      };
    } catch {
      return makeErrorResult(
        `Failed to reach bridge at ${this.baseUrl}. ${OFFLINE_HINT}`,
        {
          error: {
            code: "bridge_offline",
            message: `Cannot connect to bridge at ${this.baseUrl}`,
          },
        },
      );
    }
  }

  private async ensureReady(): Promise<CallToolResult | null> {
    try {
      const res = await this.fetchWithTimeout("/ping", { method: "GET" });

      if (res.status === 503) {
        return this.waitForCompile();
      }

      if (!res.ok) {
        return makeErrorResult(
          `Bridge /ping returned unexpected HTTP ${res.status}`,
        );
      }

      const body = (await res.json()) as PingResponse;
      this.pingCache.record(body);

      if (!body.connected) {
        return makeErrorResult(
          "Bridge listener is running but session is not initialized.",
          {
            error: {
              code: "bridge_not_connected",
              message: "Bridge session not connected",
            },
          },
        );
      }

      if (body.compiling) {
        return this.waitForCompile();
      }

      return null;
    } catch {
      // Bridge is unreachable (ECONNREFUSED / timeout). Before falling back to
      // the generic offline error, check whether the instance lock indicates a
      // DEAD bridge assembly — the Unity process is still alive but the bridge
      // failed to reload after a compile error. That case is NOT recoverable
      // by waiting; surface it immediately so the agent can fetch the compiler
      // errors and fix them instead of hanging on /ping.
      const deadBridge = this.deadBridgeResult();
      if (deadBridge) return deadBridge;

      return makeErrorResult(
        `Bridge is not reachable at ${this.baseUrl}. ${OFFLINE_HINT}`,
        {
          error: {
            code: "bridge_offline",
            message: `Cannot connect to bridge at ${this.baseUrl}`,
          },
        },
      );
    }
  }

  // Detect the dead-bridge-assembly signature from the on-disk instance lock:
  // Unity process alive (PID running) but heartbeat stale (the bridge's
  // [InitializeOnLoad] never re-ran after a compile failure, so the heartbeat
  // writer is gone). Returns a structured error pointing the agent at
  // read_compile_errors, or null when the signature is not present (no lock,
  // dead PID, or a fresh/reloading heartbeat — i.e. the wait is still worth
  // keeping). Requires projectPath to have been threaded in; without it we
  // can't read the lock, so we return null and preserve the original behavior.
  private deadBridgeResult(): CallToolResult | null {
    if (!this.projectPath) return null;
    let classification;
    try {
      classification = classifyInstance(readInstanceLock(this.projectPath));
    } catch {
      return null;
    }
    if (classification !== "dead_bridge") return null;

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            error: {
              code: "bridge_compile_failed",
              message:
                "The bridge assembly failed to recompile and Unity is likely " +
                "in a bad state (safe mode / compile errors). The HTTP listener " +
                "will not return until the C# error is fixed. " +
                "Call unity_open_mcp_read_compile_errors to retrieve the compiler " +
                "errors from Unity's Editor.log, fix the cited file/line, then " +
                "trigger a recompile (e.g. a no-op edit + focus Unity, or " +
                "unity_open_mcp_compile_check once the source compiles).",
            },
          }),
        },
      ],
      isError: true,
    };
  }

  private async waitForCompile(): Promise<CallToolResult | null> {
    const deadline = Date.now() + MAX_COMPILE_WAIT_MS;

    // M13 T4.5 — run the launch-errors / Safe Mode dialog auto-dismiss loop
    // CONCURRENTLY with the compile poll. The dismiss loop ticks on the same
    // stall point this method represents (UCP bridge-wait pattern): if a
    // native modal is blocking the Editor, the compile poll below will spin
    // until timeout with no recovery. The dismiss loop clicks Ignore on that
    // modal; the moment compile resolves (compiling → idle), we abort the
    // loop — there is no launch dialog left to dismiss once Unity is idle.
    //
    // When auto-dismiss is opted out (UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1),
    // `dismissEnabled` is false and this branch is skipped entirely,
    // preserving the pre-feature baseline.
    let dismissAbort: AbortController | null = null;
    let dismissDone: Promise<void> | null = null;
    if (this.dismissEnabled) {
      dismissAbort = new AbortController();
      const dismissOpts: PollAndDismissOptions = {
        timeoutMs: this.dismissTimeoutMs,
        intervalMs: this.dismissIntervalMs,
        abortSignal: dismissAbort.signal,
      };
      dismissDone = this.runDismissLoop(dismissOpts);
    }

    try {
      while (Date.now() < deadline) {
        await sleep(COMPILE_POLL_INTERVAL_MS);

        try {
          const res = await this.fetchWithTimeout("/ping", { method: "GET" });

          if (res.status === 503) continue;

          if (!res.ok) continue;

          const body = (await res.json()) as PingResponse;

          if (!body.compiling && body.connected) return null;
        } catch {
          // Network failure during the poll — normal during a domain reload
          // (the listener is torn down and rebuilt). But if the instance lock
          // shows a dead bridge assembly (stale heartbeat + live PID), the
          // reload will never complete: abort the wait immediately with the
          // structured bridge_compile_failed error instead of spinning to the
          // 120s deadline.
          const deadBridge = this.deadBridgeResult();
          if (deadBridge) return deadBridge;
          continue;
        }
      }

      // Final check before declaring a timeout: a dead-bridge signature here
      // is a more useful error than the opaque compile_timeout.
      const deadBridge = this.deadBridgeResult();
      if (deadBridge) return deadBridge;

      return makeErrorResult(
        `Unity is still compiling after ${MAX_COMPILE_WAIT_MS / 1000}s. ` +
          "The compile-wait timeout was exceeded.",
        {
          error: {
            code: "compile_timeout",
            message: `Compile-wait exceeded ${MAX_COMPILE_WAIT_MS / 1000}s`,
          },
        },
      );
    } finally {
      // Compile wait resolved (idle) OR timed out — either way there is
      // nothing more to dismiss. Aborting unblocks the dismiss loop's sleep.
      dismissAbort?.abort();
      await dismissDone;
    }
  }

  /**
   * Run the launch-errors dismiss polling loop. Indirected through a method
   * so unit tests can stub the OS-clicking loop without invoking
   * PowerShell / osascript / xdotool. Production uses the real
   * `pollAndDismissLaunchErrors`.
   */
  protected runDismissLoop(opts: PollAndDismissOptions): Promise<void> {
    return pollAndDismissLaunchErrors(opts);
  }

  async readResource(route: string): Promise<Record<string, unknown>> {
    try {
      const res = await this.fetchWithTimeout(
        `/resources/${route}`,
        { method: "GET" },
        10_000,
      );

      if (!res.ok) {
        return {
          status: "no_data",
          asOf: null,
          summary: null,
          nextStep:
            "Run unity_open_mcp_scan_paths or a gated mutation to populate the cache.",
        };
      }

      const text = await res.text();
      return JSON.parse(text) as Record<string, unknown>;
    } catch {
      return {
        status: "no_data",
        asOf: null,
        summary: null,
        nextStep:
          "Run unity_open_mcp_scan_paths or a gated mutation to populate the cache.",
      };
    }
  }

  private fetchWithTimeout(
    path: string,
    init: RequestInit,
    timeoutMs: number = PING_TIMEOUT_MS,
  ): Promise<Response> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    // M14 — attach the bearer token to every request when one was discovered
    // from the instance lock. Merge with any caller-supplied headers so we
    // never clobber a per-request value.
    const headers = new Headers(init.headers);
    if (this.authToken && !headers.has("Authorization")) {
      headers.set("Authorization", `Bearer ${this.authToken}`);
    }

    return fetch(`${this.baseUrl}${path}`, {
      ...init,
      headers,
      signal: controller.signal,
    }).finally(() => clearTimeout(timer));
  }
}
