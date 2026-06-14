import { spawn } from "node:child_process";
import { stat } from "node:fs/promises";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";

const EXECUTE_METHOD = "UnityAgentVerify.Batch.VerifyBatchEntry.Run";
const OUTPUT_BEGIN = "---UNITY_AGENT_VERIFY_JSON_BEGIN---";
const OUTPUT_END = "---UNITY_AGENT_VERIFY_JSON_END---";

const DEFAULT_BATCH_TIMEOUT_MS = 600_000;

const TOOL_TO_OPERATION: Record<string, string> = {
  unity_agent_scan_all: "scan_all",
  unity_agent_baseline_create: "baseline_create",
  unity_agent_regression_check: "regression_check",
};

export const BATCH_TOOL_NAMES = new Set(Object.keys(TOOL_TO_OPERATION));

interface ParsedBatchResult {
  json: Record<string, unknown>;
  exitCode: number;
  elapsedMs: number;
}

function makeErrorResult(code: string, message: string, detail?: unknown): CallToolResult {
  return {
    content: [
      {
        type: "text",
        text: JSON.stringify(detail ?? { error: { code, message } }),
      },
    ],
    isError: true,
  };
}

function extractJson(stdout: string): string | null {
  const beginIdx = stdout.indexOf(OUTPUT_BEGIN);
  if (beginIdx === -1) return null;
  const jsonStart = beginIdx + OUTPUT_BEGIN.length;
  const endIdx = stdout.indexOf(OUTPUT_END, jsonStart);
  if (endIdx === -1) return null;
  return stdout.slice(jsonStart, endIdx).trim();
}

function buildArgs(
  operation: string,
  args: Record<string, unknown>,
): string[] {
  const cli: string[] = [operation];

  if (operation === "scan_all") {
    if (args.platform_profile) cli.push("--platform-profile", String(args.platform_profile));
    if (args.fail_on_severity) cli.push("--fail-on-severity", String(args.fail_on_severity));
    if (args.output_path) cli.push("--output-path", String(args.output_path));
  } else if (operation === "baseline_create") {
    const baselinePath = (args.baseline_path as string) || "CI/unity-agent-baseline.json";
    cli.push("--baseline-path", baselinePath);
    if (args.platform_profile) cli.push("--platform-profile", String(args.platform_profile));
  } else if (operation === "regression_check") {
    cli.push("--baseline-path", String(args.baseline_path));
    if (args.regression_threshold !== undefined)
      cli.push("--regression-threshold", String(args.regression_threshold));
    if (args.platform_profile) cli.push("--platform-profile", String(args.platform_profile));
  }

  return cli;
}

export class BatchSpawn implements Router {
  private unityPath: string;
  private projectPath: string;
  private timeoutMs: number;

  constructor() {
    this.unityPath = process.env.UNITY_PATH ?? "";
    this.projectPath = process.env.UNITY_PROJECT_PATH ?? "";
    this.timeoutMs = process.env.UNITY_AGENT_BATCH_TIMEOUT_MS
      ? parseInt(process.env.UNITY_AGENT_BATCH_TIMEOUT_MS, 10)
      : DEFAULT_BATCH_TIMEOUT_MS;
  }

  isBatchTool(toolName: string): boolean {
    return BATCH_TOOL_NAMES.has(toolName);
  }

  async route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const operation = TOOL_TO_OPERATION[toolName];
    if (!operation) {
      return makeErrorResult(
        "unknown_batch_tool",
        `Tool '${toolName}' is not a batch tool.`,
      );
    }

    const pathError = await this.validateUnityPath();
    if (pathError) return pathError;

    if (!this.projectPath) {
      return makeErrorResult(
        "project_path_missing",
        "UNITY_PROJECT_PATH environment variable is required for batch operations.",
      );
    }

    let parsed: ParsedBatchResult;
    try {
      parsed = await this.spawnUnity(operation, args);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return makeErrorResult("batch_spawn_failed", message);
    }

    const body = parsed.json;
    body.exitCode = parsed.exitCode;
    body._diagnostics = {
      command: this.unityPath,
      elapsedMs: parsed.elapsedMs,
      exitCode: parsed.exitCode,
    };

    const hasError = body.error != null;

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify(body),
        },
      ],
      isError: hasError,
    };
  }

  private async validateUnityPath(): Promise<CallToolResult | null> {
    if (!this.unityPath) {
      return makeErrorResult(
        "unity_path_missing",
        "UNITY_PATH environment variable is required for batch operations. " +
          "Set it to the Unity Editor executable path " +
          "(macOS: /Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity, " +
          "Windows: C:\\Program Files\\Unity\\Hub\\Editor\\<version>\\Editor\\Unity.exe, " +
          "Linux: ~/Unity/Hub/Editor/<version>/Unity).",
      );
    }

    try {
      const s = await stat(this.unityPath);
      if (!s.isFile()) {
        return makeErrorResult(
          "unity_path_invalid",
          `UNITY_PATH '${this.unityPath}' is not a file. ` +
            "Set it to the Unity Editor executable.",
        );
      }
    } catch {
      return makeErrorResult(
        "unity_path_not_found",
        `UNITY_PATH '${this.unityPath}' does not exist or is not accessible. ` +
          "Verify the path points to a valid Unity Editor executable.",
      );
    }

    return null;
  }

  private spawnUnity(
    operation: string,
    args: Record<string, unknown>,
  ): Promise<ParsedBatchResult> {
    return new Promise((resolve, reject) => {
      const toolArgs = buildArgs(operation, args);

      const unityArgs = [
        "-batchmode",
        "-quit",
        "-projectPath", this.projectPath,
        "-executeMethod", EXECUTE_METHOD,
        "--",
        ...toolArgs,
      ];

      console.error(
        `[unity-agent-mcp] Batch spawn: ${this.unityPath} ${unityArgs.join(" ")}`,
      );

      const startTime = Date.now();
      let stdout = "";
      let stderr = "";

      const child = spawn(this.unityPath, unityArgs, {
        stdio: ["ignore", "pipe", "pipe"],
      });

      const timer = setTimeout(() => {
        child.kill("SIGTERM");
        reject(new Error(
          `Batch Unity process timed out after ${this.timeoutMs / 1000}s.`,
        ));
      }, this.timeoutMs);

      child.stdout?.on("data", (chunk: Buffer) => {
        stdout += chunk.toString();
      });

      child.stderr?.on("data", (chunk: Buffer) => {
        stderr += chunk.toString();
      });

      child.on("error", (err) => {
        clearTimeout(timer);
        reject(new Error(
          `Failed to spawn Unity at '${this.unityPath}': ${err.message}`,
        ));
      });

      child.on("close", (code) => {
        clearTimeout(timer);
        const elapsedMs = Date.now() - startTime;
        const exitCode = code ?? 1;

        console.error(
          `[unity-agent-mcp] Batch completed: exit=${exitCode} elapsed=${elapsedMs}ms`,
        );

        const jsonStr = extractJson(stdout);
        if (!jsonStr) {
          const tail = stderr.trim().slice(-500) || stdout.trim().slice(-500);
          reject(new Error(
            `Batch output did not contain JSON markers. Exit code: ${exitCode}.` +
              (tail ? ` Last output: ${tail}` : ""),
          ));
          return;
        }

        let json: Record<string, unknown>;
        try {
          json = JSON.parse(jsonStr);
        } catch {
          reject(new Error(
            `Failed to parse batch JSON output. Exit code: ${exitCode}.`,
          ));
          return;
        }

        json.elapsedMs = elapsedMs;
        resolve({ json, exitCode, elapsedMs });
      });
    });
  }
}
