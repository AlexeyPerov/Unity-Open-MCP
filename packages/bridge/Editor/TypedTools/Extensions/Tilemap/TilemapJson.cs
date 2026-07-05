// M20 Plan 6 / T20.6.3 — Tilemap (com.unity.2d.tilemap + .extras) embedded
// domain tools.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_TILEMAP (core tilemap module) and
// additionally by UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS for RuleTile. The owning
// sub-asmdef carries `defineConstraints: ["UNITY_OPEN_MCP_EXT_TILEMAP"]`; the
// RuleTile tool adds an inner `#if UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS` guard so
// it returns a clean install error when the extras package is absent (two
// defines, two guards — the canonical two-dependency pattern).
//
// Tilemap has a single stable public API (UnityEngine.Tilemaps namespace);
// RuleTile lives in com.unity.2d.tilemap.extras (UnityEditor.Tilemaps
// namespace). When the core package is absent the tools are not compiled in
// and the capability surface reports the domain as `available: false
// (dependency missing: com.unity.2d.tilemap)`.
//
// Naming: tool ids follow `unity_open_mcp_tilemap_<action>` (snake_case
// domain prefix).
#if UNITY_OPEN_MCP_EXT_TILEMAP
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.Tilemaps;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.TilemapExt
{
    // Shared helpers for the Tilemap embedded domain tools.
    //
    // JSON envelope builders + target resolution (instance_id > path > name).
    // Mirrors the SplinesJson / TimelineJson helper shape.
    internal static class TilemapJson
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

        public static string Vec3Int(Vector3Int v) => $"[{v.x},{v.y},{v.z}]";
    }

    internal static class TilemapTargets
    {
        public static GameObject Resolve(int instanceId, string path, string name)
        {
            if (instanceId != 0)
            {
                var obj = InstanceId.ToObject(instanceId);
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
