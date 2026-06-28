// M20 Plan 6 / T20.6.2 — Timeline (com.unity.timeline) embedded domain tools.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_TIMELINE. The owning sub-asmdef
// (com.alexeyperov.unity-open-mcp-bridge.Timeline.Editor) carries
// `defineConstraints: ["UNITY_OPEN_MCP_EXT_TIMELINE"]` and references
// Unity.Timeline; the bridge root asmdef sets the define via `versionDefines`
// when the package resolves.
//
// Timeline has a single stable public API across com.unity.timeline 1.x (the
// reference pack wraps 1.8.12) — compile-gate-only, no reflection probing.
// When the package is absent the tools are not compiled in and the capability
// surface reports the domain as `available: false (dependency missing:
// com.unity.timeline)`.
//
// Naming: tool ids follow `unity_open_mcp_timeline_<action>` (snake_case
// domain prefix), mirroring the kebab `timeline-*` ids in the upstream
// Unity-AI-Timeline reference pack.
#if UNITY_OPEN_MCP_EXT_TIMELINE
#pragma warning disable CS0618
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.Timeline
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
        public static GameObject Resolve(int instanceId, string path, string name)
        {
            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is GameObject go) return go;
            }

            if (!string.IsNullOrEmpty(path))
            {
                var go = FindByPath(path);
                if (go != null) return go;
            }

            if (!string.IsNullOrEmpty(name))
            {
                var roots = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude);
                foreach (var root in roots)
                {
                    if (root.gameObject.name == name) return root.gameObject;
                }
            }

            return null;
        }

        public static GameObject FindByPath(string path)
        {
            var parts = path.Split('/');
            var roots = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude);
            foreach (var root in roots)
            {
                if (root.gameObject.name == parts[0])
                {
                    var current = root.gameObject;
                    bool match = true;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var child = current.transform.Find(parts[i]);
                        if (child == null) { match = false; break; }
                        current = child.gameObject;
                    }
                    if (match) return current;
                }
            }
            return null;
        }

        public static string BuildPath(GameObject go)
        {
            var sb = new StringBuilder();
            var t = go.transform;
            while (t != null)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }
    }
}
#endif
