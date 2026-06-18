import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — read-only shader enumeration. Gate-free. Token-bounded by
// `max_results`. Wraps AssetDatabase.FindAssets("t:Shader") and resolves the
// shader name list (a curated subset that loadAssetAtPath can resolve; built-in
// shader names are still discoverable via Shader.Find).
export const shaderListAll: Tool = {
  name: "unity_open_mcp_shader_list_all",
  description:
    "List all shader assets in the project (Assets/-rooted + packages), returning each shader's " +
    "name (the string Shader.Find resolves) and its asset path when available. Read-only " +
    "(gate-free). Use this to discover a valid shader_name for material_create / " +
    "material_set_shader. Token-bounded by `max_results`; remaining count is reported in 'truncated'.",
  inputSchema: {
    type: "object",
    properties: {
      max_results: {
        type: "integer",
        default: 200,
        minimum: 1,
        description: "Max shaders returned; remaining count is reported in 'truncated'.",
      },
    },
    additionalProperties: false,
  },
};
