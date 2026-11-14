// M18 Plan 7 — Splines (com.unity.splines) embedded domain tools.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_SPLINES. The owning sub-asmdef
// (com.alexeyperov.unity-open-mcp-bridge.Splines.Editor) carries
// `defineConstraints: ["UNITY_OPEN_MCP_EXT_SPLINES"]` and references
// Unity.Splines; the bridge root asmdef sets the define via `versionDefines`
// when the package resolves. This is the first backlog domain shipped under
// M18 Plan 7 — proof that the embedded + grouped model extends to compile-
// gated domain packs. (Cinemachine, the recommended first domain, was swapped
// for Splines per the plan's fallback path — see the M18 changelog.)
//
// Splines is compile-gate-only (single stable API across com.unity.splines
// 1.x and 2.x). No reflection probing for version detection — reflection is
// reserved for the Cinemachine 2.x/3.x split (see docs/contributing/extensions.md §Reflection
// fallback policy). The reflective escape hatch below (SplinesModify) targets
// Spline-level serialized fields; it is not a version-detection layer.
#if UNITY_OPEN_MCP_EXT_SPLINES
using System.Text;
using UnityEngine;
using UnityEditor;
using Unity.Mathematics;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.SplinesExt
{
    // Shared helpers for the Splines embedded domain tools.
    //
    // Target resolution (instance_id > path > name), JSON envelope builders,
    // and the float3/vector parsing every Splines tool composes. Mirrors the
    // ProBuilderJson / NavigationJson helper shape so the domain packs read
    // consistently.
    //
    // Naming: tool ids follow `unity_open_mcp_splines_<action>` (snake_case
    // domain prefix).
    static class SplinesJson
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

        // A-R4 — render floats with invariant culture. Bare interpolation
        // ($"{v.x}") uses the OS culture, so de/fr/ru locales emit "1,5" and
        // the vector array parses as valid JSON with the wrong element count.
        public static string Vec3(Vector3 v)
            => "[" + Num(v.x) + "," + Num(v.y) + "," + Num(v.z) + "]";

        public static string Num(float v)
            => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Target resolver for Splines tools. Mirrors the bridge's GameObject
    // addressing convention (instance_id > path > name) so agents reuse the
    // same addressing they learned for gameobject_* / component_*.
    static class SplinesTargets
    {
        // The shared resolver (TypedTargets): loaded scenes and the open
        // prefab stage, scene_path / paths_hint narrowing, refused instance ids
        // (ambiguous_path and friends: see TypedTargets.RefusalJson); a
        // one-segment path also matches a nested GameObject of that name.
        public static GameObject Resolve(int instanceId, string path, string name)
            => UnityOpenMcpBridge.TypedTools.TypedTargets.ResolveLenient(instanceId, path, name);

        public static GameObject FindByPath(string path)
            => UnityOpenMcpBridge.TypedTools.TypedTargets.ResolveLenient(0, path, null);

        // Canonical: relative to the prefab root for a prefab-stage object.
        public static string BuildPath(GameObject go) => UnityOpenMcpBridge.TypedTools.TypedTargets.HierarchyPath(go);
    }
}
#endif
