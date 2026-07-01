using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpVerify.Rules.ShaderAnalysis
{
    public static class Scanner
    {
        private static readonly HashSet<string> ErrorShaderNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Hidden/InternalErrorShader",
            "InternalErrorShader",
        };

        // Mobile-expensive keyword blocklist ported verbatim.
        private static readonly HashSet<string> ExpensiveKeywordsMobile = new HashSet<string>(StringComparer.Ordinal)
        {
            "DIRECTIONAL_COOKIE",
            "POINT_COOKIE",
            "SHADOWS_CUBE",
            "SHADOWS_SCREEN",
            "LIGHTMAP_SHADOW_MIXING",
            "SHADOWS_SHADOWMASK",
            "LIGHTPROBE_SH",
            "VERTEXLIGHT_ON",
            "DIRLIGHTMAP_COMBINED",
            "DYNAMICLIGHTMAP_ON",
        };

        public static void ScanPaths(
            string[] paths,
            ShaderAnalysisScanSettings settings,
            string platformProfileId,
            List<ShaderData> sink,
            List<MaterialKeywordSet> materialKeywordSets,
            bool fullScan)
        {
            if (paths == null || paths.Length == 0) return;

            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!IsShaderAsset(path)) continue;
                if (!File.Exists(path)) continue;

                var data = AnalyzeShader(path, settings, platformProfileId);
                if (data != null) sink.Add(data);
            }

            // Material keyword-set collection for duplicate-feature detection.
            // Runs in full-scan only (needs the cross-asset material set).
            if (fullScan && settings.DetectDuplicateKeywordProfiles)
            {
                foreach (var matPath in CollectMaterialPaths(paths))
                {
                    var keywords = LoadMaterialKeywords(matPath);
                    if (keywords == null || keywords.Count == 0) continue;
                    materialKeywordSets.Add(new MaterialKeywordSet
                    {
                        MaterialPath = matPath,
                        Keywords = keywords,
                    });
                }
            }
        }

        private static ShaderData AnalyzeShader(string assetPath, ShaderAnalysisScanSettings settings, string platformProfileId)
        {
            var data = new ShaderData(assetPath);

            Shader shader = null;
            try { shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath); }
            catch { }

            if (shader == null)
            {
                // For .shadergraph a null load is not necessarily an error in
                // all Unity versions; only flag .shader assets that fail to load.
                if (assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                    data.FailedToLoad = true;
                return data;
            }

            data.Name = shader.name;
            data.PassCount = shader.passCount;
            data.IsErrorShader = IsErrorShader(shader);
            data.RenderPipeline = DetectRenderPipeline(shader);

            // Keyword enumeration via the public shader.keywordSpace API.
            var keywordSet = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var ks = shader.keywordSpace;
                if (ks.keywords != null)
                {
                    foreach (var kw in ks.keywords)
                    {
                        if (kw != null && kw.name != null)
                        {
                            keywordSet.Add(kw.name);
                            data.Keywords.Add(kw.name);
                        }
                    }
                }
            }
            catch { }

            data.KeywordCount = keywordSet.Count;
            data.VariantCount = EstimateVariantCount(data);
            data.IsFallbackShader = HasShaderFallback(assetPath, out var fallbackName);
            data.FallbackName = fallbackName;

            if (settings.DetectExpensiveFeatures && platformProfileId == "mobile")
            {
                foreach (var kw in data.Keywords)
                {
                    if (ExpensiveKeywordsMobile.Contains(kw))
                        data.ExpensiveKeywords.Add(kw);
                }
            }

            return data;
        }

        // -------------------------------------------------------------------
        // Helpers (ported verbatim)
        // -------------------------------------------------------------------

        private static bool IsErrorShader(Shader shader)
        {
            if (shader == null) return true;
            return ErrorShaderNames.Contains(shader.name);
        }

        private static int EstimateVariantCount(ShaderData data)
        {
            if (data.KeywordCount == 0) return data.PassCount;
            return (int)Math.Pow(2, Math.Min(data.KeywordCount, 20)) * data.PassCount;
        }

        private static string DetectRenderPipeline(Shader shader)
        {
            if (shader == null) return "Unknown";
            var name = shader.name;
            if (name.Contains("Universal") || name.Contains("URP")) return "URP";
            if (name.Contains("HDRenderPipeline") || name.Contains("HDRP")) return "HDRP";
            return "Built-in";
        }

        // Ported: raw .shader source parse for the Fallback directive.
        private static bool HasShaderFallback(string shaderPath, out string fallbackName)
        {
            fallbackName = "";
            try
            {
                if (!File.Exists(shaderPath)) return false;
                var lines = File.ReadAllLines(shaderPath);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("Fallback", StringComparison.Ordinal)) continue;
                    if (line.Contains("Off")) return false;
                    // Extract the name between first and last quote.
                    var first = line.IndexOf('"');
                    var last = line.LastIndexOf('"');
                    if (first >= 0 && last > first)
                        fallbackName = line.Substring(first + 1, last - first - 1);
                    else
                        fallbackName = "Yes";
                    return true;
                }
            }
            catch { }
            return false;
        }

        // -------------------------------------------------------------------
        // Material keyword-set collection (for duplicate-feature detection)
        // -------------------------------------------------------------------

        private static IEnumerable<string> CollectMaterialPaths(string[] paths)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    yield return path;
            }
        }

        private static List<string> LoadMaterialKeywords(string matPath)
        {
            Material mat;
            try { mat = AssetDatabase.LoadAssetAtPath<Material>(matPath); }
            catch { return null; }
            if (mat == null) return null;
            return mat.shaderKeywords?.ToList();
        }

        private static bool IsShaderAsset(string path)
        {
            return path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A material + its enabled shader keywords, for duplicate-feature
    /// detection.</summary>
    public class MaterialKeywordSet
    {
        public string MaterialPath { get; set; }
        public List<string> Keywords { get; set; }
    }
}
