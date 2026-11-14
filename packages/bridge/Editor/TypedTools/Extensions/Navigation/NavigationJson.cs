// M18 Plan 1 — Navigation (AI Navigation package) embedded domain tools.
//
// Compile-gated reference template for the TypedTools/Extensions/* layout.
// The whole file is compiled only when com.unity.ai.navigation is present:
// the owning sub-asmdef (com.alexeyperov.unity-open-mcp-bridge.Navigation.Editor)
// carries `defineConstraints: ["UNITY_OPEN_MCP_EXT_NAVIGATION"]`, and the bridge
// root asmdef sets that define via `versionDefines` when the package resolves.
// The `#if` below is belt-and-suspenders — it documents the gate at the source
// level and keeps the file buildable if it is ever moved out of the gated asmdef.
#if UNITY_OPEN_MCP_EXT_NAVIGATION
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEditor;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.NavigationExt
{
    // Shared helpers for the Navigation (NavMesh) embedded domain tools.
    // Target resolution, JSON envelope builders, and the typed baking helpers
    // every NavMesh tool composes.
    //
    // Naming: tool ids follow `unity_open_mcp_navigation_<action>` (snake_case
    // domain prefix).
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
        // The shared resolver (TypedTargets): loaded scenes and the open
        // prefab stage, scene_path / paths_hint narrowing, refused instance ids
        // (ambiguous_path and friends: see TypedTargets.RefusalJson); a
        // one-segment path also matches a nested GameObject of that name.
        public static GameObject Resolve(int instanceId, string path, string name)
            => UnityOpenMcpBridge.TypedTools.TypedTargets.ResolveLenient(instanceId, path, name);

        public static GameObject FindByPath(string path)
            => UnityOpenMcpBridge.TypedTools.TypedTargets.ResolveLenient(0, path, null);
    }

    // Thin wrapper around the NavMesh area name table. The Unity 6
    // UnityEngine.AI.NavMesh API exposes GetAreaFromName (not GetAreaByName);
    // older Unity versions used GetAreaByName. We surface both name and id so
    // agents can round-trip the value.
    static class NavigationAreas
    {
        public static int Resolve(string name, int fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            // Unity 6+ renamed GetAreaByName -> GetAreaFromName. Both return -1
            // when the name is unknown. Prefer the current name, fall back to
            // the legacy one for older editors.
            var id = NavMesh.GetAreaFromName(name);
            return id != -1 ? id : fallback;
        }
    }
}
#endif
