// M18 Plan 3 — ProBuilder (com.unity.probuilder) embedded domain tools.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_PROBUILDER. See ProBuilderTools.cs for
// the gate rationale. Ported verbatim (logic, JSON schema) from the former
// standalone extension pack — only the namespace changed.
#if UNITY_OPEN_MCP_EXT_PROBUILDER
#pragma warning disable CS0618
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.ProBuilder
{
    // Shared helpers for the ProBuilder embedded domain tools.
    //
    // Target resolution (instance_id > path > name), JSON envelope builders,
    // and the semantic face-direction matcher every ProBuilder tool composes.
    //
    // Naming: tool ids follow `unity_open_mcp_probuilder_<action>` (snake_case
    // domain prefix), mirroring the kebab `probuilder-*` ids from the upstream
    // Unity-MCP reference pack.
    static class ProBuilderJson
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

        public static string Vec3(Vector3 v) => $"[{v.x},{v.y},{v.z}]";
    }

    // Target resolver for ProBuilder tools. Mirrors the bridge's GameObject
    // addressing convention (instance_id > path > name) so agents can reuse
    // the same addressing they learned for gameobject_* / component_*.
    static class ProBuilderTargets
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

    // Semantic face selection by direction — port of FaceSelectionHelper from
    // the upstream Unity-AI-ProBuilder pack. Index-based selection (not
    // SceneView mouse picking) so agents can target faces without an
    // interactive editor.
    static class FaceSelection
    {
        // Dot-product threshold for face-normal matching (~45 degree tolerance).
        public const float DirectionThreshold = 0.7f;

        public static Vector3 DirectionVector(string direction)
        {
            switch ((direction ?? "").ToLowerInvariant())
            {
                case "up": return Vector3.up;
                case "down": return Vector3.down;
                case "left": return Vector3.left;
                case "right": return Vector3.right;
                case "forward": return Vector3.forward;
                case "back": return Vector3.back;
                default: return Vector3.zero;
            }
        }

        public static bool IsKnownDirection(string direction)
        {
            var d = (direction ?? "").ToLowerInvariant();
            return d == "up" || d == "down" || d == "left" ||
                   d == "right" || d == "forward" || d == "back";
        }

        // Returns matching face indices for a direction, or null + outError
        // when no faces match.
        public static int[] SelectByDirection(global::UnityEngine.ProBuilder.ProBuilderMesh mesh,
            string direction, out string error)
        {
            error = null;
            var target = DirectionVector(direction);
            var faces = mesh.faces;
            var positions = mesh.positions;
            var matches = new System.Collections.Generic.List<int>();

            for (int i = 0; i < faces.Count; i++)
            {
                var normal = CalculateFaceNormal(faces[i], positions);
                if (Vector3.Dot(normal.normalized, target) >= DirectionThreshold)
                    matches.Add(i);
            }

            if (matches.Count == 0)
            {
                error = $"No faces facing '{direction}'.";
                return null;
            }
            return matches.ToArray();
        }

        public static Vector3 CalculateFaceNormal(global::UnityEngine.ProBuilder.Face face,
            System.Collections.Generic.IList<Vector3> positions)
        {
            var indices = face.indexes;
            if (indices.Count < 3) return Vector3.up;
            var v0 = positions[indices[0]];
            var v1 = positions[indices[1]];
            var v2 = positions[indices[2]];
            return Vector3.Cross(v1 - v0, v2 - v0).normalized;
        }
    }
}
#endif
