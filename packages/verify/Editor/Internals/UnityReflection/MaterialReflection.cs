using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityOpenMcpVerify.Internals.UnityReflection
{
    /// <summary>
    /// Reflection-backed accessors for internal Material / ShaderUtil members
    /// used by the materials verify rule (variant chains, GPU instancing
    /// support, SRP batcher compatibility). Ported verbatim from the source
    /// scanner so the rule compiles against any Unity version without
    /// InternalsVisibleTo.
    /// </summary>
    public static class MaterialReflection
    {
        private static readonly PropertyInfo IsVariantProperty =
            typeof(Material).GetProperty("isVariant",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly PropertyInfo ParentProperty =
            typeof(Material).GetProperty("parent",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo HasInstancingMethod =
            typeof(ShaderUtil).GetMethod("HasInstancing",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // Unity exposes this under two different names across versions.
        private static readonly MethodInfo SrpBatcherMethod =
            typeof(ShaderUtil).GetMethod("IsShaderSrpBatcherCompatible",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? typeof(ShaderUtil).GetMethod("IsSRPBatcherShaderCompatible",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        public static bool TryGetIsMaterialVariant(Material material, out bool isVariant)
        {
            isVariant = false;
            if (material == null || IsVariantProperty == null) return false;
            try
            {
                var value = IsVariantProperty.GetValue(material);
                if (value is bool b) { isVariant = b; return true; }
            }
            catch { }
            return false;
        }

        // Resolves the variant's parent material. Mirrors the source: tries the
        // internal `parent` property first, then falls back to a SerializedObject
        // m_Parent object reference. Returns the parent Material (or null) and
        // reports whether the parent link is broken (parent exists in-memory but
        // resolves to no asset path).
        public static bool TryGetParentMaterial(Material material, out Material parent, out bool parentLinkBroken)
        {
            parent = null;
            parentLinkBroken = false;
            if (material == null) return false;

            try
            {
                if (ParentProperty != null)
                {
                    parent = ParentProperty.GetValue(material) as Material;
                }

                if (parent == null)
                {
                    // SerializedObject fallback.
                    using (var so = new SerializedObject(material))
                    {
                        var prop = so.FindProperty("m_Parent");
                        if (prop != null)
                            parent = prop.objectReferenceValue as Material;
                    }
                }

                if (parent != null)
                {
                    // Fully qualified: this package also defines the namespace
                    // UnityOpenMcpVerify.Internals.AssetDatabase, which would
                    // otherwise shadow UnityEditor.AssetDatabase here.
                    var parentPath = UnityEditor.AssetDatabase.GetAssetPath(parent);
                    parentLinkBroken = string.IsNullOrEmpty(parentPath);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static int ComputeVariantChainDepth(Material material)
        {
            var depth = 0;
            var visited = new HashSet<Material>();
            var current = material;
            while (current != null && visited.Add(current))
            {
                if (depth > 64) break;
                if (!TryGetIsMaterialVariant(current, out var isVariant) || !isVariant) break;
                if (!TryGetParentMaterial(current, out var parent, out _) || parent == null) break;
                current = parent;
                depth++;
            }
            return depth;
        }

        public static int ComputeVariantOverrideCount(Material child, Material parent)
        {
            if (child == null || parent == null) return 0;
            var childShader = child.shader;
            var parentShader = parent.shader;
            if (childShader != parentShader) return 999;

            var count = 0;
            if (child.renderQueue != parent.renderQueue) count++;
            if (child.enableInstancing != parent.enableInstancing) count++;

            var childKeywords = new HashSet<string>(child.shaderKeywords);
            var parentKeywords = new HashSet<string>(parent.shaderKeywords);
            if (!childKeywords.SetEquals(parentKeywords)) count++;

            var shader = childShader;
            if (shader == null) return count;
            var propCount = shader.GetPropertyCount();
            for (var i = 0; i < propCount; i++)
            {
                var propName = shader.GetPropertyName(i);
                var propType = shader.GetPropertyType(i);
                if (propType == ShaderPropertyType.Texture)
                {
                    if (TexturesDifferByAssetPath(child, parent, propName)) count++;
                }
                else if (propType == ShaderPropertyType.Color)
                {
                    if (child.GetColor(propName) != parent.GetColor(propName)) count++;
                }
                else if (propType == ShaderPropertyType.Vector)
                {
                    if (child.GetVector(propName) != parent.GetVector(propName)) count++;
                }
                else
                {
                    if (Mathf.Abs(child.GetFloat(propName) - parent.GetFloat(propName)) > 0.0001f) count++;
                }
            }
            return count;
        }

        public static bool? TryGetGpuInstancingSupport(Shader shader)
        {
            if (shader == null || HasInstancingMethod == null) return null;
            try
            {
                var result = HasInstancingMethod.Invoke(null, new object[] { shader });
                if (result is bool b) return b;
            }
            catch { }
            return null;
        }

        public static bool? TryGetSrpBatcherCompatibility(Shader shader)
        {
            if (shader == null || SrpBatcherMethod == null) return null;
            try
            {
                var result = SrpBatcherMethod.Invoke(null, new object[] { shader });
                if (result is bool b) return b;
            }
            catch { }
            return null;
        }

        private static bool TexturesDifferByAssetPath(Material a, Material b, string propName)
        {
            var ta = a.GetTexture(propName);
            var tb = b.GetTexture(propName);
            // Fully qualified to avoid the UnityOpenMcpVerify.Internals.AssetDatabase
            // namespace shadowing UnityEditor.AssetDatabase.
            var pa = ta != null ? UnityEditor.AssetDatabase.GetAssetPath(ta) : null;
            var pb = tb != null ? UnityEditor.AssetDatabase.GetAssetPath(tb) : null;
            return pa != pb;
        }
    }
}
