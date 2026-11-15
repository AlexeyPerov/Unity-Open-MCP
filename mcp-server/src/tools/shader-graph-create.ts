import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 7 / T20.7.1 — Shader Graph create. Compile-gated in the bridge
// (UNITY_OPEN_MCP_EXT_SHADERGRAPH on com.unity.shadergraph) + auto-activating
// (the `shadergraph` group auto-activates when the package is present). The
// first specialty to ship under the package-detection auto-activation model.
// Mutating: produces a .shadergraph asset — paths_hint includes the asset path.
export const shaderGraphCreate = makeTool(
  "unity_open_mcp_shader_graph_create",
  "Create a new Shader Graph asset at the given asset_path. The asset_path " +
    "is required ('Assets/.../MyShader.shadergraph'; the parent folder must " +
    "exist). shader_type selects the template: Unlit (default) / Lit / Decal / " +
    "Fullscreen / Blank. Mutating: runs the full gate path; paths_hint includes " +
    "the new asset path. Requires the com.unity.shadergraph package installed. " +
    "The graph is built and written without the editor UI, with the target of " +
    "the active render pipeline for the template (the response names it; Blank " +
    "has none). A template the pipeline has no target for answers " +
    "shader_type_unsupported; when the installed package version exposes a " +
    "different editing surface the tool answers shadergraph_api_unavailable — " +
    "create the graph manually via Assets/Create > Shader Graph instead.",
  {
    required: ["asset_path", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description:
              "Destination asset path. Must end with '.shadergraph' and the parent " +
              "folder must exist.",
          },
          shader_type: {
            type: "string",
            description:
              "Template kind: 'Unlit' (default), 'Lit', 'Decal', 'Fullscreen', or " +
              "'Blank'. Unlit when omitted; any other value answers unknown_shader_type.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — must include the new .shadergraph asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
