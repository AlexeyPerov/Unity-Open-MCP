using System.Text;

namespace UnityOpenMcpExtensions.ParticleSystemExt
{
    // M16 Plan 10 — shared helpers for the Particle System extension pack.
    //
    // JSON envelope builders used by both tools. The ParticleSystem domain has
    // no asset path of its own — it is a scene component — so paths_hint on
    // mutating tools is scoped to the host's scene path (mirrors ProBuilder).
    //
    // Naming: tool ids follow `unity_open_mcp_particle_system_<action>`
    // (snake_case domain prefix), mirroring the kebab `particle-system-*` ids
    // from the upstream Unity-MCP reference pack.
    static class ParticleSystemJson
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

        public static string Vec3(UnityEngine.Vector3 v) => $"[{v.x},{v.y},{v.z}]";
        public static string Vec4(UnityEngine.Vector4 v) => $"[{v.x},{v.y},{v.z},{v.w}]";
    }
}
