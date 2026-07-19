import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 7 / T20.7.1 — Shader Graph node_add. Compile-gated +
// auto-activating (see shader-graph-create.ts). Mutating: adds a node to the
// graph — paths_hint is the asset path, EditorSettle lifecycle. node_type is a
// friendly name (UV / Multiply / Sample Texture 2D / ...) or a full class name.
export const shaderGraphNodeAdd = makeTool(
  "unity_open_mcp_shader_graph_node_add",
  "Add a node to a Shader Graph. node_type is a friendly name (UV, Multiply, " +
    "Sample Texture 2D, Add, Time, Color, Vector3, ...) or a full class name in " +
    "UnityEditor.ShaderGraph. position is 'x,y' in graph space. properties_json " +
    "is an optional JSON object of initial { field: value } patches applied " +
    "after creation. Returns the new node's id (when the API assigns one) + its " +
    "input/output slot ids. Mutating: runs the full gate path; paths_hint is the " +
    "graph asset path. Requires com.unity.shadergraph. When the installed " +
    "package version exposes a different node-add surface, the tool returns a " +
    "structured shadergraph_api_unavailable error — add the node manually in the " +
    "graph window, then re-open to read its id.",
  {
    required: ["asset_path", "node_type", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description: "Shader Graph asset path ('Assets/.../*.shadergraph').",
          },
          node_type: {
            type: "string",
            description:
              "Node type — friendly name (UV, Multiply, Sample Texture 2D, Add, " +
              "Time, Color, Vector3, ...) or a full UnityEditor.ShaderGraph class name.",
          },
          position: {
            type: "string",
            description: "Optional graph-space position as 'x,y'.",
          },
          properties_json: {
            type: "string",
            description:
              "Optional JSON object of initial { field: value } patches applied to " +
              "the node after creation (serialized-field patch — same vocabulary as " +
              "object_modify / timeline_modify).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — must include the .shadergraph asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
