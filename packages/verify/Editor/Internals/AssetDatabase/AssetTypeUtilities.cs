using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpVerify.Internals.AssetDatabase
{
    public static class AssetTypeUtilities
    {
        // Unity text-serialized YAML asset extensions. Each helper below names a
        // distinct policy that previously lived as inline extension comparisons
        // in ReferenceGraph.cs and Rules/*/Scanner.cs — keep them separate so
        // the intent at each call site stays explicit (the sets are NOT the same).

        /// <summary>
        /// Assets that can embed <c>m_AssetGUID</c> references in their text
        /// serialization: ScriptableObject-like <c>.asset</c> files, prefabs,
        /// and scenes. Used by <see cref="ReferenceGraph"/> to decide whether to
        /// scan file text for asset-GUID references beyond what
        /// <c>AssetDatabase.GetDependencies</c> reports.
        /// </summary>
        public static bool CanContainAssetReferences(string assetPath)
        {
            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            return ext == ".asset" || ext == ".prefab" || ext == ".unity";
        }

        /// <summary>
        /// Assets whose text serialization the dependency-rule scanner reads
        /// line-by-line to build a reference/cycle graph. Broader than
        /// <see cref="CanContainAssetReferences"/> because materials, animator
        /// controllers, and animation clips are also text-serialized YAML that
        /// can reference other assets.
        /// </summary>
        public static bool IsTextSerializedYaml(string assetPath)
        {
            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            return ext == ".prefab" || ext == ".unity" || ext == ".asset" ||
                   ext == ".mat" || ext == ".controller" || ext == ".anim";
        }

        /// <summary>
        /// Scene and prefab files — the assets that can host a GameObject /
        /// component hierarchy. Used to narrow the candidate set when scanning
        /// for references that only live inside a hierarchy (e.g. Terrain
        /// references wired through components).
        /// </summary>
        public static bool IsSceneOrPrefab(string assetPath)
        {
            var ext = Path.GetExtension(assetPath);
            return ext.Equals(".prefab", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".unity", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsValidType(string path, Type type)
        {
            // A null type is the normal result for paths Unity does not import
            // as a typed asset (.asmdef, .cs, .meta, pending imports, …). That
            // is not an error condition — just skip the path silently. The
            // downstream CanAnalyzeType filter already restricts analysis to
            // GameObject / SceneAsset / ScriptableObject.
            if (type == null)
                return false;

            if (type == typeof(DefaultAsset))
                return false;

            return true;
        }

        public static bool CanAnalyzeType(Type type)
        {
            return type == typeof(GameObject) || type == typeof(SceneAsset)
                   || DerivesFromOrEqual(type, typeof(ScriptableObject));
        }

        private static bool DerivesFromOrEqual(Type a, Type b)
        {
            return b == a || b.IsAssignableFrom(a);
        }

        public static string GetReadableTypeName(Type type)
        {
            if (type != null)
            {
                var typeName = type.ToString();
                typeName = typeName.Replace("UnityEngine.", string.Empty);
                typeName = typeName.Replace("UnityEditor.", string.Empty);
                return typeName;
            }

            return "Unknown Type";
        }

        public static bool IsInResources(string path)
        {
            return path.Contains("/Resources/");
        }
    }
}
