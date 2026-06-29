import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 7 / T20.7.1 — Shader Graph node_connect. Compile-gated +
// auto-activating (see shader-graph-create.ts). Mutating: connects an output
// slot to an input slot — paths_hint is the asset path, EditorSettle lifecycle.
// Idempotent (connecting an existing edge again is a no-op success).
export const shaderGraphNodeConnect: Tool = {
  name: "unity_open_mcp_shader_graph_node_connect",
  description:
    "Connect an output slot of one node to an input slot of another in a " +
    "Shader Graph. source_node_id + source_slot identify the output; " +
    "destination_node_id + destination_slot identify the input. Node ids come " +
    "from shader_graph_open or a prior node_add; slot ids are integers (see a " +
    "node's 'slots' in the open summary). Mutating: runs the full gate path; " +
    "paths_hint is the graph asset path. Idempotent. Requires " +
    "com.unity.shadergraph. When the installed package version exposes a " +
    "different connect surface, the tool returns a structured " +
    "shadergraph_api_unavailable error — connect the nodes manually in the " +
    "graph window.",
  inputSchema: {
    type: "object",
    required: [
      "asset_path",
      "source_node_id",
      "source_slot",
      "destination_node_id",
      "destination_slot",
      "paths_hint",
    ],
    properties: {
      asset_path: {
        type: "string",
        description: "Shader Graph asset path ('Assets/.../*.shadergraph').",
      },
      source_node_id: {
        type: "string",
        description: "Source (output) node id — from shader_graph_open or node_add.",
      },
      source_slot: {
        type: "integer",
        description: "Output slot id on the source node.",
        minimum: 0,
      },
      destination_node_id: {
        type: "string",
        description: "Destination (input) node id — from shader_graph_open or node_add.",
      },
      destination_slot: {
        type: "integer",
        description: "Input slot id on the destination node.",
        minimum: 0,
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — must include the .shadergraph asset path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
