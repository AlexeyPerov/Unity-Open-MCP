import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 1 — read-only shader data. Includes an `errors` field carrying
// shader compile errors. Read-only: gate-free. Token-bounded by `max_results`
// for the property list.
export const shaderGetData = makeTool(
  "unity_open_mcp_shader_get_data",
  "Read a shader's properties, subshader/pass count, and compile errors. Read-only (gate-free). " +
    "Resolve the shader by `asset_path` (.shader / .shadergraph) or by `name` (Shader.Find). " +
    "Each property carries name, type, and description; `errors` lists ShaderMessage entries " +
    "(severity + message + platform when available). Token-bounded by `max_results`.",
  {
    properties: {
          asset_path: {
            type: "string",
            description: "Shader asset path. Highest priority resolver.",
          },
          name: {
            type: "string",
            description: "Shader name (Shader.Find). Used when asset_path is omitted.",
          },
          max_results: {
            type: "integer",
            default: 100,
            minimum: 1,
            description: "Max shader properties returned; remaining count reported in 'truncated'.",
          },
        },
  },
);
