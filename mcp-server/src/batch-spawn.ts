import { spawn } from "node:child_process";
import { StringDecoder } from "node:string_decoder";
import { stat } from "node:fs/promises";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import { resolveUnityPath, scannedHubRoots } from "./unity-install-discovery.js";
import { readInstanceLock, isPidAlive } from "./instance-discovery.js";
import { makeErrorResult } from "./results.js";
import { VERIFY_JSON_BEGIN, VERIFY_JSON_END } from "./constants.js";

const VERIFY_EXECUTE_METHOD = "UnityOpenMcpVerify.Batch.VerifyBatchEntry.Run";
const BRIDGE_EXECUTE_METHOD = "UnityOpenMcpBridge.Batch.BridgeBatchEntry.Run";
const OUTPUT_BEGIN = VERIFY_JSON_BEGIN;
const OUTPUT_END = VERIFY_JSON_END;

const DEFAULT_BATCH_TIMEOUT_MS = 600_000;

const VERIFY_TOOL_TO_OPERATION: Record<string, string> = {
  unity_open_mcp_scan_all: "scan_all",
  unity_open_mcp_baseline_create: "baseline_create",
  unity_open_mcp_regression_check: "regression_check",
};

// Verify-family tools that are ALWAYS batch-routed, even when a live bridge is
// up. These scan/baseline/regression ops require the headless verify package
// and are NOT registered on the live bridge — routing them live yields a bare
// `tool_not_found`. The router mirrors the compile_check precedent: always
// spawn fresh so the tool does what its contract says. validate_edit /
// scan_paths are intentionally NOT here — those ARE registered on the live
// bridge and stay live-first.
export const VERIFY_BATCH_TOOL_NAMES: ReadonlySet<string> = new Set(
  Object.keys(VERIFY_TOOL_TO_OPERATION),
);

// Tools that ALWAYS route to batch, even when a live bridge is up — keyed by
// tool name → fallbackReason for the route-meta envelope. Single source of
// truth for the router's pinned always-batch branch: compile_check spawns a
// fresh Unity to recompile from scratch; verify-family tools need the headless
// verify package and are NOT registered on the live bridge.
//
// validate_edit / scan_paths are intentionally NOT here. They ARE verify-family
// tools, but they are also registered on the live bridge and have named
// routeHandlers in tool-router.ts (routeVerifyResult). Named-handler dispatch
// runs FIRST in routeCore — before the ALWAYS_BATCH_TOOLS lookup is even
// reached — so an entry here for them would be dead code. They stay live-first
// (the bridge serves them when up; the named handler applies the output-profile
// fold/paging server-side). Adding them here would have no effect and mislead
// future contributors. Keep this map disjoint (a tool appears once).
export const ALWAYS_BATCH_TOOLS: ReadonlyMap<string, string> = new Map<string, string>([
  ["unity_open_mcp_compile_check", "compile_check_always_batch"],
  ...[...VERIFY_BATCH_TOOL_NAMES].map((name) => [name, "verify_always_batch"] as [string, string]),
]);

const META_TOOL_TO_OPERATION: Record<string, string> = {
  unity_open_mcp_find_members: "find_members",
  unity_open_mcp_compile_check: "compile_check",
  unity_open_mcp_execute_csharp: "execute_csharp",
  unity_open_mcp_invoke_method: "invoke_method",
  unity_open_mcp_execute_menu: "execute_menu",
};

export const BATCH_TOOL_NAMES = new Set([
  ...Object.keys(VERIFY_TOOL_TO_OPERATION),
  ...Object.keys(META_TOOL_TO_OPERATION),
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

// M13 — bounded, UTF-8-correct stdout/stderr accumulator for the batch spawn.
//
// Two defects this fixes:
//   1. `stdout += chunk.toString()` decoded each chunk independently, so a
//      multi-byte UTF-8 sequence straddling a chunk boundary became U+FFFD
//      replacement characters inside the VERIFY_JSON_BEGIN/END block, breaking
//      JSON.parse for any payload with non-ASCII (asset names, localized
//      compiler messages). `StringDecoder` buffers incomplete trailing bytes
//      across chunks and only emits whole characters.
//   2. `stdout`/`stderr` grew unbounded over a 10-minute run (Unity can emit
//      megabytes of compile/import log lines). `MAX_OUTPUT_BYTES` caps the
//      retained tail; the verify JSON is emitted near the end (after the
//      operation completes), so keeping the last `MAX_OUTPUT_BYTES` preserves
//      the markers + body for any realistic verify output while bounding
//      memory. Head truncation is silent: only the tail is retained, and
//      nothing downstream inspects whether leading bytes were dropped.
const MAX_OUTPUT_BYTES = 16 * 1024 * 1024; // 16 MiB per stream — covers verify JSON + tail.

export class BoundedTextAccumulator {
  private decoder = new StringDecoder("utf8");
  private bytes: Buffer[] = [];
  private byteLen = 0;

  /** Append a chunk; bytes that don't yet form a complete character are
   *  buffered in the decoder and emitted on the next push / flush. */
  push(chunk: Buffer): void {
    const decoded = this.decoder.write(chunk);
    if (decoded) this.appendDecoded(decoded);
  }

  /** Flush any trailing incomplete bytes (called once on stream end). */
  flush(): void {
    const tail = this.decoder.end();
    if (tail) this.appendDecoded(tail);
  }

  private appendDecoded(text: string): void {
    // Encode to UTF-8 bytes so the cap is a byte budget (not a UTF-16 code-unit
    // budget), matching how the data arrives from the child.
    const buf = Buffer.from(text, "utf8");
    this.bytes.push(buf);
    this.byteLen += buf.length;
    // Drop whole leading buffers while over budget. Never splits a buffer
    // (keeps the StringDecoder contract intact) and keeps the JSON markers,
    // which arrive at the tail.
    while (this.byteLen > MAX_OUTPUT_BYTES && this.bytes.length > 1) {
      const head = this.bytes.shift()!;
      this.byteLen -= head.length;
    }
  }

  toString(): string {
    return Buffer.concat(this.bytes, this.byteLen).toString("utf8");
  }
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
    if (typeof args.regression_threshold === "number")
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
    if (typeof args.max_results === "number") cli.push("--max-results", String(args.max_results));
  } else if (operation === "compile_check") {
    // T6.1 — guard with typeof === "number" (not !== undefined) so a caller
    // passing timeout_ms:null does not emit `--timeout-ms null` on the argv
    // (null !== undefined). The C# parser would then try to parse "null" as an
    // int. Mirrors the safe guard in live-client.ts postToolFetch.
    if (typeof args.timeout_ms === "number") cli.push("--timeout-ms", String(args.timeout_ms));
  } else if (operation === "execute_csharp") {
    // The code payload is space-bearing and multi-line: it cannot round-trip
    // through argv splitting on spaces. Encode spaces as ASCII unit separator
    // (0x1f) here; the C# entry point decodes them back to spaces so the
    // original snippet is reconstructed exactly.
    if (args.code !== undefined) cli.push("--code", encodeSpaces(String(args.code)));
    if (Array.isArray(args.usings)) {
      for (const u of args.usings) cli.push("--using", String(u));
    }
    if (Array.isArray(args.object_ids)) {
      for (const id of args.object_ids) cli.push("--object-id", String(id));
    }
    if (typeof args.max_depth === "number") cli.push("--max-depth", String(args.max_depth));
    if (typeof args.max_items === "number") cli.push("--max-items", String(args.max_items));
    // Deny-list bypass contract: confirm_bypass:true forces an explicit gate:"off".
    if (args.confirm_bypass === true) cli.push("--confirm-bypass", "true");
  } else if (operation === "invoke_method") {
    if (args.type_name !== undefined) cli.push("--type-name", String(args.type_name));
    if (args.method_name !== undefined) cli.push("--method-name", String(args.method_name));
    if (args.is_static !== undefined) cli.push("--is-static", String(args.is_static));
    if (args.assembly_name !== undefined) cli.push("--assembly-name", String(args.assembly_name));
    if (args.object_id !== undefined) cli.push("--object-id", String(args.object_id));
    if (Array.isArray(args.args)) {
      for (const a of args.args) cli.push("--arg", encodeSpaces(String(a)));
    }
    if (Array.isArray(args.arg_type_names)) {
      for (const t of args.arg_type_names) cli.push("--arg-type-name", String(t));
    }
    if (Array.isArray(args.generic_arg_types)) {
      for (const g of args.generic_arg_types) cli.push("--generic-arg-type", String(g));
    }
    if (typeof args.max_depth === "number") cli.push("--max-depth", String(args.max_depth));
    if (typeof args.max_items === "number") cli.push("--max-items", String(args.max_items));
  } else if (operation === "execute_menu") {
    if (args.menu_path !== undefined) cli.push("--menu-path", String(args.menu_path));
  }

  return cli;
}

// Encodes spaces in a value as ASCII unit separator (0x1f) so a space-bearing
// flag value (e.g. a C# code snippet or a multi-token arg) survives argv
// splitting when spawned through `Unity ... -- <flags>`. The C# entry point
// (BridgeBatchEntry.ReadMultilineValue) decodes 0x1f back to a space.
export function encodeSpaces(value: string): string {
  return value.replace(/ /g, "\x1f");
}

// M22 Plan 3 / T-fix-2 — classify a batch failure tail before falling back
// to the generic `batch_spawn_failed`. Unity's one-Editor-per-project lock
// produces a recognizable signature when a live Editor already holds the
// project open; surfacing `editor_instance_locked` lets an agent tell that
// situation apart from a genuine compile/spawn failure and act on it (close
// the live Editor, or use live introspection instead of a headless spawn).
//
// Package-resolution failures get the same treatment: Unity bails at package
// resolution BEFORE writing the VERIFY_JSON_BEGIN/END markers, so the generic
// `batch_spawn_failed` / "did not contain JSON markers" error masks the real
// root cause (an unresolvable Packages/manifest.json dependency). Surfacing
// `project_load_failed` with the offending package stops an agent from
// "fixing" a compile that never ran. See specs/feedback.md (compile_check
// batch_spawn_failed).
//
// Returns the error code to emit, or null when the tail does not match a
// known classification (caller falls back to batch_spawn_failed).
const PROJECT_LOCK_PATTERN = /another unity instance|already open/i;

// Unity prints these when the project fails to load at the package-resolution
// stage — before the batch entry point ever runs, so no markers are emitted:
//   - "Project has invalid dependencies" (Package Manager resolution failure)
//   - "Package Manager Server process was shutdown" (backend process died)
const PROJECT_LOAD_FAILED_PATTERN =
  /Project has invalid dependencies|Package Manager Server process was (?:shutdown|terminated)/i;

export function classifyBatchFailure(combined: string): string | null {
  if (typeof combined !== "string" || combined.length === 0) return null;
  if (PROJECT_LOCK_PATTERN.test(combined)) return "editor_instance_locked";
  if (PROJECT_LOAD_FAILED_PATTERN.test(combined)) return "project_load_failed";
  return null;
}

// Capture the offending package id(s) from a Package Manager resolution
// failure tail. Unity prints reverse-DNS package identifiers on lines like:
//   [Package Manager] com.unity.modules.physicscore2d is not a valid package
//   Missing or invalid dependencies: com.unity.modules.physicscore2d
// We match com.*/org.* identifiers with at least three dot-separated labels
// (so "com.unity" alone does not match), deduped and capped — best-effort,
// since the exact phrasing varies across Unity versions. Returns the raw
// matches in first-seen order.
const PACKAGE_ID_PATTERN = /\b((?:com|org)\.[a-z0-9][a-z0-9_-]*(?:\.[a-z0-9][a-z0-9_-]*)+)\b/gi;
export function extractOffendingPackages(combined: string): string[] {
  if (typeof combined !== "string" || combined.length === 0) return [];
  const seen = new Set<string>();
  const out: string[] = [];
  PACKAGE_ID_PATTERN.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = PACKAGE_ID_PATTERN.exec(combined)) !== null) {
    const id = m[1];
    if (!seen.has(id)) {
      seen.add(id);
      out.push(id);
      if (out.length >= 8) break; // cap; a wall of ids is not more useful
    }
  }
  return out;
}

const UNITY_SPAWN_REFUSED_NEXT_STEPS = [
  "The Unity binary could not be executed (spawn refused or exit code 127). Do not retry compile_check blindly.",
  "Verify UNITY_PATH points to a valid Unity Editor executable, then call unity_open_mcp_read_compile_errors to check compile state without headless spawn.",
  "Call unity_open_mcp_bridge_status to confirm whether a live Editor is running before retrying batch operations.",
];

// Builds the argv array passed to the headless Unity child process. Exported
// for unit tests that assert compile_check omits -quit (async finalize path).
export function buildUnityBatchArgs(
  operation: string,
  projectPath: string,
  executeMethod: string,
  toolArgs: string[],
): string[] {
  const unityArgs = ["-batchmode"];
  // compile_check is asynchronous: BridgeBatchEntry.Run() returns immediately
  // and CompileCheckState emits markers later from EditorApplication.update.
  // -quit would exit as soon as Run() returns (exit 0, no markers).
  if (operation !== "compile_check") {
    unityArgs.push("-quit");
  }
  unityArgs.push(
    "-projectPath", projectPath,
    "-executeMethod", executeMethod,
    "--",
    ...toolArgs,
  );
  return unityArgs;
}

// Error carrying a classified code so route()'s catch can emit a targeted
// error result instead of the generic batch_spawn_failed. Thrown by the
// spawn close-handler when classifyBatchFailure matches the tail.
//
// `agentNextSteps` is an optional structured recovery hint (an array of
// actionable strings naming specific tools). When present, route()'s catch
// emits it alongside the error envelope so an agent has a machine-readable
// recovery branch — mirroring the MutationEnvelope.agentNextSteps pattern
// used by the live-bridge gate results. See specs/feedback.md (editor_instance_locked).
export class BatchClassificationError extends Error {
  readonly code: string;
  readonly agentNextSteps?: string[];
  constructor(code: string, message: string, agentNextSteps?: string[]) {
    super(message);
    this.name = "BatchClassificationError";
    this.code = code;
    this.agentNextSteps = agentNextSteps;
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

    // Parse the timeout env override with a finiteness/positivity guard:
    // a non-numeric value (e.g. "abc") parses to NaN, and setTimeout(fn, NaN)
    // fires immediately, making every batch time out before Unity even starts.
    // Fall back to the documented default for unset/invalid values, matching
    // retry-policy.ts's parsePositiveInt discipline.
    const rawTimeout = process.env.UNITY_OPEN_MCP_BATCH_TIMEOUT_MS;
    const parsedTimeout = rawTimeout
      ? parseInt(rawTimeout, 10)
      : DEFAULT_BATCH_TIMEOUT_MS;
    this.timeoutMs =
      Number.isFinite(parsedTimeout) && parsedTimeout > 0
        ? parsedTimeout
        : DEFAULT_BATCH_TIMEOUT_MS;
  }

  isBatchTool(toolName: string): boolean {
    return BATCH_TOOL_NAMES.has(toolName);
  }

  // The resolved project path this router will pass to the headless Unity
  // spawn. Read-only — the value is fixed at construction from
  // BatchSpawnOptions.projectPath, UNITY_PROJECT_PATH, or the instance lock.
  // Exposed so the router-stack wiring test can assert buildRouterStack
  // threads env.projectPath through (without spawning Unity to observe it).
  getProjectPath(): string {
    return this.projectPath;
  }

  async route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const verifyOperation = VERIFY_TOOL_TO_OPERATION[toolName];
    const metaOperation = META_TOOL_TO_OPERATION[toolName];

    if (!verifyOperation && !metaOperation) {
      return makeErrorResult({
        code: "unknown_batch_tool",
        message: `Tool '${toolName}' is not a batch tool.`,
      });
    }

    // feedback-fable-31-07 §5 — pre-spawn instance-lock check. Unity allows
    // only ONE Editor per project. When a live editor already holds this
    // project, a batch spawn is GUARANTEED to fail on the project lock AND its
    // startup rotates ~/Library/Logs/Unity/Editor.log → Editor-prev.log, which
    // then poisons read_compile_errors (the live editor keeps writing to the
    // rotated file). Short-circuit BEFORE spawning (and before Unity discovery
    // — there's no point discovering Unity when the spawn will be refused): if
    // the lock's PID is alive, return the same editor_instance_locked error the
    // post-spawn classifier would produce, without the failed spawn and without
    // the log rotation.
    if (this.projectPath) {
      try {
        const lock = readInstanceLock(this.projectPath);
        if (lock && lock.pid && isPidAlive(lock.pid)) {
          return makeErrorResult({
            code: "editor_instance_locked",
            message:
              "A live Unity Editor holds the project lock, so the headless " +
              "spawn was not attempted (Unity allows one Editor per project, " +
              "and the failed spawn would rotate Editor.log and break " +
              "read_compile_errors).",
            detail: {
              error: {
                code: "editor_instance_locked",
                message:
                  "A live Unity Editor (pid " + lock.pid + ") holds the project lock.",
              },
              agentNextSteps: [
                "A live Unity Editor holds the project lock, so the headless spawn cannot open the project (one Editor per project).",
                "To verify compile state without closing the Editor, call unity_open_mcp_read_compile_errors (reads Editor.log offline; if its logSource is a prev_log_* value, the log was rotated — prefer the live bridge's compile signal).",
                "Or close the live Editor and retry the batch spawn.",
              ],
            },
          });
        }
      } catch {
        // Unreadable lock → fall through to the normal spawn path (its
        // classification already handles the project-locked case reactively).
      }
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
      if (err instanceof BatchClassificationError) {
        // When the classified error carries structured recovery hints, surface
        // them as a top-level agentNextSteps[] sibling to the error envelope —
        // matching MutationEnvelope's shape so an agent has a machine-readable
        // recovery branch instead of having to parse prose. See specs/feedback.md.
        if (err.agentNextSteps && err.agentNextSteps.length > 0) {
          return makeErrorResult({
            code: err.code,
            message,
            detail: { error: { code: err.code, message }, agentNextSteps: err.agentNextSteps },
          });
        }
        return makeErrorResult({ code: err.code, message });
      }
      // Unclassified spawn failure — point the agent at the same recovery tools
      // (read_compile_errors, reimport_package) so it is not left with an opaque
      // code and no next step.
      return makeErrorResult({
        code: "batch_spawn_failed",
        message,
        detail: {
          error: { code: "batch_spawn_failed", message },
          agentNextSteps: [
            "The batch spawn failed for an unclassified reason. To check compile state without spawning a headless Editor, call unity_open_mcp_read_compile_errors.",
            "If the issue is local-package source not under Assets/, call unity_open_mcp_reimport_package on the package to force a reimport.",
            "To run a headless batch, ensure no live Editor holds the project lock and the Unity install path is valid.",
          ],
        },
      });
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

      const unityArgs = buildUnityBatchArgs(
        operation,
        this.projectPath,
        executeMethod,
        toolArgs,
      );

      console.error(
        `[unity-open-mcp] Batch spawn: ${this.unityPath} ${unityArgs.join(" ")}`,
      );

      const startTime = Date.now();
      // M13 — bounded, UTF-8-correct accumulators (see BoundedTextAccumulator).
      const stdoutAcc = new BoundedTextAccumulator();
      const stderrAcc = new BoundedTextAccumulator();

      const child = spawn(this.unityPath, unityArgs, {
        stdio: ["ignore", "pipe", "pipe"],
      });

      // M13 — escalate SIGTERM → SIGKILL after a grace window so a wedged
      // Unity actually dies instead of lingering until the parent is killed.
      // The grace is short relative to the overall timeout but long enough for
      // Unity to flush its log on a cooperative shutdown.
      let sigtermSent = false;
      let sigkillTimer: NodeJS.Timeout | null = null;
      const SIGKILL_GRACE_MS = 5_000;
      const armKillEscalation = (): void => {
        if (sigkillTimer) return;
        sigtermSent = true;
        child.kill("SIGTERM");
        sigkillTimer = setTimeout(() => {
          // SIGKILL is unblockable; ignore the return — the close handler
          // resolves/rejects the promise.
          try { child.kill("SIGKILL"); } catch { /* already dead */ }
        }, SIGKILL_GRACE_MS);
      };

      const timer = setTimeout(() => {
        armKillEscalation();
        reject(new Error(
          `Batch Unity process timed out after ${this.timeoutMs / 1000}s.`,
        ));
      }, this.timeoutMs);

      const clearTimers = (): void => {
        clearTimeout(timer);
        if (sigkillTimer) clearTimeout(sigkillTimer);
      };

      child.stdout?.on("data", (chunk: Buffer) => {
        stdoutAcc.push(chunk);
      });

      child.stderr?.on("data", (chunk: Buffer) => {
        stderrAcc.push(chunk);
      });

      child.on("error", (err) => {
        clearTimers();
        reject(new BatchClassificationError(
          "unity_spawn_refused",
          `Failed to spawn Unity at '${this.unityPath}': ${err.message}`,
          UNITY_SPAWN_REFUSED_NEXT_STEPS,
        ));
      });

      child.on("close", (code) => {
        clearTimers();
        const elapsedMs = Date.now() - startTime;
        const exitCode = code ?? 1;
        // M13 — flush any trailing incomplete UTF-8 bytes before reading.
        stdoutAcc.flush();
        stderrAcc.flush();
        const stdout = stdoutAcc.toString();
        const stderr = stderrAcc.toString();

        console.error(
          `[unity-open-mcp] Batch completed: exit=${exitCode} elapsed=${elapsedMs}ms` +
            (sigtermSent ? " (killed after timeout)" : ""),
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
              [
                "A live Unity Editor holds the project lock, so the headless compile_check spawn cannot open the project (one Editor per project).",
                "To verify compile state without closing the Editor, call unity_open_mcp_read_compile_errors (reads Editor.log offline).",
                "To confirm a specific local package recompiled, call unity_open_mcp_reimport_package on the package and compare dllMtimeBefore/dllMtimeAfter.",
                "To run a true headless compile_check, close the live Editor first.",
              ],
            ));
            return;
          }
          if (classified === "project_load_failed") {
            // Unity bailed at package resolution BEFORE the batch entry point
            // ran, so no markers were emitted — the generic "did not contain
            // JSON markers" error would mask the real root cause (an
            // unresolvable Packages/manifest.json dependency). Name the
            // offending package(s) and point at read_compile_errors, which
            // surfaces the Package Manager notice without a headless spawn.
            const offending = extractOffendingPackages(combined);
            const pkgPart = offending.length > 0
              ? ` Offending package(s): ${offending.join(", ")}.`
              : "";
            reject(new BatchClassificationError(
              "project_load_failed",
              `Batch Unity exited at package/project resolution (exit ${exitCode}) ` +
                `without running the batch entry point, so no compile took place. ` +
                `The project failed to load — this is NOT a C# compile error.${pkgPart} ` +
                `Check Packages/manifest.json for unresolvable dependencies and call ` +
                `unity_open_mcp_read_compile_errors to see the Package Manager notice.`,
              [
                `Batch Unity failed to load the project (exit ${exitCode}) before reaching the batch entry point — this is a package-resolution / project-load failure, not a compile failure.${pkgPart}`,
                "Call unity_open_mcp_read_compile_errors to read the Package Manager notice offline (no headless spawn).",
                "Inspect Packages/manifest.json and Packages/packages-lock.json for packages that cannot be resolved by the current Unity version.",
                "Do not retry compile_check blindly — the same project-load failure will recur until the manifest is fixed.",
              ],
            ));
            return;
          }
          if (exitCode === 127) {
            reject(new BatchClassificationError(
              "unity_spawn_refused",
              `Unity binary exited with code 127 (command not found or not executable). ` +
                `Path: '${this.unityPath}'.`,
              UNITY_SPAWN_REFUSED_NEXT_STEPS,
            ));
            return;
          }
          // Final generic fallback. Two cases reach here with no markers:
          //   - exit 0: Unity exited cleanly (likely a healthy compile) but
          //     emitted no markers — the async finalize path (BridgeBatchEntry
          //     → EditorApplication.update) did not fire. Point the agent at
          //     read_compile_errors so it can confirm the clean state instead
          //     of trusting an opaque failure. See specs/feedback.md (case 2).
          //   - non-zero: an unclassified spawn failure.
          reject(new Error(
            `Batch output did not contain JSON markers. Exit code: ${exitCode}.` +
              (tail ? ` Last output: ${tail}` : "") +
              (exitCode === 0
                ? " Unity exited cleanly but emitted no markers (the async " +
                  "finalize path did not run) — the compile likely succeeded. " +
                  "Call unity_open_mcp_read_compile_errors to confirm."
                : ""),
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
