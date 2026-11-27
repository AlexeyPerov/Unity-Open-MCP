// M20 Plan 6 / T20.6.2 — Timeline (com.unity.timeline) embedded domain tools.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_TIMELINE. The owning sub-asmdef
// (com.alexeyperov.unity-open-mcp-bridge.Timeline.Editor) carries
// `defineConstraints: ["UNITY_OPEN_MCP_EXT_TIMELINE"]` and references
// Unity.Timeline; the bridge root asmdef sets the define via `versionDefines`
// when the package resolves.
//
// Timeline has a single stable public API across com.unity.timeline 1.x —
// compile-gate-only, no reflection probing. When the package is absent the
// tools are not compiled in and the capability surface reports the domain as
// `available: false (dependency missing: com.unity.timeline)`.
//
// Naming: tool ids follow `unity_open_mcp_timeline_<action>` (snake_case
// domain prefix).
#if UNITY_OPEN_MCP_EXT_TIMELINE
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.TimelineExt
{
    // Shared helpers for the Timeline embedded domain tools.
    //
    // JSON envelope builders, target resolution (instance_id > path > name),
    // and the float/vector parsing every Timeline tool composes. Mirrors the
    // SplinesJson / NavigationJson helper shape.
    internal static class TimelineJson
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

    internal static class TimelineTargets
    {
        // The shared resolver (TypedTargets): loaded scenes and the open
        // prefab stage, scene_path / paths_hint narrowing, refused instance ids
        // (ambiguous_path and friends: see TypedTargets.RefusalJson); a
        // one-segment path also matches a nested GameObject of that name.
        public static GameObject Resolve(long instanceId, string path, string name)
            => UnityOpenMcpBridge.TypedTools.TypedTargets.ResolveLenient(instanceId, path, name);

        public static GameObject FindByPath(string path)
            => UnityOpenMcpBridge.TypedTools.TypedTargets.ResolveLenient(0, path, null);

        // Canonical: relative to the prefab root for a prefab-stage object.
        public static string BuildPath(GameObject go) => UnityOpenMcpBridge.TypedTools.TypedTargets.HierarchyPath(go);
    }
}
#endif
