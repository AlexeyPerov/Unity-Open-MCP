using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityOpenMcpVerify.Internals.UnityReflection;

namespace UnityOpenMcpVerify.Rules.Materials
{
    public static class Scanner
    {
        // Builtin shader names ported verbatim from the source scanner.
        private static readonly HashSet<string> BuiltinShaderNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Standard",
            "Standard (Specular setup)",
            "Standard (Roughness setup)",
            "Unlit/Color",
            "Unlit/Texture",
            "Unlit/Transparent",
            "Unlit/Transparent Cutout",
            "Particles/Standard Unlit",
            "Legacy Shaders/Diffuse",
            "Legacy Shaders/Specular",
            "Legacy Shaders/Bumped Diffuse",
            "Legacy Shaders/Bumped Specular",
            "Mobile/Diffuse",
            "Mobile/Unlit (Supports Lightmap)",
            "Mobile/VertexLit",
            "Mobile/VertexLit-OnlyDirectionalLights",
            "Mobile/Particles/Alpha Blended",
            "Mobile/Particles/Additive",
        };

        private const string InternalErrorShader = "Hidden/InternalErrorShader";

        /// <summary>Scan materials under the scoped paths. Per-asset detections
        /// run in every mode; cross-asset detections (duplicates, unused,
        /// variant hierarchies) only run when <paramref name="fullScan"/> is
        /// true because they need the full material set as context.</summary>
        public static void ScanPaths(string[] paths, MaterialsScanSettings settings, List<MaterialData> sink, List<RendererData> renderers, bool fullScan)
        {
            if (paths == null || paths.Length == 0) return;

            // Phase 1: collect the material set + renderer-side warnings.
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(path)) continue;

                var data = CreateMaterialData(path, settings);
                if (data != null) sink.Add(data);
            }

            // Renderer-side scan: GameObjects in scope with Renderer components.
            if (fullScan && (settings.CheckMissingShader || settings.CheckUnusedMaterials || settings.CheckDuplicateMaterials))
            {
                ScanRenderers(paths, renderers);
            }

            // Phase 2: cross-asset post-passes (full-scan only).
            if (fullScan)
            {
                if (settings.CheckDuplicateMaterials)
                    DetectDuplicateMaterials(sink);
                ApplyReferencedByPaths(sink, renderers);
                if (settings.CheckUnusedMaterials)
                    DetectUnusedMaterials(sink);
                if (settings.CheckVariants || settings.CheckGpuInstancing || settings.CheckSrpBatcher)
                    AnalyzeVariantsAndPerformance(sink, settings);
            }
        }

        // -------------------------------------------------------------------
        // Per-material data collection + warnings (ported)
        // -------------------------------------------------------------------

        private static MaterialData CreateMaterialData(string path, MaterialsScanSettings settings)
        {
            var data = new MaterialData(path)
            {
                Name = Path.GetFileName(path),
            };

            Material material;
            try { material = AssetDatabase.LoadAssetAtPath<Material>(path); }
            catch
            {
                data.Issues.Add(new MaterialIssue("unable_to_load",
                    "Material could not be loaded.", VerifySeverity.Error));
                return data;
            }

            if (material == null)
            {
                data.Issues.Add(new MaterialIssue("unable_to_load",
                    "Material could not be loaded.", VerifySeverity.Error));
                return data;
            }

            var shader = material.shader;
            data.ShaderName = shader != null ? shader.name : "Unknown";
            data.RenderQueue = material.renderQueue;
            data.GpuInstancingEnabled = material.enableInstancing;
            data.EnabledKeywords = new List<string>(material.shaderKeywords);

            if (shader != null)
            {
                data.ShaderDefaultRenderQueue = shader.renderQueue;
                PopulateMaterialProperties(data, material);
                data.Fingerprint = ComputeMaterialFingerprint(material);
            }

            FindMaterialWarnings(data, material, shader, settings);
            return data;
        }

        private static void FindMaterialWarnings(MaterialData data, Material material, Shader shader, MaterialsScanSettings settings)
        {
            if (shader == null)
            {
                if (settings.CheckMissingShader)
                {
                    data.IsMissingShader = true;
                    data.Issues.Add(new MaterialIssue("missing_shader",
                        "Material shader is null.", VerifySeverity.Error));
                }
                return;
            }

            if (shader.name == InternalErrorShader)
            {
                if (settings.CheckMissingShader)
                {
                    data.IsMissingShader = true;
                    data.Issues.Add(new MaterialIssue("missing_shader",
                        "Material references the InternalErrorShader — the original shader failed to compile or is missing.",
                        VerifySeverity.Error));
                }
                return;
            }

            if (settings.CheckBuiltinShader && IsBuiltinShader(shader.name))
            {
                data.IsBuiltinShader = true;
                data.Issues.Add(new MaterialIssue("builtin_shader",
                    $"Material uses built-in shader '{shader.name}'.", VerifySeverity.Warning));
            }

            if (settings.CheckRenderQueueOverride && data.HasRenderQueueOverride)
            {
                data.Issues.Add(new MaterialIssue("render_queue_override",
                    $"Render queue override: {data.RenderQueue} (shader default: {data.ShaderDefaultRenderQueue}).",
                    VerifySeverity.Warning));
            }

            // Per-property texture checks via the public Shader API.
            if (settings.CheckMissingTexture || settings.CheckBuiltinTexture)
            {
                var propCount = shader.GetPropertyCount();
                for (var i = 0; i < propCount; i++)
                {
                    if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    var propName = shader.GetPropertyName(i);
                    var texture = material.GetTexture(propName);
                    var texturePath = texture != null ? AssetDatabase.GetAssetPath(texture) : null;

                    if (texture == null)
                    {
                        if (settings.CheckMissingTexture)
                        {
                            data.TextureWarnings.Add(new TextureWarning(propName, "missing_texture",
                                $"Texture is null at '{propName}'."));
                        }
                    }
                    else if (texturePath != null && texturePath.Contains("unity_builtin"))
                    {
                        if (settings.CheckBuiltinTexture)
                        {
                            data.TextureWarnings.Add(new TextureWarning(propName, "builtin_texture",
                                $"unity_builtin texture at '{propName}'."));
                        }
                    }
                }
            }
        }

        private static void PopulateMaterialProperties(MaterialData data, Material material)
        {
            var shader = material.shader;
            if (shader == null) return;
            var propCount = shader.GetPropertyCount();
            for (var i = 0; i < propCount; i++)
            {
                var propName = shader.GetPropertyName(i);
                var propType = shader.GetPropertyType(i);
                string value;
                switch (propType)
                {
                    case ShaderPropertyType.Color:
                        value = material.GetColor(propName).ToString();
                        break;
                    case ShaderPropertyType.Vector:
                        value = material.GetVector(propName).ToString();
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        value = material.GetFloat(propName).ToString();
                        break;
                    case ShaderPropertyType.Texture:
                        var tex = material.GetTexture(propName);
                        value = tex != null ? AssetDatabase.GetAssetPath(tex) : "null";
                        break;
                    default:
                        value = "";
                        break;
                }
                data.Properties.Add(new MaterialPropertyData
                {
                    Name = propName,
                    Type = propType.ToString(),
                    Value = value,
                });
            }
        }

        // -------------------------------------------------------------------
        // Fingerprint (ported verbatim): shader name + renderQueue + sorted
        // keywords + all property values (textures by GUID). SHA-256 hex.
        // -------------------------------------------------------------------

        private static string ComputeMaterialFingerprint(Material material)
        {
            var sb = new StringBuilder();
            var shader = material.shader;
            sb.Append("shader:").Append(shader != null ? shader.name : "null").Append(';');
            sb.Append("queue:").Append(material.renderQueue).Append(';');

            var keywords = material.shaderKeywords;
            Array.Sort(keywords, StringComparer.Ordinal);
            sb.Append("keywords:").Append(string.Join(",", keywords)).Append(';');

            if (shader != null)
            {
                var propCount = shader.GetPropertyCount();
                for (var i = 0; i < propCount; i++)
                {
                    var propName = shader.GetPropertyName(i);
                    var propType = shader.GetPropertyType(i);
                    switch (propType)
                    {
                        case ShaderPropertyType.Texture:
                            var tex = material.GetTexture(propName);
                            if (tex != null)
                            {
                                var texPath = AssetDatabase.GetAssetPath(tex);
                                sb.Append("tex:").Append(propName).Append('=')
                                  .Append(AssetDatabase.AssetPathToGUID(texPath)).Append(';');
                            }
                            break;
                        case ShaderPropertyType.Color:
                            sb.Append("col:").Append(propName).Append('=')
                              .Append(material.GetColor(propName)).Append(';');
                            break;
                        case ShaderPropertyType.Vector:
                            sb.Append("vec:").Append(propName).Append('=')
                              .Append(material.GetVector(propName)).Append(';');
                            break;
                        default:
                            sb.Append("flt:").Append(propName).Append('=')
                              .Append(material.GetFloat(propName)).Append(';');
                            break;
                    }
                }
            }

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        // -------------------------------------------------------------------
        // Renderer scan (ported): null material slots + builtin materials.
        // -------------------------------------------------------------------

        private static void ScanRenderers(string[] paths, List<RendererData> renderers)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (type != typeof(GameObject)) continue;

                GameObject go;
                try { go = AssetDatabase.LoadAssetAtPath<GameObject>(path); }
                catch { continue; }
                if (go == null) continue;

                Renderer[] renderersOnGo;
                try { renderersOnGo = go.GetComponentsInChildren<Renderer>(true); }
                catch { continue; }
                if (renderersOnGo == null) continue;

                foreach (var renderer in renderersOnGo)
                {
                    if (renderer == null) continue;
                    var childPath = GetFullName(renderer.transform);
                    var rd = new RendererData(path, childPath);

                    Material[] shared;
                    try { shared = renderer.sharedMaterials; }
                    catch { shared = null; }

                    if (shared == null || shared.Length == 0)
                    {
                        rd.Warnings.Add("null_material");
                    }
                    else
                    {
                        foreach (var mat in shared)
                        {
                            if (mat == null) { rd.Warnings.Add("null_material_slot"); continue; }
                            var matPath = AssetDatabase.GetAssetPath(mat);
                            if (matPath != null && matPath.Contains("unity_builtin"))
                                rd.Warnings.Add("builtin_material");
                        }
                    }

                    renderers.Add(rd);
                }
            }
        }

        // -------------------------------------------------------------------
        // Cross-asset detections (ported; full-scan only)
        // -------------------------------------------------------------------

        private static void DetectDuplicateMaterials(List<MaterialData> materials)
        {
            var groups = new Dictionary<string, List<MaterialData>>(StringComparer.Ordinal);
            foreach (var mat in materials)
            {
                if (string.IsNullOrEmpty(mat.Fingerprint)) continue;
                if (!groups.TryGetValue(mat.Fingerprint, out var list))
                {
                    list = new List<MaterialData>();
                    groups[mat.Fingerprint] = list;
                }
                list.Add(mat);
            }

            foreach (var group in groups.Values)
            {
                if (group.Count < 2) continue;
                foreach (var mat in group)
                {
                    mat.IsDuplicate = true;
                    var others = group.Where(m => m != mat).ToList();
                    mat.DuplicatePaths.Clear();
                    mat.DuplicatePaths.AddRange(others.Select(o => o.Path));
                    mat.Issues.Add(new MaterialIssue("duplicate_material",
                        $"Duplicate of {others.Count} material(s): {string.Join(", ", others.Select(o => o.Name))}.",
                        VerifySeverity.Warning));
                }
            }
        }

        private static void ApplyReferencedByPaths(List<MaterialData> materials, List<RendererData> renderers)
        {
            // Build a map of material-path -> GameObject asset paths that
            // reference it, by reloading each renderer's sharedMaterials.
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var rd in renderers)
            {
                GameObject go;
                try { go = AssetDatabase.LoadAssetAtPath<GameObject>(rd.AssetPath); }
                catch { continue; }
                if (go == null) continue;

                Renderer[] rs;
                try { rs = go.GetComponentsInChildren<Renderer>(true); }
                catch { continue; }
                if (rs == null) continue;

                foreach (var renderer in rs)
                {
                    if (renderer == null) continue;
                    if (GetFullName(renderer.transform) != rd.ChildPath) continue;
                    Material[] shared;
                    try { shared = renderer.sharedMaterials; }
                    catch { continue; }
                    if (shared == null) continue;
                    foreach (var mat in shared)
                    {
                        if (mat == null) continue;
                        var matPath = AssetDatabase.GetAssetPath(mat);
                        if (string.IsNullOrEmpty(matPath) || matPath.Contains("unity_builtin")) continue;
                        if (!map.TryGetValue(matPath, out var set))
                        {
                            set = new HashSet<string>(StringComparer.Ordinal);
                            map[matPath] = set;
                        }
                        set.Add(rd.AssetPath);
                    }
                }
            }

            foreach (var mat in materials)
            {
                if (map.TryGetValue(mat.Path, out var set))
                {
                    mat.ReferencedByPaths.Clear();
                    mat.ReferencedByPaths.AddRange(set.OrderBy(p => p));
                }
            }
        }

        private static void DetectUnusedMaterials(List<MaterialData> materials)
        {
            foreach (var mat in materials)
            {
                var inResources = mat.Path.Contains("/Resources/");
                if (mat.ReferencedByPaths.Count == 0 && !inResources)
                {
                    mat.Issues.Add(new MaterialIssue("unused_material",
                        "Material is not referenced by any renderer and is not in Resources.",
                        VerifySeverity.Warning));
                }
            }
        }

        private static void AnalyzeVariantsAndPerformance(List<MaterialData> materials, MaterialsScanSettings settings)
        {
            foreach (var data in materials)
            {
                Material material;
                try { material = AssetDatabase.LoadAssetAtPath<Material>(data.Path); }
                catch { continue; }
                if (material == null) continue;

                var shader = material.shader;

                if (settings.CheckVariants && MaterialReflection.TryGetIsMaterialVariant(material, out var isVariant) && isVariant)
                {
                    data.IsVariant = true;
                    if (MaterialReflection.TryGetParentMaterial(material, out var parent, out var parentLinkBroken))
                    {
                        data.ParentLinkBroken = parentLinkBroken;
                        data.VariantChainDepth = MaterialReflection.ComputeVariantChainDepth(material);
                        if (parentLinkBroken)
                        {
                            data.Issues.Add(new MaterialIssue("variant_parent_invalid",
                                "Material variant parent link is broken — parent does not resolve to an asset.",
                                VerifySeverity.Error));
                        }
                        if (data.VariantChainDepth > settings.VariantDeepChainThreshold)
                        {
                            data.Issues.Add(new MaterialIssue("variant_deep_chain",
                                $"Variant chain depth {data.VariantChainDepth} exceeds threshold {settings.VariantDeepChainThreshold}.",
                                VerifySeverity.Warning));
                        }
                        if (parent != null)
                        {
                            data.VariantOverrideCount = MaterialReflection.ComputeVariantOverrideCount(material, parent);
                            if (data.VariantOverrideCount > settings.VariantHeavyOverridesThreshold)
                            {
                                data.Issues.Add(new MaterialIssue("variant_heavy_overrides",
                                    $"Heavy variant overrides: {data.VariantOverrideCount} (threshold {settings.VariantHeavyOverridesThreshold}).",
                                    VerifySeverity.Warning));
                            }
                        }
                    }
                }

                if (shader != null)
                {
                    if (settings.CheckGpuInstancing)
                    {
                        data.SupportsGpuInstancing = MaterialReflection.TryGetGpuInstancingSupport(shader);
                        if (data.SupportsGpuInstancing == true && !data.GpuInstancingEnabled)
                        {
                            data.Issues.Add(new MaterialIssue("gpu_instancing_off",
                                "Shader supports GPU instancing but it is disabled on the material.",
                                VerifySeverity.Warning));
                        }
                    }

                    if (settings.CheckSrpBatcher)
                    {
                        data.SrpBatcherCompatible = MaterialReflection.TryGetSrpBatcherCompatibility(shader);
                        if (data.SrpBatcherCompatible == false)
                        {
                            data.Issues.Add(new MaterialIssue("srp_batcher_incompatible",
                                $"Shader '{shader.name}' is not SRP Batcher compatible.",
                                VerifySeverity.Warning));
                        }
                    }
                }
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static bool IsBuiltinShader(string shaderName)
        {
            return BuiltinShaderNames.Contains(shaderName);
        }

        private static string GetFullName(Transform transform)
        {
            var names = new Stack<string>();
            var t = transform;
            while (t != null)
            {
                names.Push(t.name);
                t = t.parent;
            }
            return string.Join("/", names);
        }
    }
}
