import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// Offline, filesystem-only tool: reads the tail of Unity's platform Editor.log
// and extracts C# compiler errors. The one recovery channel that works when
// the bridge assembly itself has failed to compile — in that state every
// in-bridge channel (read_console, editor_status) is dead with it, and
// compile_check can't run either (the batch entry point shares the broken
// assembly, and Unity's per-project lock blocks a second instance). The live
// Editor still writes CSxxxx diagnostics to Editor.log regardless of bridge
// health, so this tool retrieves them without touching Unity or the bridge.
export const readCompileErrors: Tool = {
  name: "unity_open_mcp_read_compile_errors",
  description:
    "Read C# compiler errors directly from Unity's Editor.log (offline, " +
    "no bridge, no Unity spawn). Returns structured CSxxxx errors with file, " +
    "line, and message. Use this when the bridge is unreachable after a " +
    "recompile — a 'bridge_compile_failed' response points here — to retrieve " +
    "the exact compiler errors that prevented the bridge from reloading. " +
    "Works even when the bridge assembly itself is broken, because it reads " +
    "the log file the Editor writes independently of the bridge.",
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
          "(default 256KB). Compiler errors are written in a contiguous " +
          "block near the end, so a modest tail is ample. Increase only if " +
          "errors are reported missing.",
      },
    },
    additionalProperties: false,
  },
};
