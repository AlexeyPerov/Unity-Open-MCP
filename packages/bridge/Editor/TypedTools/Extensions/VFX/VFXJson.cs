// M20 Plan 7 / T20.7.2 — VFX Graph (com.unity.visualeffectgraph) embedded
// domain tools.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_VFX. The owning sub-asmdef
// (com.alexeyperov.unity-open-mcp-bridge.VFX.Editor) carries
// `defineConstraints: ["UNITY_OPEN_MCP_EXT_VFX"]` and references
// Unity.VisualEffectGraph.Editor; the bridge root asmdef sets the define via
// `versionDefines` when the package resolves.
//
// VFX Graph is the second package-gated specialty (M20 Plan 7 fallback). It
// ships with compile-gate AND auto-activation (the `vfx` group auto-activates
// for the session when com.unity.visualeffectgraph is installed — see
// tool-groups.ts). VFX Graph's editing API (UnityEditor.VFX: VFXGraph, VFXBlock,
// VFXContext) is more internal than Shader Graph's — even the package editor
// surface is largely internal. The catalog is therefore scoped to list / open /
// narrow block-edit, mirroring the competitor's stopping point (list/open) with
// a small extension (block_edit) attempted behind the reflection helper. When
// the editing API cannot be reached the mutating tool returns a structured
// `vfx_api_unavailable` error instead of throwing.
//
// Naming: tool ids follow `unity_open_mcp_vfx_<action>` (snake_case domain
// prefix).
#if UNITY_OPEN_MCP_EXT_VFX
using System.Text;

namespace UnityOpenMcpBridge.Extensions.VFX
{
    // Shared helpers for the VFX Graph embedded domain tools.
    //
    // JSON envelope builders + a thin reflection surface over
    // Unity.VisualEffectGraph.Editor. Mirrors the ShaderGraphJson /
    // TimelineJson helper shape so the domain packs read consistently.
    internal static class VFXJson
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
