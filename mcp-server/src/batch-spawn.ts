import { spawn } from "node:child_process";
import { stat } from "node:fs/promises";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";

const VERIFY_EXECUTE_METHOD = "UnityOpenMcpVerify.Batch.VerifyBatchEntry.Run";
const BRIDGE_EXECUTE_METHOD = "UnityOpenMcpBridge.Batch.BridgeBatchEntry.Run";
const OUTPUT_BEGIN = "---UNITY_OPEN_MCP_VERIFY_JSON_BEGIN---";
const OUTPUT_END = "---UNITY_OPEN_MCP_VERIFY_JSON_END---";

const DEFAULT_BATCH_TIMEOUT_MS = 600_000;

const VERIFY_TOOL_TO_OPERATION: Record<string, string> = {
  unity_open_mcp_scan_all: "scan_all",
  unity_open_mcp_baseline_create: "baseline_create",
  unity_open_mcp_regression_check: "regression_check",
};

const META_TOOL_TO_OPERATION: Record<string, string> = {
  unity_open_mcp_find_members: "find_members",
};

const LIMITED_META_TOOLS: ReadonlySet<string> = new Set([
  "unity_open_mcp_execute_csharp",
  "unity_open_mcp_invoke_method",
  "unity_open_mcp_execute_menu",
]);

export const BATCH_TOOL_NAMES = new Set([
  ...Object.keys(VERIFY_TOOL_TO_OPERATION),
  ...Object.keys(META_TOOL_TO_OPERATION),
  ...LIMITED_META_TOOLS,
]);

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

function buildVerifyArgs(
  operation: string,
  args: Record<string, unknown>,
): string[] {
  const cli: string[] = [operation];

  if (operation === "scan_all") {
    if (args.platform_profile) cli.push("--platform-profile", String(args.platform_profile));
    if (args.fail_on_severity) cli.push("--fail-on-severity", String(args.fail_on_severity));
    if (args.output_path) cli.push("--output-path", String(args.output_path));
  } else if (operation === "baseline_create") {
    const baselinePath = (args.baseline_path as string) || "CI/unity-open-mcp-baseline.json";
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

export function buildMetaArgs(
  operation: string,
  args: Record<string, unknown>,
): string[] {
  const cli: string[] = [operation];

  if (operation === "find_members") {
    if (args.query !== undefined) cli.push("--query", String(args.query));
    if (args.kind !== undefined) cli.push("--kind", String(args.kind));
    if (args.assembly_filter !== undefined) cli.push("--assembly-filter", String(args.assembly_filter));
    if (args.include_unity_editor !== undefined) cli.push("--include-unity-editor", String(args.include_unity_editor));
    if (args.include_project !== undefined) cli.push("--include-project", String(args.include_project));
    if (args.max_results !== undefined) cli.push("--max-results", String(args.max_results));
  }

  return cli;
}

const LIMITED_META_MESSAGES: Record<string, string> = {
  unity_open_mcp_execute_csharp:
    "execute_csharp is not supported in batch mode. " +
    "The gate (checkpoint, validate, delta) cannot run headless and Roslyn compilation " +
    "without a live Editor is unreliable. Connect a live Editor for full support. " +
    "Only find_members is available in batch mode.",
  unity_open_mcp_invoke_method:
    "invoke_method is not supported in batch mode. " +
    "Mutating method calls require gate enforcement which is unavailable headless, " +
    "and instance methods may depend on Editor-only state. " +
    "Connect a live Editor for full support. " +
    "Only find_members is available in batch mode.",
  unity_open_mcp_execute_menu:
    "execute_menu is not supported in batch mode. " +
    "Menu execution requires a live Editor UI; most menus fail in -batchmode. " +
    "Connect a live Editor for full support. " +
    "Only find_members is available in batch mode.",
};

export class BatchSpawn implements Router {
  private unityPath: string;
  private projectPath: string;
  private timeoutMs: number;

  constructor() {
    this.unityPath = process.env.UNITY_PATH ?? "";
    this.projectPath = process.env.UNITY_PROJECT_PATH ?? "";
    this.timeoutMs = process.env.UNITY_OPEN_MCP_BATCH_TIMEOUT_MS
      ? parseInt(process.env.UNITY_OPEN_MCP_BATCH_TIMEOUT_MS, 10)
      : DEFAULT_BATCH_TIMEOUT_MS;
  }

  isBatchTool(toolName: string): boolean {
    return BATCH_TOOL_NAMES.has(toolName);
  }

  async route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    if (LIMITED_META_TOOLS.has(toolName)) {
      return makeErrorResult(
        "batch_not_supported",
        LIMITED_META_MESSAGES[toolName] ??
          `${toolName} is not supported in batch mode.`,
      );
    }

    const verifyOperation = VERIFY_TOOL_TO_OPERATION[toolName];
    const metaOperation = META_TOOL_TO_OPERATION[toolName];

    if (!verifyOperation && !metaOperation) {
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

    const executeMethod = verifyOperation
      ? VERIFY_EXECUTE_METHOD
      : BRIDGE_EXECUTE_METHOD;
    const operation = verifyOperation ?? metaOperation;
    const argBuilder = verifyOperation ? buildVerifyArgs : buildMetaArgs;

    let parsed: ParsedBatchResult;
    try {
      parsed = await this.spawnUnity(operation, args, executeMethod, argBuilder);
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

    const mutation = body.mutation as Record<string, unknown> | undefined;
    const hasError =
      body.error != null ||
      (mutation != null && mutation.success === false);

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
    executeMethod: string,
    argBuilder: (operation: string, args: Record<string, unknown>) => string[],
  ): Promise<ParsedBatchResult> {
    return new Promise((resolve, reject) => {
      const toolArgs = argBuilder(operation, args);

      const unityArgs = [
        "-batchmode",
        "-quit",
        "-projectPath", this.projectPath,
        "-executeMethod", executeMethod,
        "--",
        ...toolArgs,
      ];

      console.error(
        `[unity-open-mcp] Batch spawn: ${this.unityPath} ${unityArgs.join(" ")}`,
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
          `[unity-open-mcp] Batch completed: exit=${exitCode} elapsed=${elapsedMs}ms`,
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
