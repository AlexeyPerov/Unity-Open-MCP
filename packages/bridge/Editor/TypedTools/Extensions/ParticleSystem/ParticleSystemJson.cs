// M18 Plan 3 — Particle System (UnityEngine.ParticleSystemModule, built-in)
// embedded domain tools.
//
// UNGATED: see ParticleSystemTools.cs for the rationale (core engine module,
// always present, former compile-gate never resolved). The namespace is
// `...Particles` to avoid the UnityEngine.ParticleSystem type collision.
// Ported verbatim (logic, JSON schema) from the former standalone extension
// pack — only the namespace changed.
using System.Text;

namespace UnityOpenMcpBridge.Extensions.Particles
{
    // Shared helpers for the Particle System embedded domain tools.
    //
    // JSON envelope builders used by both tools. The ParticleSystem domain has
    // no asset path of its own — it is a scene component — so paths_hint on
    // mutating tools is scoped to the host's scene path (mirrors ProBuilder).
    //
    // Naming: tool ids follow `unity_open_mcp_particle_system_<action>`
    // (snake_case domain prefix).
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
