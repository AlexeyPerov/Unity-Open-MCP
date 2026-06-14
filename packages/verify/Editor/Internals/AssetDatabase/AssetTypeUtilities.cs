// Extracted from Unity-Scanner: Editor/Utilities/AssetDatabase/USAssetTypeUtilities.cs

using System;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpVerify.Internals.AssetDatabase
{
    public static class AssetTypeUtilities
    {
        public static bool IsValidType(string path, Type type)
        {
            if (type != null)
            {
                if (type == typeof(DefaultAsset))
                    return false;
                return true;
            }

            Debug.LogWarning($"Invalid asset type found at {path}");
            return false;
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
