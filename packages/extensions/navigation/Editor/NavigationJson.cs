using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpExtensions.Navigation
{
    // M16 Plan 10 — shared helpers for the Navigation (NavMesh) extension
    // pack. Target resolution, JSON envelope builders, and the typed baking
    // helpers every NavMesh tool composes.
    //
    // Naming: tool ids follow `unity_open_mcp_navigation_<action>` (snake_case
    // domain prefix), mirroring the kebab `navigation-*` ids from the upstream
    // Unity-MCP reference pack.
    static class NavigationJson
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

    // Target resolver for Navigation tools. Mirrors the bridge's GameObject
    // addressing convention (instance_id > path > name) so agents can reuse
    // the same addressing they learned for gameobject_* / component_*.
    static class NavigationTargets
    {
        public static GameObject Resolve(int instanceId, string path, string name)
        {
            // instance_id wins.
            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is GameObject go) return go;
            }

            // path (slash-separated hierarchy from a root).
            if (!string.IsNullOrEmpty(path))
            {
                var go = FindByPath(path);
                if (go != null) return go;
            }

            // name-only fallback (first active match).
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
    }

    // Thin wrapper around the NavMesh area name table. `NavMesh.GetAreaByName`
    // returns the int cost area id; we surface both name and id so agents can
    // round-trip the value.
    static class NavigationAreas
    {
        public static int Resolve(string name, int fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            var id = NavMesh.GetAreaByName(name);
            return id != -1 ? id : fallback;
        }
    }
}
