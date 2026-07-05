import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// Offline, filesystem-only tool: reads the tail of Unity's Editor.log and
// extracts BOTH C# compiler errors AND package / assembly-level red flags.
// The one recovery channel that works when the bridge assembly itself has
// failed to compile — in that state every in-bridge channel (read_console,
// editor_status) is dead with it, and compile_check can't run either (the
// batch entry point shares the broken assembly, and Unity's per-project lock
// blocks a second instance). The live Editor still writes CSxxxx diagnostics
// AND assembly-resolution failures AND Package Manager notices to Editor.log
// regardless of bridge health, so this tool retrieves them without touching
// Unity or the bridge.
//
// Log resolution: Unity 6000.5+ moved the Editor.log to a project-relative
// path (<project>/Logs/Editor.log) and stops writing to the global per-user
// log. The tool prefers the project-relative log when it exists (using the
// --project path) and falls back to the global per-user log for older Unity
// versions. This matters most when the Editor is in Safe Mode with a broken
// bridge: the global log is stale there, and only the project-relative log
// carries the current compile errors.
//
// Response fields:
//   - status: "compile_failed" | "project_unhealthy" | "no_errors_found"
//   - unhealthy: true when compiler errors OR issues are present
//   - headline: one-line triage summary (empty when healthy)
//   - errors[]: structured CSxxxx diagnostics (file/line/code/message)
//   - issues[]: package / assembly red flags with kind + hint:
//       * assembly_resolution — Mono.Cecil unresolved-assembly failures
//         (classic package-version / Unity-version mismatch, e.g. ProBuilder
//         5.x compiled against an assembly ProBuilder 6 removed)
//       * package_deprecated   — [Package Manager] <id> is deprecated
//       * package_manager_error — other Package Manager conflict / resolution
//         errors
export const readCompileErrors: Tool = {
  name: "unity_open_mcp_read_compile_errors",
  description:
    "Read C# compiler errors AND package/assembly red flags directly from " +
    "Unity's Editor.log (offline, no bridge, no Unity spawn). Returns " +
    "structured CSxxxx compiler errors (file/line/code/message) PLUS a " +
    "`issues` list of package-level red flags with per-issue `kind` + " +
    "`hint`: assembly_resolution (Mono.Cecil unresolved-assembly failures — " +
    "the classic package-version / Unity-version mismatch, e.g. ProBuilder " +
    "5.x on Unity 6), package_deprecated ([Package Manager] <id> is " +
    "deprecated), and package_manager_error (conflict / resolution errors). " +
    "Use this when: (a) the bridge is unreachable after a recompile — a " +
    "'bridge_compile_failed' response points here, or ping returns " +
    "connected:false unexpectedly; (b) Unity showed a 'package update' / " +
    "incompatibility popup; (c) you suspect a package is too old for the " +
    "current Editor. Works even when the bridge assembly itself is broken, " +
    "because it reads the log file the Editor writes independently of the " +
    "bridge. Check `unhealthy` first; when true, scan `headline` for a " +
    "one-line triage then drill into `errors` and `issues`. When the response " +
    "carries `staleLogSuspected: true`, the cited source files were edited " +
    "more recently than Editor.log — the error block may reference on-disk " +
    "code you have already fixed (Unity's incremental compiler no-op'd the " +
    "recompile). Force a genuine recompile via unity_open_mcp_reimport_package " +
    "(local package) or unity_open_mcp_compile_check before trusting the errors.",
  inputSchema: {
    type: "object",
    properties: {
      tail_bytes: {
        type: "integer",
        default: 262144,
        minimum: 4096,
        maximum: 1048576,
        description:
          "Maximum number of bytes to read from the END of Editor.log " +
          "(default 256KB). Compiler errors AND assembly-resolution failures " +
          "are written in contiguous blocks near the end, so a modest tail " +
          "is ample. Increase only if errors or issues are reported missing.",
      },
    },
    additionalProperties: false,
  },
};
