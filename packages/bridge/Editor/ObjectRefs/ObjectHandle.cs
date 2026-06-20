using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// Deliberate use of deprecated GetInstanceID() / EditorUtility.InstanceIDToObject() — see docs/code-conventions.md §Instance IDs.
#pragma warning disable CS0618
namespace UnityOpenMcpBridge.ObjectRefs
{
    /// <summary>
    /// Lightweight, serializable handle to a live <see cref="UnityEngine.Object"/>.
    ///
    /// Carries a canonical instance ID plus redundant fallback locators (type,
    /// name, hierarchy path, asset path/GUID) so the handle survives the LLM
    /// round-trip and degrades gracefully after domain reload — when instance
    /// IDs change, the path/name/asset locators allow re-acquisition without a
    /// full re-snapshot.
    ///
    /// Resolution priority:
    ///   1. objectId (instance ID)  — fast, canonical; invalidated by domain reload.
    ///   2. assetPath               — survives reload for persistent assets.
    ///   3. assetGuid               — survives reload for persistent assets.
    ///   4. path (GameObject)       — hierarchy path; survives reload.
    ///   5. component fallback       — find parent GameObject, then GetComponent.
    ///   6. name (GameObject)        — ambiguous under duplicates; last resort.
    ///
    /// Inspired by Unity-MCP's ObjectRef/GameObjectRef/ComponentRef and UCP's
    /// polymorphic ObjectLocator, translated to the bridge's hand-rolled JSON
    /// conventions (no Newtonsoft dependency).
    /// </summary>
    static class ObjectHandle
    {
        public const string ObjectIdKey = "objectId";
        public const string TypeKey = "type";
        public const string NameKey = "name";
        public const string PathKey = "path";
        public const string AssetPathKey = "assetPath";
        public const string AssetGuidKey = "assetGuid";
        public const string GameObjectPathKey = "gameObjectPath";
        public const string GameObjectIdKey = "gameObjectId";

        /// <summary>
        /// Serialize a live <see cref="UnityEngine.Object"/> into a JSON handle.
        /// Emits the canonical instance ID plus every available fallback locator
        /// so the handle can be re-acquired after domain reload.
        /// </summary>
        public static string Serialize(UnityEngine.Object obj)
        {
            if (obj == null) return "null";

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append('"').Append(ObjectIdKey).Append("\":").Append(obj.GetInstanceID());
            sb.Append(",\"").Append(TypeKey).Append("\":\"").Append(Escape(obj.GetType().FullName)).Append('"');
            sb.Append(",\"").Append(NameKey).Append("\":\"").Append(Escape(obj.name)).Append('"');

            // Asset locators — survive domain reload for persistent assets.
            var assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(assetPath))
            {
                sb.Append(",\"").Append(AssetPathKey).Append("\":\"").Append(Escape(assetPath)).Append('"');
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(guid))
                    sb.Append(",\"").Append(AssetGuidKey).Append("\":\"").Append(Escape(guid)).Append('"');
            }

            // GameObject / Component hierarchy locators — survive domain reload.
            if (obj is GameObject go)
            {
                var path = GetHierarchyPath(go);
                if (!string.IsNullOrEmpty(path))
                    sb.Append(",\"").Append(PathKey).Append("\":\"").Append(Escape(path)).Append('"');
            }
            else if (obj is Component comp)
            {
                var parentGo = comp.gameObject;
                var parentPath = GetHierarchyPath(parentGo);
                if (!string.IsNullOrEmpty(parentPath))
                    sb.Append(",\"").Append(GameObjectPathKey).Append("\":\"").Append(Escape(parentPath)).Append('"');
                sb.Append(",\"").Append(GameObjectIdKey).Append("\":").Append(parentGo.GetInstanceID());
            }

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Resolve a handle to a live <see cref="UnityEngine.Object"/> using the
        /// priority-ordered locator chain. Returns null and sets <paramref name="error"/>
        /// with agent-actionable guidance when all locators fail.
        /// </summary>
        public static UnityEngine.Object Resolve(
            int instanceId,
            string typeName,
            string name,
            string path,
            string assetPath,
            string assetGuid,
            out string error,
            string gameObjectPath = null,
            int gameObjectId = 0)
        {
            // 1. Canonical instance ID — fast, but invalidated by domain reload.
            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj != null)
                {
                    error = null;
                    return obj;
                }
            }

            // Resolve the target type once for the remaining fallbacks.
            var targetType = TryResolveType(typeName);

            // 2. Asset path — survives domain reload for persistent assets.
            if (!string.IsNullOrEmpty(assetPath))
            {
                var obj = targetType != null
                    ? AssetDatabase.LoadAssetAtPath(assetPath, targetType)
                    : AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (obj != null)
                {
                    error = null;
                    return obj;
                }
            }

            // 3. Asset GUID — survives domain reload for persistent assets.
            if (!string.IsNullOrEmpty(assetGuid))
            {
                var guidPath = AssetDatabase.GUIDToAssetPath(assetGuid);
                if (!string.IsNullOrEmpty(guidPath))
                {
                    var obj = targetType != null
                        ? AssetDatabase.LoadAssetAtPath(guidPath, targetType)
                        : AssetDatabase.LoadMainAssetAtPath(guidPath);
                    if (obj != null)
                    {
                        error = null;
                        return obj;
                    }
                }
            }

            // 4. GameObject hierarchy path — survives domain reload.
            if (!string.IsNullOrEmpty(path) && targetType == typeof(GameObject))
            {
                var go = FindByPath(path);
                if (go != null)
                {
                    error = null;
                    return go;
                }
            }

            // 5. Component fallback — find the parent GameObject, then GetComponent.
            if (targetType != null && typeof(Component).IsAssignableFrom(targetType))
            {
                GameObject parent = null;
                if (gameObjectId != 0)
                    parent = EditorUtility.InstanceIDToObject(gameObjectId) as GameObject;
                if (parent == null && !string.IsNullOrEmpty(gameObjectPath))
                    parent = FindByPath(gameObjectPath);

                if (parent != null)
                {
                    var comp = parent.GetComponent(targetType);
                    if (comp != null)
                    {
                        error = null;
                        return comp;
                    }
                }
            }

            // 6. Last resort: name lookup (ambiguous under duplicates).
            if (!string.IsNullOrEmpty(name) && targetType == typeof(GameObject))
            {
                var go = FindByName(name);
                if (go != null)
                {
                    error = null;
                    return go;
                }
            }

            error = BuildStaleHandleError(instanceId, typeName);
            return null;
        }

        /// <summary>
        /// Resolve from a raw JSON handle string (e.g. an arg passed by the LLM).
        /// </summary>
        public static UnityEngine.Object ResolveJson(string handleJson, out string error)
        {
            if (string.IsNullOrEmpty(handleJson) || handleJson.Trim() == "null")
            {
                error = "Object handle is null.";
                return null;
            }

            // Bare integer → instance ID shorthand.
            var trimmed = handleJson.Trim();
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bareId))
            {
                return Resolve(bareId, null, null, null, null, null, out error);
            }

            var instanceId = JsonBody.GetInt(handleJson, ObjectIdKey, 0);
            if (instanceId == 0)
                instanceId = JsonBody.GetInt(handleJson, "instanceId", 0);

            var typeName = JsonBody.GetString(handleJson, TypeKey);
            if (typeName == null)
                typeName = JsonBody.GetString(handleJson, "$type");

            var name = JsonBody.GetString(handleJson, NameKey);
            var path = JsonBody.GetString(handleJson, PathKey);
            var assetPath = JsonBody.GetString(handleJson, AssetPathKey);
            var assetGuid = JsonBody.GetString(handleJson, AssetGuidKey);
            var gameObjectPath = JsonBody.GetString(handleJson, GameObjectPathKey);
            var gameObjectId = JsonBody.GetInt(handleJson, GameObjectIdKey, 0);

            return Resolve(instanceId, typeName, name, path, assetPath, assetGuid,
                out error, gameObjectPath, gameObjectId);
        }

        /// <summary>
        /// Check whether a parsed arg value looks like a serialised object handle.
        /// </summary>
        public static bool LooksLikeHandle(object value)
        {
            if (!(value is string s)) return false;
            var trimmed = s.TrimStart();
            return trimmed.StartsWith("{") && trimmed.Contains(ObjectIdKey);
        }

        private static string BuildStaleHandleError(int instanceId, string typeName)
        {
            var sb = new StringBuilder(256);
            sb.Append("Object handle is stale or invalid");
            if (instanceId != 0)
                sb.Append($" (instance ID {instanceId} no longer exists)");
            if (!string.IsNullOrEmpty(typeName))
                sb.Append($" of type '{typeName}'");
            sb.Append(". Instance IDs change on domain reload (recompilation, enter/exit Play Mode). ");
            sb.Append("Re-acquire the object via 'unity_senses_scene_snapshot', ");
            sb.Append("'unity_senses_spatial_query', or 'unity_open_mcp_search_assets', ");
            sb.Append("then retry with the fresh handle.");
            return sb.ToString();
        }

        private static Type TryResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(typeName);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        // ============================ hierarchy helpers ============================

        private static string GetHierarchyPath(GameObject go)
        {
            if (go == null) return "";
            var t = go.transform;
            if (t.parent == null) return go.name;
            var sb = new StringBuilder(64);
            sb.Append(go.name);
            var p = t.parent;
            while (p != null)
            {
                sb.Insert(0, '/');
                sb.Insert(0, p.name);
                p = p.parent;
            }
            return sb.ToString();
        }

        private static GameObject FindByPath(string path)
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

        private static GameObject FindByName(string name)
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

        private static string Escape(string s)
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
