import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import type { MutationEnvelope } from "./gate-error.js";
import type { PingCache } from "./ping-cache.js";
import { deriveIsError } from "./gate-error.js";
import { existsSync, readFileSync, readdirSync, statSync, unlinkSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import {
  pollAndDismissDialogs,
  readDismissConfig,
  type PollAndDismissOptions,
} from "./dialog-dismiss.js";
import { readInstanceLock, classifyInstance, lockPath, isPidAlive } from "./instance-discovery.js";
import type { InstanceClassification, InstanceLock } from "./instance-discovery.js";
import { makeErrorResult } from "./results.js";
import {
  checkBridgeCompat,
  isVersionCheckSuppressed,
} from "./compat.js";
// M23 Plan 3 — structured retry policy + env-overridable tunables. Replaces
// the hardcoded module constants that previously bounded the compile-wait and
// transient-retry loops. The policy is keyed by lifecycle class (see
// retryConfigFor); the tunables are resolved once at construction.
import {
  readRetryTunables,
  type RetryTunables,
} from "./retry-policy.js";
// M23 Plan 3 — compile-verify failure-code detection (compile_noop /
// dll_stale). Applied as an additive annotation on compile-reload results so
// an agent can branch when a recompile reported success but the compiled state
// did not advance.
import {
  detectCompileVerify,
  buildCompileVerifyAnnotation,
  type CompileVerifySnapshot,
} from "./compile-verify.js";
import { lifecycleFor } from "./capabilities/lifecycle.js";
// M23 Plan 3 — per-process agent identity (sent as X-Agent-Id so the bridge's
// fair round-robin queue can schedule across agents).
import { PROCESS_AGENT_ID } from "./agent-identity.js";

const PING_TIMEOUT_MS = 5_000;

// The bridge's default per-tool wait before IT gives up and returns a timeout
// envelope (packages/bridge/Editor/Bridge/BridgeRequestBody.cs
// DefaultTimeoutMs). The client fetch timeout must never preempt this — if the
// client aborts first it re-POSTs while the bridge is still processing the
// original Work, manufacturing duplicate side-effects (specs/feedback.md entry
// 4B/4A). postTool floors its fetch timeout at BRIDGE_DEFAULT_TIMEOUT_MS +
// slack so a small/absent timeout_ms can't make the client give up before the
// bridge. Kept as a literal here (not imported from the C# side) because the
// bridge assembly isn't readable from the TS server; the cross-reference above
// is the contract — bump both together.
const BRIDGE_DEFAULT_TIMEOUT_MS = 30_000;

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
  // M20 Plan 1 / T20.1.1 — capture_inline returns an inlineImage field (base64
  // PNG). The postTool handler unwraps it into an MCP image content block so
  // the agent receives a viewable image without reading the filesystem.
  "unity_senses_capture_inline",
  "unity_senses_screenshot_camera",
  // M20 Plan 1 / T20.1.2 — EditorWindow capture (file path). Read-only.
  "unity_senses_screenshot_window",
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

const OFFLINE_HINT =
  "Ensure the Unity Editor is open with the Agent Bridge running. " +
  "The bridge port is per-project (20000 + sha256(projectPath) % 10000), not " +
  "fixed — if Unity is open the MCP server may be aimed at the wrong port. " +
  "Check the instance lock at ~/.unity-open-mcp/instances/<sha256(projectPath)>.json " +
  "for the live port/pid, or set UNITY_OPEN_MCP_BRIDGE_PORT. If Unity is not " +
  "open, launch it (or set UNITY_PATH + UNITY_PROJECT_PATH for batch fallback).";

/**
 * Build a per-instance offline hint that names THIS project's lock file path
 * and (when the lock is readable) its port/pid/state, so an agent debugging a
 * `bridge_offline` knows exactly where to look. Falls back to OFFLINE_HINT
 * when the project path is unknown (older callers / tests).
 */
function buildOfflineHint(projectPath: string | undefined): string {
  if (!projectPath) return OFFLINE_HINT;
  const lock = lockPath(projectPath);
  const base = `${OFFLINE_HINT} This project's lock file: ${lock}`;
  try {
    const inst = readInstanceLock(projectPath);
    if (inst) {
      return `${base}. Lock state: pid=${inst.pid}, port=${inst.port}, state=${inst.state}`;
    }
  } catch {
    // best-effort — fall through to the base hint
  }
  return `${base} (lock not readable — Unity may not be running).`;
}

export class LiveClient implements Router {
  private baseUrl: string;
  private pingCache: PingCache;
  private dismissEnabled: boolean;
  private dismissTimeoutMs: number;
  private dismissIntervalMs: number;
  /** M23 Plan 2 — active dialog policy (auto/manual/ignore/recover/safe-mode/
   *  cancel). Stored so waitForCompile can thread it into the dismiss loop
   *  without re-reading the env on every stall. */
  private dialogPolicy: import("./dialog-policy.js").DialogPolicy;
  /** M23 Plan 2 — opt-in for the Project Upgrade Required dialog (irreversible,
   *  off by default). */
  private allowProjectUpgrade: boolean;
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
  /** One-shot guard so the version-compat warning (server vs bridge) is emitted
   *  at most once per process — every /ping would otherwise re-warn. */
  private compatWarned: boolean;
  /** M23 Plan 3 — env-overridable retry/compile-wait tunables. Resolved once
   *  at construction (the env does not change mid-process). Replaces the
   *  hardcoded MAX_COMPILE_WAIT_MS / COMPILE_POLL_INTERVAL_MS /
   *  TRANSIENT_RETRY_* module constants. */
  private retry: RetryTunables;
  /** M23 Plan 3 — agent identity sent as X-Agent-Id on every request so the
   *  bridge's fair round-robin queue can schedule across agents sharing one
   *  bridge. Defaults to the process-wide id; a transient LiveClient built for
   *  a per-request port override may carry a per-call id. */
  private agentId: string;
  /** Parsed UNITY_OPEN_MCP_BRIDGE_PORT, or undefined. When set, the env
   *  override is authoritative and refreshEndpointFromLock() is a no-op (there
   *  is no lock to read — matches resolvePort/resolveAuthToken semantics).
   *  Stored so a mid-session refresh can respect the same override precedence
   *  the constructor used; without it a refresh would silently switch from an
   *  env-pinned port to the lock port. */
  private readonly envPort: number | undefined;

  constructor(
    port: number,
    pingCache: PingCache,
    authToken?: string,
    projectPath?: string,
    agentId: string = PROCESS_AGENT_ID,
    envPort?: number,
  ) {
    this.baseUrl = `http://127.0.0.1:${port}`;
    this.pingCache = pingCache;
    this.authToken = authToken;
    this.projectPath = projectPath;
    this.retry = readRetryTunables();
    this.agentId = agentId;
    this.envPort = envPort;
    // M13 T4.5 + M23 Plan 2 — startup dialog auto-dismissal. Resolved once at
    // construction; the env vars do not change mid-process. The feature is
    // enabled by default and runs concurrently with every compile/bridge
    // readiness wait so it ticks on the same stall points agents hit, not
    // only at process spawn. M23 Plan 2 generalizes T4.5's single Ignore
    // click into the full 6-variant policy taxonomy (dialog-policy.ts) so
    // different workflows can pick different buttons on the same dialog.
    const dismissCfg = readDismissConfig();
    this.dismissEnabled = dismissCfg.enabled;
    this.dismissTimeoutMs = dismissCfg.timeoutMs;
    this.dismissIntervalMs = dismissCfg.intervalMs;
    this.dialogPolicy = dismissCfg.policy;
    this.allowProjectUpgrade = dismissCfg.allowProjectUpgrade;
    this.compatWarned = false;
  }

  /**
   * Emit the server/bridge version-compatibility warning at most once per
   * process. Advisory only — the caller never blocks on this. Suppressed
   * entirely by UNITY_OPEN_MCP_SKIP_VERSION_CHECK=1. See docs/versioning.md.
   */
  private maybeWarnCompat(bridgeVersion: string | undefined): void {
    if (this.compatWarned) return;
    if (bridgeVersion === undefined || bridgeVersion === null) return;
    if (isVersionCheckSuppressed()) {
      this.compatWarned = true;
      return;
    }
    const result = checkBridgeCompat(String(bridgeVersion));
    this.compatWarned = true;
    if (result.message) {
      console.warn(result.message);
    }
  }

  async isLiveAvailable(): Promise<boolean> {
    try {
      const res = await this.fetchWithTimeout("/ping", { method: "GET" });
      if (res.status === 503) return true;
      if (!res.ok) return false;
      const body = (await res.json()) as PingResponse;
      this.pingCache.record(body);
      this.maybeWarnCompat(body.bridgeVersion);
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
      this.maybeWarnCompat(body.bridgeVersion);
      return {
        content: [{ type: "text", text: JSON.stringify(body) }],
        isError: false,
      };
    } catch {
      return makeErrorResult({
        code: "bridge_offline",
        message: `Bridge is not reachable at ${this.baseUrl}. ${buildOfflineHint(this.projectPath)}`,
        detail: {
          error: {
            code: "bridge_offline",
            message: `Cannot connect to bridge at ${this.baseUrl}`,
          },
        },
      });
    }
  }

  private async handleToolCall(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const readyError = await this.ensureReady();
    if (readyError) return readyError;

    // M23 Plan 3 — compile-verify annotation. For compile-reload tools, capture
    // a before-snapshot (bridge tool count + newest ScriptAssemblies DLL mtime)
    // so the post-call detectCompileVerify() can flag a no-op/stale compile.
    // Non-compile-reload tools skip this entirely (no extra /tools round-trip,
    // no fs scan). The annotation is additive: it never blocks a success.
    const isCompileReload = lifecycleFor(toolName).class === "compile-reload";
    const before = isCompileReload ? await this.captureCompileSnapshot() : null;

    const result = await this.postTool(toolName, args, true);

    if (isCompileReload && !result.isError && before !== null) {
      return this.annotateCompileVerify(toolName, result, before, args);
    }
    return result;
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

    return makeErrorResult({
      code: "test_results_timeout",
      message: `Test results for run ${runId} were not available within ` +
        `${timeoutMs / 1000}s. The test run may still be in progress or ` +
        "the bridge may have lost the callback.",
      detail: {
        error: {
          code: "test_results_timeout",
          message: `Test results poll timed out after ${timeoutMs / 1000}s`,
        },
      },
    });
  }

  private async postTool(
    toolName: string,
    args: Record<string, unknown>,
    retryOn503: boolean,
  ): Promise<CallToolResult> {
    try {
      const timeoutMs =
        typeof args.timeout_ms === "number" ? args.timeout_ms : 60_000;
      // Floor the client fetch timeout at the bridge's own default wait + slack
      // so the client never aborts before the bridge has had a chance to return
      // its timeout envelope. Without this floor, a small/absent timeout_ms
      // (MCP hosts often omit the field → schema default 30000, or smaller)
      // makes the client re-POST while the bridge is still processing →
      // duplicate mutations (specs/feedback.md entry 4B). An explicit large
      // timeout_ms still wins via the Math.max.
      const fetchTimeout = Math.max(
        timeoutMs + 10_000,
        BRIDGE_DEFAULT_TIMEOUT_MS + 10_000,
      );
      const res = await this.fetchWithTimeout(
        `/tools/${toolName}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json; charset=utf-8" },
          body: JSON.stringify(args),
        },
        fetchTimeout,
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
        return makeErrorResult({
          code: "bridge_http_error",
          message: body?.error?.message ?? `Bridge returned HTTP ${res.status}`,
          detail: body ?? {
            error: {
              code: "bridge_http_error",
              message: `HTTP ${res.status}`,
            },
          },
        });
      }

      if (DIRECT_RESPONSE_TOOLS.has(toolName)) {
        const directBody = (await res.json().catch(() => null)) as Record<
          string,
          unknown
        > | null;

        // M20 Plan 1 / T20.1.1 — capture_inline returns an `inlineImage` base64
        // PNG field. Unwrap it into an MCP image content block alongside a text
        // metadata block ([{type: image}, {type: text}]). The image carries the
        // same viewable payload a file-path screenshot would; the metadata keeps
        // the view / resolution / byteLength fields without the base64 blob.
        const inlineImage = directBody?.inlineImage;
        if (
          directBody != null &&
          typeof inlineImage === "string" &&
          inlineImage.length > 0 &&
          directBody.error == null
        ) {
          const body: Record<string, unknown> = directBody;
          const mimeType =
            typeof body.mimeType === "string" ? body.mimeType : "image/png";
          const metadata = { ...body };
          delete metadata.inlineImage;
          return {
            content: [
              { type: "image", data: inlineImage, mimeType },
              { type: "text", text: JSON.stringify(metadata) },
            ],
            isError: false,
          };
        }

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
      // M20 Plan 4-5 / T-fix-2 — transient POST failure (ECONNREFUSED /
      // timeout during a domain reload). Classify + recover before declaring
      // offline. handleTransientOffline re-syncs the cached endpoint from the
      // lock first (so a Unity restart self-heals).
      //
      // Re-POST discipline (specs/feedback.md entry 4A — duplicate side-effects):
      // only re-POST when the endpoint CHANGED during recovery (a restart → new
      // bridge that never saw the original POST, so a retry is safe). When the
      // endpoint is unchanged the same live bridge is still processing — or has
      // queued — the original POST's Work; re-POSTing would run the mutation a
      // second time. In that case surface the timeout instead of retrying.
      const endpointBefore = this.baseUrl;
      const recovered = await this.handleTransientOffline("post");
      if (recovered !== null) return recovered;
      const endpointChanged = this.baseUrl !== endpointBefore;
      if (endpointChanged) {
        return this.postTool(toolName, args, retryOn503);
      }
      return makeErrorResult({
        code: "bridge_offline",
        message:
          `Tool '${toolName}' did not return from the bridge within the client ` +
          `timeout. The bridge endpoint did not change (no restart detected), so ` +
          `the request was NOT retried — re-POSTing would risk running the ` +
          `mutation twice on the same bridge. If the editor was briefly slow ` +
          `(unfocused, GC, a long main-thread op), the original call may still ` +
          `complete there; verify the effect before retrying. Endpoint: ${this.baseUrl}.`,
        detail: {
          error: {
            code: "bridge_offline",
            message: `Client timeout against unchanged endpoint ${this.baseUrl}; not retried to avoid duplicate side-effects.`,
          },
        },
      });
    }
  }

  private async ensureReady(): Promise<CallToolResult | null> {
    try {
      const res = await this.fetchWithTimeout("/ping", { method: "GET" });

      if (res.status === 503) {
        return this.waitForCompile();
      }

      if (!res.ok) {
        return makeErrorResult({
          code: "bridge_error",
          message: `Bridge /ping returned unexpected HTTP ${res.status}`,
        });
      }

      const body = (await res.json()) as PingResponse;
      this.pingCache.record(body);

      if (!body.connected) {
        return makeErrorResult({
          code: "bridge_not_connected",
          message: "Bridge listener is running but session is not initialized.",
          detail: {
            error: {
              code: "bridge_not_connected",
              message: "Bridge session not connected",
            },
          },
        });
      }

      if (body.compiling) {
        return this.waitForCompile();
      }

      return null;
    } catch {
      // Bridge is unreachable (ECONNREFUSED / timeout). Before falling back to
      // the generic offline error, classify the failure via the instance lock:
      //   - dead_bridge    → bridge assembly failed to recompile (fail fast)
      //   - reloading/compiling → a normal domain reload is in flight; wait it
      //     out (the listener socket is torn down for the reload duration) then
      //     re-probe once before declaring offline (T-fix-2)
      //   - otherwise      → retry with backoff a few times, then offline
      // handleTransientOffline refreshes the cached endpoint from the lock
      // first, so a Unity restart (new PID/port) re-points this client before
      // the retry probes (specs/feedback.md entry 2).
      const recovered = await this.handleTransientOffline("ping");
      if (recovered !== null) return recovered;

      // Recovery returned null ("recovered" — a /ping probe succeeded during
      // handleTransientOffline, possibly against a freshly-refreshed endpoint
      // after a restart). Re-probe /ping ONCE on the (possibly new) endpoint
      // before declaring offline: without this, a restart that refreshed the
      // endpoint mid-recovery would still surface bridge_offline here, and the
      // caller (handleToolCall) treats any non-null ensureReady result as a
      // terminal error — so the next tool call would fail despite the bridge
      // being back. The single re-probe closes that gap.
      try {
        const retryRes = await this.fetchWithTimeout("/ping", { method: "GET" });
        if (retryRes.status === 503) return this.waitForCompile();
        if (retryRes.ok) {
          const retryBody = (await retryRes.json()) as PingResponse;
          this.pingCache.record(retryBody);
          if (retryBody.compiling) return this.waitForCompile();
          if (retryBody.connected) return null; // recovered against the new endpoint
        }
      } catch {
        // Re-probe also failed — fall through to the offline error below.
      }

      return makeErrorResult({
        code: "bridge_offline",
        message: `Bridge is not reachable at ${this.baseUrl}. ${buildOfflineHint(this.projectPath)}`,
        detail: {
          error: {
            code: "bridge_offline",
            message: `Cannot connect to bridge at ${this.baseUrl}`,
          },
        },
      });
    }
  }

  /**
   * M20 Plan 4-5 / T-fix-2 — classify a transient /ping or tool-POST failure
   * (ECONNREFUSED / timeout / socket reset) via the instance lock and either
   * recover or surface the right error.
   *
   * Returns:
   *   - a structured `bridge_compile_failed` result when the lock shows a dead
   *     bridge assembly (fail fast — preserves the pre-existing fast-path),
   *   - `null` to signal "recovered — the caller should retry its operation"
   *     after either waiting out a `reloading`/`compiling` lock window or
   *     succeeding a bounded backoff retry,
   *   - a `bridge_offline` result when the bridge is genuinely unreachable after
   *     the recovery attempts (so the caller's outer `bridge_offline` fallback
   *     is reached only via this returned result).
   *
   * `operation` is "ping" | "post" and only affects the log line — both paths
   * share the same recovery logic.
   */
  private async handleTransientOffline(
    operation: "ping" | "post",
  ): Promise<CallToolResult | null> {
    // Before any classification/retry, re-sync the cached endpoint to the
    // current live bridge. A Unity restart writes a fresh PID/port/authToken
    // to the lock; without this refresh every retry below would keep hitting
    // the dead cached listener (specs/feedback.md entry 2) and, on the POST
    // path, re-dispatch the Work to the new bridge (duplicate side-effects,
    // entry 4A). refreshEndpointFromLock is a no-op when an env-port override
    // is in force or nothing has changed.
    this.refreshEndpointFromLock();

    // Dead bridge assembly — not recoverable by waiting. Fail fast so the
    // agent can fetch compile errors instead of hanging on /ping.
    const deadBridge = this.deadBridgeResult();
    if (deadBridge) return deadBridge;

    const classification = this.classifyLockNow();

    // A normal domain reload in flight: the listener socket is torn down for
    // the reload duration (BridgeHttpServer.OnBeforeAssemblyReload →
    // _listener.Stop()). classifyInstance folds both lock states
    // "reloading" and "compiling" into the "reloading" classification
    // (instance-discovery.ts) — so a single check covers the whole compile +
    // reload window. Wait it out, then signal the caller to retry.
    if (classification === "reloading") {
      const waitError = await this.waitForCompile();
      if (waitError) return waitError; // timed out or dead_bridge mid-wait
      return null; // recovered — caller retries the original operation
    }

    // No reload signal but a transient refusal/reset/timeout. Retry a bounded
    // number of times with backoff before declaring offline. Covers brief
    // socket churn that isn't reflected in the lock yet. The bounds come from
    // the env-overridable retry tunables (M23 Plan 3 / retry-policy.ts).
    const maxAttempts = this.retry.transientRetryAttempts;
    const backoffMs = this.retry.transientBackoffMs;
    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      await sleep(backoffMs * attempt);
      // Re-classify on each attempt: a reload may have begun between the
      // first failure and now.
      const reclass = this.classifyLockNow();
      if (reclass === "dead_bridge") return this.deadBridgeResult();
      if (reclass === "reloading") {
        const waitError = await this.waitForCompile();
        if (waitError) return waitError;
        return null;
      }
      // Probe whether the listener is back up.
      try {
        const res = await this.fetchWithTimeout("/ping", { method: "GET" });
        if (res.ok || res.status === 503) {
          // Listener responded — recovered. Caller re-runs its readiness /
          // POST. (503 still counts: it means compiling, handled on retry.)
          return null;
        }
      } catch {
        // Still down — loop and back off again.
      }
    }
    void operation; // log-only parameter, reserved for future telemetry
    const retryMsg = `Bridge is not reachable at ${this.baseUrl} after ${maxAttempts} retries. ${buildOfflineHint(this.projectPath)}`;
    return makeErrorResult({
      code: "bridge_offline",
      message: retryMsg,
      detail: {
        error: {
          code: "bridge_offline",
          message: retryMsg,
        },
      },
    });
  }

  /**
   * Re-resolve the bridge endpoint (port + authToken) from the on-disk instance
   * lock and update the cached {@link baseUrl} / {@link authToken} when the
   * live bridge has moved. Returns true when the endpoint changed.
   *
   * This is the single fix for two coupled failure modes (specs/feedback.md
   * entries 2 + 4A): the client cached the port/token once at construction,
   * so a Unity restart (new PID/port/authToken in the lock) left every retry
   * hitting a dead listener — producing minutes of `bridge_offline` *and*
   * (because {@link postTool} re-POSTs on recovery) duplicate side-effects
   * once the editor came back. Refreshing here lets a restart self-heal.
   *
   * No-op (returns false) when there is no projectPath, an env-port override
   * is in force (env is authoritative — no lock to read, matching
   * resolvePort/resolveAuthToken), or the lock is missing/its PID is dead/
   * nothing actually changed. Never throws — readInstanceLock/isPidAlive are
   * already fault-tolerant.
   */
  private refreshEndpointFromLock(): boolean {
    if (!this.projectPath) return false;
    if (typeof this.envPort === "number") return false; // env override wins
    let lock: InstanceLock | null;
    try {
      lock = readInstanceLock(this.projectPath);
    } catch {
      return false;
    }
    if (!lock) return false;
    if (!isPidAlive(lock.pid)) return false;
    const portChanged =
      typeof lock.port === "number" &&
      lock.port > 0 &&
      this.baseUrl !== `http://127.0.0.1:${lock.port}`;
    const tokenChanged = lock.authToken !== this.authToken;
    if (!portChanged && !tokenChanged) return false;
    if (portChanged) this.baseUrl = `http://127.0.0.1:${lock.port}`;
    if (tokenChanged) this.authToken = lock.authToken;
    return true;
  }

  /**
   * Read + classify the instance lock at the current moment, without throwing.
   * Returns "gone" (treated as no-signal) when the lock can't be read or the
   * project path is unknown. T-fix-2 helper.
   */
  private classifyLockNow(): InstanceClassification {
    if (!this.projectPath) return "gone";
    try {
      return classifyInstance(readInstanceLock(this.projectPath));
    } catch {
      return "gone";
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
    const compileWaitMs = this.retry.compileWaitMs;
    const deadline = Date.now() + compileWaitMs;

    // M13 T4.5 — run the launch-errors / Safe Mode dialog auto-dismiss loop
    // CONCURRENTLY with the compile poll. The dismiss loop ticks on the same
    // stall point this method represents: if a native modal is blocking the
    // Editor, the compile poll below will spin until timeout with no recovery.
    // The dismiss loop clicks Ignore on that modal; the moment compile
    // resolves (compiling → idle), we abort the loop — there is no launch
    // dialog left to dismiss once Unity is idle.
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
        policy: this.dialogPolicy,
        allowProjectUpgrade: this.allowProjectUpgrade,
        abortSignal: dismissAbort.signal,
      };
      dismissDone = this.runDismissLoop(dismissOpts);
    }

    try {
      while (Date.now() < deadline) {
        await sleep(this.retry.compilePollIntervalMs);

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

      return makeErrorResult({
        code: "compile_timeout",
        message:
          `Unity is still compiling after ${compileWaitMs / 1000}s. ` +
          "The compile-wait timeout was exceeded.",
        detail: {
          error: {
            code: "compile_timeout",
            message: `Compile-wait exceeded ${compileWaitMs / 1000}s`,
          },
        },
      });
    } finally {
      // Compile wait resolved (idle) OR timed out — either way there is
      // nothing more to dismiss. Aborting unblocks the dismiss loop's sleep.
      dismissAbort?.abort();
      await dismissDone;
    }
  }

  /**
   * Run the startup-dialog dismiss polling loop. Indirected through a method
   * so unit tests can stub the OS-clicking loop without invoking
   * PowerShell / osascript / xdotool. Production uses the real
   * `pollAndDismissDialogs`.
   */
  protected runDismissLoop(opts: PollAndDismissOptions): Promise<void> {
    return pollAndDismissDialogs(opts);
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

  /**
   * M18 Plan 2 / T18.2.3 — fetch the compiled-state tool inventory from the
   * bridge (`GET /tools`). Returns the set of tool names the bridge compiled
   * in (KnownTools ∪ BridgeToolRegistry), so the MCP server can report per-
   * group availability in `unity_open_mcp_capabilities` and
   * `unity_open_mcp_manage_tools(list_groups)`.
   *
   * Returns null when the bridge is unreachable or the response is malformed
   * — callers fall back to `available: null` (unknown) so the agent is
   * directed at manage_tools when the bridge comes back up.
   */
  async listBridgeTools(): Promise<{
    tools: Set<string>;
    groups: Array<{ id: string; tools: string[] }>;
  } | null> {
    try {
      const res = await this.fetchWithTimeout(
        "/tools",
        { method: "GET" },
        5_000,
      );
      if (!res.ok) return null;
      const body = (await res.json()) as {
        tools?: string[];
        groups?: Array<{ id: string; tools: string[] }>;
      };
      const tools = new Set<string>(Array.isArray(body.tools) ? body.tools : []);
      const groups = Array.isArray(body.groups) ? body.groups : [];
      return { tools, groups };
    } catch {
      return null;
    }
  }

  // -------------------------------------------------------------------------
  // M23 Plan 3 — compile-verify annotation (compile_noop / dll_stale).
  //
  // For compile-reload tools we capture a before/after snapshot and let the
  // pure detectCompileVerify() flag a no-op or stale compile. The annotation
  // is additive (`_compileVerify` on the result body) — it never blocks a
  // successful response, only surfaces a structured signal so an agent can
  // branch instead of trusting a no-op success. Snapshots degrade gracefully:
  // if the bridge inventory or DLL mtimes can't be read, the fields are
  // undefined and the detector returns null (no false positive).
  // -------------------------------------------------------------------------

  /**
   * Capture a compile-verify snapshot: the bridge tool inventory count + the
   * newest mtime under Library/ScriptAssemblies. Returns undefined fields
   * (never throws) when the bridge or filesystem is unavailable.
   */
  private async captureCompileSnapshot(): Promise<CompileVerifySnapshot> {
    const snap: CompileVerifySnapshot = {};
    try {
      const inventory = await this.listBridgeTools();
      if (inventory) snap.bridgeToolCount = inventory.tools.size;
    } catch {
      // best-effort — undefined count is a valid "unknown" snapshot
    }
    const dllMtime = this.newestScriptAssembliesMtime();
    if (dllMtime !== undefined) snap.dllMtimeMs = dllMtime;
    return snap;
  }

  /**
   * Newest mtime (epoch ms) of `Library/ScriptAssemblies/*.dll` for this
   * project, or undefined when the directory is missing/unreadable. Used by
   * the compile-verify detector to tell whether a recompile actually advanced
   * the compiled output.
   */
  private newestScriptAssembliesMtime(): number | undefined {
    if (!this.projectPath) return undefined;
    const dir = join(this.projectPath, "Library", "ScriptAssemblies");
    let entries: string[];
    try {
      entries = readdirSync(dir);
    } catch {
      return undefined;
    }
    let newest: number | undefined;
    for (const name of entries) {
      if (!name.endsWith(".dll")) continue;
      try {
        const m = statSync(join(dir, name)).mtimeMs;
        if (newest === undefined || m > newest) newest = m;
      } catch {
        // best-effort per file
      }
    }
    return newest;
  }

  /**
   * Apply the compile-verify annotation to a successful compile-reload result.
   * Reads the after-snapshot, runs the pure detector, and — when flagged —
   * injects `_compileVerify: { code, recommendation }` into the result body.
   * The source-edit mtime is inferred from `args._sourceMtimeMs` when the
   * caller (e.g. script_write) supplied it; otherwise only the no-op path
   * (count + dll-mtime delta) is evaluated.
   */
  private async annotateCompileVerify(
    _toolName: string,
    result: CallToolResult,
    before: CompileVerifySnapshot,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const after = await this.captureCompileSnapshot();
    const sourceMtimeMs =
      typeof args._sourceMtimeMs === "number"
        ? (args._sourceMtimeMs as number)
        : undefined;

    const detection = detectCompileVerify({ before, after, sourceMtimeMs });
    const annotation = buildCompileVerifyAnnotation(detection);
    if (annotation === null) return result;

    // Inject into the first text content block's JSON body. Mirrors the
    // injectRouteMeta shape in tool-router.ts.
    const textIndex = result.content.findIndex((c) => c.type === "text");
    if (textIndex < 0) return result;
    const block = result.content[textIndex];
    if (block.type !== "text") return result;
    try {
      const body = JSON.parse(block.text) as Record<string, unknown>;
      body._compileVerify = annotation;
      const newContent = result.content.slice();
      newContent[textIndex] = { type: "text", text: JSON.stringify(body) };
      return { ...result, content: newContent };
    } catch {
      return result;
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
    // M23 Plan 3 — attach the agent identity so the bridge's fair round-robin
    // queue can schedule across agents sharing one bridge. Merge so a caller-
    // supplied header (rare) wins.
    if (this.agentId && !headers.has("X-Agent-Id")) {
      headers.set("X-Agent-Id", this.agentId);
    }

    return fetch(`${this.baseUrl}${path}`, {
      ...init,
      headers,
      signal: controller.signal,
    }).finally(() => clearTimeout(timer));
  }
}
