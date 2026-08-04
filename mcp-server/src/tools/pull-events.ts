import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M13 T4.4 — bridge event pull tool.
//
// The bridge emits console-log and editor-state (compile / play-mode)
// notifications over an SSE stream at GET /events. This tool drains the
// per-process subscription queue and returns incremental events since the
// previous call. The first call opens the subscription; subsequent calls
// return only new events.
//
// Why poll instead of push? The MCP server runs over a stdio transport; it has
// no native way to forward bridge SSE → MCP notifications. Polling per call
// keeps the model in the loop and lets an agent decide when to drain (e.g.
// right after a mutation that may produce logs).
export const pullEvents = makeTool(
  "unity_senses_pull_events",
  "Drain incremental bridge events (console logs + editor-state transitions) since the previous call. " +
    "The first call opens a server-side SSE subscription and returns nothing (an empty events array with " +
    "started:true); console history is NOT replayed. For anything that happened before the first call, " +
    "read the Editor log via unity_open_mcp_read_compile_errors (compile errors) or call this tool BEFORE " +
    "the operation you want to observe, then again after. Later calls return only events that arrived " +
    "since the previous drain. Use this after execute_csharp / mutations to stream console output without " +
    "polling /ping or re-reading the full console. Each event carries `seq`, `ts`, `type` ('log' | " +
    "'editor_state'), and type-specific fields (logType/message/stack for logs, " +
    "state/isCompiling/isPlaying for state). `dropped` reports events evicted from the queue before this " +
    "pull; `connected` reports the SSE reader state. Requires a live Unity Editor connection.",
  {
    properties: {
          max_events: {
            type: "integer",
            default: 50,
            minimum: 1,
            maximum: 1000,
            description:
              "Maximum events to return per call. Additional buffered events are counted in `dropped`.",
          },
          // Hidden-but-documented: an explicit subscriber id lets a long-lived
          // agent session resume across MCP server restarts. Defaults to a
          // server-scoped id; callers normally omit it.
          subscriber: {
            type: "string",
            description:
              "Optional subscriber id to resume a prior subscription (defaults to a server-scoped id).",
          },
        },
  },
);
