#pragma warning disable CS0618
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpBridge.TypedTools
{
    // Shared helpers for the M16 typed tools (prefab/material/shader). Mirrors
    // the instance_id > path > name addressing priority used by
    // spatial-query.ts / ManageSpatialQueryTool.cs so agents can use the same
    // target address vocabulary across the typed surface.
    public static class TypedTargets
    {
        public static GameObject ResolveGameObject(int instanceId, string path, string name)
        {
            if (instanceId != 0)
            {
                var byId = FindByInstanceId(instanceId);
                if (byId != null) return byId;
            }
            if (!string.IsNullOrEmpty(path))
            {
                var byPath = FindByPath(path);
                if (byPath != null) return byPath;
            }
            if (!string.IsNullOrEmpty(name))
            {
                var byName = FindByName(name);
                if (byName != null) return byName;
            }
            return null;
        }

        public static GameObject FindByInstanceId(int instanceId)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = FindInHierarchyById(root, instanceId);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static GameObject FindInHierarchyById(GameObject go, int instanceId)
        {
            if (go.GetInstanceID() == instanceId) return go;
            foreach (Transform child in go.transform)
            {
                var found = FindInHierarchyById(child.gameObject, instanceId);
                if (found != null) return found;
            }
            return null;
        }

        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var trimmed = path.Trim('/');
            var segments = trimmed.Split('/');
            if (segments.Length == 0) return null;

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != segments[0]) continue;
                    var resolved = WalkPath(root.transform, segments, 1);
                    if (resolved != null) return resolved;
                }
            }
            return null;
        }

        private static GameObject WalkPath(Transform current, string[] segments, int index)
        {
            if (index >= segments.Length) return current.gameObject;
            foreach (Transform child in current)
            {
                if (child.name == segments[index])
                {
                    var resolved = WalkPath(child, segments, index + 1);
                    if (resolved != null) return resolved;
                }
            }
            return null;
        }

        public static GameObject FindByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = FindInHierarchyByName(root, name);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static GameObject FindInHierarchyByName(GameObject go, string name)
        {
            if (go.name == name) return go;
            foreach (Transform child in go.transform)
            {
                var found = FindInHierarchyByName(child.gameObject, name);
                if (found != null) return found;
            }
            return null;
        }

        // Build a slash-joined hierarchy path for a GameObject (root-relative).
        // Used by prefab_instantiate / prefab_create to return the new
        // instance's locator.
        public static string HierarchyPath(GameObject go)
        {
            if (go == null) return "";
            var sb = new StringBuilder(64);
            var t = go.transform;
            sb.Append(go.name);
            while (t.parent != null)
            {
                sb.Insert(0, '/');
                sb.Insert(0, t.parent.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        // Escape a string for inline JSON.
        public static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
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
            return sb.ToString();
        }
    }
}
