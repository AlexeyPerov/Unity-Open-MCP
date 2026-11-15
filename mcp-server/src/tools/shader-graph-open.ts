import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 7 / T20.7.1 — Shader Graph open. Compile-gated + auto-activating
// (see shader-graph-create.ts). Read-only window bring-up: opens the graph in
// the Shader Graph editor and returns a structured node/edge summary parsed
// from the graph as the Shader Graph importer loads it. Non-mutating state change — Gate = Off, no
// paths_hint. Use this to learn node ids / slot ids before node_add /
// node_connect.
export const shaderGraphOpen = makeTool(
  "unity_open_mcp_shader_graph_open",
  "Open a Shader Graph asset in the graph editor window and return a " +
    "structured summary (node count, each node's id / name / type / position / " +
    "slots, edge count, each edge's output→input node+slot). Read-only window " +
    "bring-up — Gate is Off, no paths_hint. The summary is read from the graph " +
    "as the Shader Graph importer loads it; nodes include the master stack's " +
    "block nodes, and each slot is { id, name, direction: input | output }. " +
    "Use it to discover node ids and slot ids before node_add / node_connect. " +
    "Requires com.unity.shadergraph.",
  {
    required: ["asset_path"],
        properties: {
          asset_path: {
            type: "string",
            description:
              "Shader Graph asset path ('Assets/.../*.shadergraph'). Highest and " +
              "only resolver.",
          },
        },
  },
);
