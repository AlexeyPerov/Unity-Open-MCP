// M20 Plan 3 / T20.3.1 — Audio embedded domain tools.
//
// Shared helpers for the Audio embedded domain tools: target resolution,
// JSON envelope builders. The AudioSource / AudioListener / AudioMixer /
// AudioMixerGroup types live in the built-in engine audio module
// (UnityEngine.AudioModule) and are present in every Unity install, so this
// domain ships UNGATED — no UNITY_OPEN_MCP_EXT_AUDIO define, no sub-asmdef
// defineConstraints. The `audio` tool group is still hidden from ListTools
// until the session activates it via `unity_open_mcp_manage_tools` (group
// visibility is a session concern, independent of compile-gating).
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.AudioExt
{
    // Shared JSON envelope + escape helpers. Mirrors LightingJson /
    // NavigationJson so each embedded domain has a self-contained helper it
    // can evolve independently.
    static class AudioJson
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

    // Target resolver for Audio tools. Mirrors the bridge's GameObject
    // addressing convention (instance_id > path > name) so agents reuse the
    // same addressing they learned for gameobject_* / component_*.
    static class AudioTargets
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
