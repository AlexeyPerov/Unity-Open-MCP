import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 7 / T20.7.1 — Shader Graph open. Compile-gated + auto-activating
// (see shader-graph-create.ts). Opens the graph in its Shader Graph window (or
// focuses the window already showing it) and returns a structured node/edge
// summary read from the graph as the Shader Graph importer loads it.
// `window: "none"` reads without a window, `"close"` closes the graph's
// window. Writes no asset — Gate = Off, no paths_hint. Use the summary to
// learn node ids / slot ids before node_add / node_connect.
export const shaderGraphOpen = makeTool(
  "unity_open_mcp_shader_graph_open",
  "Open a Shader Graph asset in its Shader Graph editor window (or focus the window " +
    "already showing it) and return a structured summary (node count, each node's id / " +
    "name / type / position / slots, edge count, each edge's output→input node+slot) plus " +
    "`window` { action: opened | focused | closed | not_open | none, open, title }. " +
    "`window: \"none\"` returns the summary without touching windows (works in batch " +
    "mode); `\"close\"` closes the graph's window. Writes no asset — Gate is Off, no " +
    "paths_hint. Refuses `editor_window_unavailable` in batch mode (use window none) and " +
    "`graph_has_unsaved_changes` when closing a window that holds unsaved edits. The " +
    "window stays open after the call; node_add / node_connect reload it. Close it before " +
    "deleting or moving the graph — Shader Graph asks in a modal dialog otherwise. The " +
    "summary is read from the graph as the importer loads it; nodes include the master " +
    "stack's block nodes, and each slot is { id, name, direction: input | output }. Use it " +
    "to discover node ids and slot ids before node_add / node_connect. Requires " +
    "com.unity.shadergraph.",
  {
    required: ["asset_path"],
        properties: {
          asset_path: {
            type: "string",
            description:
              "Shader Graph asset path ('Assets/.../*.shadergraph' or a '.shadersubgraph'). " +
              "Highest and only resolver.",
          },
          window: {
            type: "string",
            enum: ["open", "none", "close"],
            default: "open",
            description:
              "open (default): open the graph's window or focus it. none: summary only. " +
              "close: close the graph's window (refused while it has unsaved changes).",
          },
        },
  },
);
