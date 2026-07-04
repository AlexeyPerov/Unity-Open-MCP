// M20 Plan 7 / T20.7.1 — ShaderGraph (com.unity.shadergraph) embedded domain
// tools.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_SHADERGRAPH. The owning sub-asmdef
// (com.alexeyperov.unity-open-mcp-bridge.ShaderGraph.Editor) carries
// `defineConstraints: ["UNITY_OPEN_MCP_EXT_SHADERGRAPH"]` and references
// Unity.ShaderGraph.Editor; the bridge root asmdef sets the define via
// `versionDefines` when the package resolves.
//
// Shader Graph is the highest-value package-gated specialty (M20 Plan 7).
// This is the first domain that ships with BOTH a compile gate AND
// auto-activation (the `shadergraph` group auto-activates for the session
// when com.unity.shadergraph is installed — see tool-groups.ts). The
// companion inspect surface (shader_get_data / shader_list_all) reads
// compiled shader properties and is unaffected by this edit layer.
//
// Shader Graph's editing API (UnityEditor.ShaderGraph: GraphData,
// AbstractMaterialNode, slots) is partially internal and varies across
// versions. The mutating tools wrap the public-ish surface behind a single
// reflection helper (ShaderGraphApi) and document the Unity-version
// dependency. When the API cannot be reached the tool returns a structured
// `shadergraph_api_unavailable` error rather than throwing — the agent can
// fall back to manual editing.
//
// Naming: tool ids follow `unity_open_mcp_shader_graph_<action>` (snake_case
// domain prefix — note the underscore split vs the package name, matching
// the rest of the catalog's word boundary convention).
#if UNITY_OPEN_MCP_EXT_SHADERGRAPH
using System.Text;

namespace UnityOpenMcpBridge.Extensions.ShaderGraph
{
    // Shared helpers for the ShaderGraph embedded domain tools.
    //
    // JSON envelope builders + a reflection surface over UnityEditor.ShaderGraph
    // (GraphData / AbstractMaterialNode / slots) that varies across versions.
    // Mirrors the TimelineJson / SplinesJson helper shape so the domain packs
    // read consistently.
    internal static class ShaderGraphJson
    {
        public static string Ok(string body)
            => "{\"status\":\"ok\"," + (body ?? "") + "}";

        public static string Error(string code, string message)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        public static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
#endif
