// M20 Plan 9 / T20.9.1 — SpriteAtlas embedded domain tools.
//
// Shared JSON envelope + escape helpers for the SpriteAtlas embedded domain
// tools. Mirrors LightingJson / AudioJson so each embedded domain has a
// self-contained helper it can evolve independently.
//
// The SpriteAtlas / SpriteAtlasAsset / SpriteAtlasPackingSettings /
// SpriteAtlasTextureSettings types live in the built-in 2D module
// (UnityEngine.U2D / UnityEditor.U2D in CoreModule) and are present in every
// Unity install, so this domain ships UNGATED — no UNITY_OPEN_MCP_EXT_2D
// define, no sub-asmdef defineConstraints. The `2d` tool group is still hidden
// from ListTools until the session activates it via manage_tools (group
// visibility is a session concern, independent of compile-gating).
#pragma warning disable CS0618
using System.Text;

namespace UnityOpenMcpBridge.Extensions.SpriteAtlas
{
    static class SpriteAtlasJson
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
