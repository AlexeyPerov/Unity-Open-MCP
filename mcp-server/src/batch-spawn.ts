import { spawn } from "node:child_process";
import { stat } from "node:fs/promises";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import { resolveUnityPath, scannedHubRoots } from "./unity-install-discovery.js";
import { readInstanceLock } from "./instance-discovery.js";
import { makeErrorResult } from "./results.js";

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
  unity_open_mcp_compile_check: "compile_check",
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

function extractJson(stdout: string): string | null {
  const beginIdx = stdout.indexOf(OUTPUT_BEGIN);
  if (beginIdx === -1) return null;
  const jsonStart = beginIdx + OUTPUT_BEGIN.length;
  const endIdx = stdout.indexOf(OUTPUT_END, jsonStart);
  if (endIdx === -1) return null;
  return stdout.slice(jsonStart, endIdx).trim();
}

// extractCompilerErrors is shared with the offline read_compile_errors tool —
// see compiler-errors.ts. Imported for the local call site below and
// re-exported so existing callers/tests that import it from batch-spawn keep
// working. Captures CSxxxx lines from raw Unity compiler output (Editor.log /
// batch stdout) when the bridge assembly itself fails to compile and the JSON
// markers never print.
import { extractCompilerErrors } from "./compiler-errors.js";
export { extractCompilerErrors };

export function buildVerifyArgs(
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
    // Per-category thresholds are an optional object: ruleId -> max delta.
    // Repeatable --per-category-threshold <ruleId>=<int> flags are emitted in a
    // stable key order so the spawn line is deterministic.
    const perCategory = args.per_category_thresholds;
    if (perCategory && typeof perCategory === "object") {
      const entries = Object.entries(perCategory).sort(([a], [b]) => a.localeCompare(b));
      for (const [ruleId, value] of entries) {
        if (typeof value === "number" && Number.isFinite(value) && value >= 0) {
          cli.push("--per-category-threshold", `${ruleId}=${Math.trunc(value)}`);
        }
      }
    }
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
  } else if (operation === "compile_check") {
    if (args.timeout_ms !== undefined) cli.push("--timeout-ms", String(args.timeout_ms));
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

// M22 Plan 3 / T-fix-2 — classify a batch failure tail before falling back
// to the generic `batch_spawn_failed`. Unity's one-Editor-per-project lock
// produces a recognizable signature when a live Editor already holds the
// project open; surfacing `editor_instance_locked` lets an agent tell that
// situation apart from a genuine compile/spawn failure and act on it (close
// the live Editor, or use live introspection instead of a headless spawn).
//
// Returns the error code to emit, or null when the tail does not match a
// known classification (caller falls back to batch_spawn_failed).
const PROJECT_LOCK_PATTERN = /another unity instance|already open/i;
export function classifyBatchFailure(combined: string): string | null {
  if (typeof combined !== "string" || combined.length === 0) return null;
  if (PROJECT_LOCK_PATTERN.test(combined)) return "editor_instance_locked";
  return null;
}

// Error carrying a classified code so route()'s catch can emit a targeted
// error result instead of the generic batch_spawn_failed. Thrown by the
// spawn close-handler when classifyBatchFailure matches the tail.
export class BatchClassificationError extends Error {
  readonly code: string;
  constructor(code: string, message: string) {
    super(message);
    this.name = "BatchClassificationError";
    this.code = code;
  }
}

export interface BatchSpawnOptions {
  /**
   * Optional override for the Unity-install discovery roots (test hook).
   * When omitted, the real OS-default Hub paths (+ UNITY_HUB env override)
   * are scanned. Pass an empty array to force the "nothing discovered"
   * path in tests.
   */
  discoveryRoots?: string[];
  /**
   * Optional explicit project path (test hook). When omitted, falls back to
   * UNITY_PROJECT_PATH env var, then the instance lock's projectPath.
   */
  projectPath?: string;
}

export class BatchSpawn implements Router {
  private unityPath: string;
  private unityPathSource: "env" | "discovered" | "none";
  private projectPath: string;
  private timeoutMs: number;
  private readonly discoveryRoots?: string[];

  constructor(options: BatchSpawnOptions = {}) {
    this.discoveryRoots = options.discoveryRoots;

    // UNITY_PATH env (validated, wins) -> auto-discovered install (preferred
    // version from the running bridge's lock when available) -> none.
    const lock = readInstanceLock(options.projectPath ?? process.env.UNITY_PROJECT_PATH ?? "");
    const resolved = resolveUnityPath(lock?.unityVersion, this.discoveryRoots);
    if (resolved) {
      this.unityPath = resolved.path;
      this.unityPathSource = resolved.source;
      if (resolved.source === "discovered") {
        console.error(
          `[unity-open-mcp] Unity path auto-discovered: ${resolved.path} (version ${resolved.version}). Set UNITY_PATH to override.`,
        );
      }
    } else {
      this.unityPath = "";
      this.unityPathSource = "none";
    }

    // UNITY_PROJECT_PATH env -> instance lock's projectPath (so batch works
    // with zero env vars when the bridge has run the project at least once)
    // -> empty.
    this.projectPath =
      options.projectPath ??
      process.env.UNITY_PROJECT_PATH ??
      lock?.projectPath ??
      "";

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
      return makeErrorResult({
        code: "batch_not_supported",
        message:
          LIMITED_META_MESSAGES[toolName] ??
          `${toolName} is not supported in batch mode.`,
      });
    }

    const verifyOperation = VERIFY_TOOL_TO_OPERATION[toolName];
    const metaOperation = META_TOOL_TO_OPERATION[toolName];

    if (!verifyOperation && !metaOperation) {
      return makeErrorResult({
        code: "unknown_batch_tool",
        message: `Tool '${toolName}' is not a batch tool.`,
      });
    }

    const pathError = await this.validateUnityPath();
    if (pathError) return pathError;

    if (!this.projectPath) {
      return makeErrorResult({
        code: "project_path_missing",
        message:
          "UNITY_PROJECT_PATH environment variable is required for batch operations " +
          "(or open the project in Unity once so the instance lock records its path — " +
          "the MCP server falls back to the lock's projectPath when the env var is unset).",
      });
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
      // M22 Plan 3 / T-fix-2 — a classified failure (e.g.
      // editor_instance_locked) carries a targeted code; only fall back to the
      // generic batch_spawn_failed for unclassified errors so genuine spawn /
      // compile failures keep their existing behavior.
      const code = err instanceof BatchClassificationError
        ? err.code
        : "batch_spawn_failed";
      return makeErrorResult({ code, message });
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
      // No explicit UNITY_PATH AND auto-discovery found nothing. List the
      // scanned roots so the user/agent knows where to install Unity or set
      // the env var. `unity_not_discovered` is distinct from the legacy
      // `unity_path_missing` so an agent can tell "I looked, nothing here"
      // from "discovery was disabled".
      const roots = this.discoveryRoots ?? scannedHubRoots();
      const rootList = roots.length > 0 ? roots.join(", ") : "(no Hub paths found for this OS)";
      return makeErrorResult({
        code: "unity_not_discovered",
        message:
          "No Unity Editor found. The MCP server auto-discovers Unity from the " +
          "OS-default Unity Hub install paths (+ UNITY_HUB env override); " +
          `scanned: ${rootList}. Either install Unity there, or set UNITY_PATH ` +
          "to an explicit editor executable " +
          "(macOS: /Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity, " +
          "Windows: C:\\Program Files\\Unity\\Hub\\Editor\\<version>\\Editor\\Unity.exe, " +
          "Linux: ~/Unity/Hub/Editor/<version>/Unity).",
      });
    }

    try {
      const s = await stat(this.unityPath);
      if (!s.isFile()) {
        return makeErrorResult({
          code: "unity_path_invalid",
          message:
            `Unity path '${this.unityPath}' is not a file. ` +
            "Set UNITY_PATH to the Unity Editor executable.",
        });
      }
    } catch {
      return makeErrorResult({
        code: "unity_path_not_found",
        message:
          `Unity path '${this.unityPath}' does not exist or is not accessible. ` +
          "Verify the path points to a valid Unity Editor executable.",
      });
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
          // Most common cause: the bridge assembly failed to compile, so the
          // batch entry point (BridgeBatchEntry.Run()) never ran and emitted
          // no markers. Surface the C# compiler errors directly rather than an
          // opaque "no markers" message.
          const combined = `${stdout}\n${stderr}`;
          const csErrors = extractCompilerErrors(combined);
          const tail = stderr.trim().slice(-500) || stdout.trim().slice(-500);
          if (csErrors.length > 0) {
            reject(new Error(
              `Batch output did not contain JSON markers (exit ${exitCode}). ` +
                `The bridge assembly likely failed to compile:\n` +
                csErrors.join("\n"),
            ));
            return;
          }
          // M22 Plan 3 / T-fix-2 — before the generic no-markers reject,
          // classify the tail. Unity's one-Editor-per-project lock surfaces a
          // recognizable signature when a live Editor already holds the
          // project; emit editor_instance_locked so an agent can act (close
          // the live Editor or use live introspection) instead of seeing an
          // opaque batch_spawn_failed.
          const classified = classifyBatchFailure(combined);
          if (classified === "editor_instance_locked") {
            reject(new BatchClassificationError(
              "editor_instance_locked",
              "A live Unity Editor holds the project lock, so the headless " +
                "compile_check spawn could not open the project. Unity allows " +
                "only one Editor per project. Either close the live Editor and " +
                "retry compile_check, or verify compile state via the live " +
                "bridge instead (execute_csharp + Library/ScriptAssemblies DLL " +
                "mtime check, or read_compile_errors).",
            ));
            return;
          }
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
