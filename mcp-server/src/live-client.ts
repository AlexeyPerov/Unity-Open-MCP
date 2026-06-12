import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import type { MutationEnvelope } from "./gate-error.js";
import { deriveIsError } from "./gate-error.js";

const MAX_COMPILE_WAIT_MS = 120_000;
const COMPILE_POLL_INTERVAL_MS = 2_000;
const PING_TIMEOUT_MS = 5_000;

const DIRECT_RESPONSE_TOOLS: ReadonlySet<string> = new Set([
  "unity_agent_validate_edit",
  "unity_agent_checkpoint_create",
  "unity_agent_delta",
  "unity_agent_find_references",
  "unity_agent_scan_paths",
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

  constructor(port: number) {
    this.baseUrl = `http://127.0.0.1:${port}`;
  }

  async route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    if (toolName === "unity_agent_ping") {
      return this.handlePing();
    }
    return this.handleToolCall(toolName, args);
  }

  private async handlePing(): Promise<CallToolResult> {
    try {
      const res = await this.fetchWithTimeout("/ping", { method: "GET" });
      const body: PingResponse = await res.json();
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

  private async postTool(
    toolName: string,
    args: Record<string, unknown>,
    retryOn503: boolean,
  ): Promise<CallToolResult> {
    try {
      const timeoutMs =
        typeof args.timeout_ms === "number" ? args.timeout_ms : 30_000;
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

  private async waitForCompile(): Promise<CallToolResult | null> {
    const deadline = Date.now() + MAX_COMPILE_WAIT_MS;

    while (Date.now() < deadline) {
      await sleep(COMPILE_POLL_INTERVAL_MS);

      try {
        const res = await this.fetchWithTimeout("/ping", { method: "GET" });

        if (res.status === 503) continue;

        if (!res.ok) continue;

        const body = (await res.json()) as PingResponse;

        if (!body.compiling && body.connected) return null;
      } catch {
        continue;
      }
    }

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
  }

  private fetchWithTimeout(
    path: string,
    init: RequestInit,
    timeoutMs: number = PING_TIMEOUT_MS,
  ): Promise<Response> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    return fetch(`${this.baseUrl}${path}`, {
      ...init,
      signal: controller.signal,
    }).finally(() => clearTimeout(timer));
  }
}
