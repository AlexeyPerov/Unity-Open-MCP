// M20 Plan 3 / T20.3.2 — UI (uGUI) embedded domain tools.
//
// Shared helpers for the UI embedded domain tools: target resolution,
// JSON envelope builders. The Canvas / CanvasScaler / GraphicRaycaster /
// Image / Text / Button / Slider / Toggle / InputField / LayoutGroup /
// EventSystem types live in the built-in UnityEngine.IMGUIModule,
// UnityEngine.UI, and UnityEngine.UIElementsModule — present in every Unity
// install (uGUI ships with the engine). TextMesh Pro (TMP_Text) is OPTIONAL
// and detected at call time — when an agent requests element_type=TMP_Text and
// TMP is absent, the tool returns a structured `tmp_package_required` error
// instead of a silent legacy-Text fallback. The tools compile regardless (no
// compile-time TMP reference). This domain is GATED on the com.unity.ugui
// package via the UNITY_OPEN_MCP_EXT_UI define (self-referential versionDefine
// + defineConstraint on the owning sub-asmdef); see UITools.cs for the gating
// details. The `ui` tool group is still hidden from ListTools until the
// session activates it via unity_open_mcp_manage_tools (group visibility is a
// session concern, independent of compile-gating).
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.UI
{
    // Shared JSON envelope + escape helpers. Mirrors AudioJson / LightingJson
    // so each embedded domain has a self-contained helper it can evolve
    // independently.
    static class UIJson
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

    // Target resolver for UI tools. Mirrors the bridge's GameObject addressing
    // convention (instance_id > path > name) so agents reuse the same
    // addressing they learned for gameobject_* / component_*.
    static class UITargets
    {
        public static GameObject Resolve(int instanceId, string path, string name)
        {
            // instance_id wins.
            if (instanceId != 0)
            {
                var obj = InstanceId.ToObject(instanceId);
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
