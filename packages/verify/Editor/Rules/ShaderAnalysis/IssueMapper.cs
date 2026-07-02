using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityOpenMcpVerify.Rules.ShaderAnalysis
{
    public static class IssueMapper
    {
        public const string CodeShaderCompileError = "shader_compile_error";
        public const string CodeMissingShaderAsset = "missing_shader_asset";
        public const string CodeVariantExplosion = "variant_explosion";
        public const string CodePassCountExceeded = "pass_count_exceeded";
        public const string CodeFallbackShader = "fallback_shader";
        public const string CodeExpensiveFeaturePlatform = "expensive_feature_platform";
        public const string CodePlatformKeywordMismatch = "platform_keyword_mismatch";
        public const string CodeDuplicateKeywordProfiles = "duplicate_keyword_profiles";

        public static void MapToIssues(
            List<ShaderData> shaders,
            List<MaterialKeywordSet> materialKeywordSets,
            ShaderAnalysisScanSettings settings,
            string platformProfileId,
            List<VerifyIssue> sink)
        {
            foreach (var shader in shaders)
            {
                if (shader.FailedToLoad)
                {
                    sink.Add(Make(shader, CodeMissingShaderAsset,
                        "Shader asset failed to load — it may be corrupted or removed.",
                        VerifySeverity.Error,
                        Evidence(("shader", shader.Name))));
                    continue;
                }

                if (shader.IsErrorShader)
                {
                    sink.Add(Make(shader, CodeShaderCompileError,
                        $"Shader '{shader.Name}' is an error shader (InternalErrorShader) — it failed to compile or is missing.",
                        VerifySeverity.Error,
                        Evidence(("shader", shader.Name))));
                    // An error shader skips the remaining checks (matches source).
                    continue;
                }

                if (settings.DetectFallbackShaders && shader.IsFallbackShader)
                {
                    var name = !string.IsNullOrEmpty(shader.FallbackName) ? shader.FallbackName : "Yes";
                    sink.Add(Make(shader, CodeFallbackShader,
                        $"Shader '{shader.Name}' has a fallback: {name}.",
                        VerifySeverity.Warning,
                        Evidence(("shader", shader.Name),
                            ("fallback", name))));
                }

                if (settings.DetectVariantExplosion && shader.VariantCount > settings.VariantThreshold)
                {
                    sink.Add(Make(shader, CodeVariantExplosion,
                        $"Shader '{shader.Name}' variant estimate {shader.VariantCount} exceeds threshold {settings.VariantThreshold} (keywords: {shader.KeywordCount}, passes: {shader.PassCount}).",
                        VerifySeverity.Warning,
                        Evidence(("shader", shader.Name),
                            ("variantCount", shader.VariantCount.ToString()),
                            ("threshold", settings.VariantThreshold.ToString()),
                            ("keywordCount", shader.KeywordCount.ToString()),
                            ("passCount", shader.PassCount.ToString()))));
                }

                if (settings.DetectPassCountExceeded && shader.PassCount > settings.PassThreshold)
                {
                    sink.Add(Make(shader, CodePassCountExceeded,
                        $"Shader '{shader.Name}' pass count {shader.PassCount} exceeds threshold {settings.PassThreshold}.",
                        VerifySeverity.Warning,
                        Evidence(("shader", shader.Name),
                            ("passCount", shader.PassCount.ToString()),
                            ("threshold", settings.PassThreshold.ToString()))));
                }

                if (settings.DetectExpensiveFeatures && platformProfileId == "mobile" && shader.ExpensiveKeywords.Count > 0)
                {
                    sink.Add(Make(shader, CodeExpensiveFeaturePlatform,
                        $"Shader '{shader.Name}' uses expensive keywords for the mobile profile: {string.Join("; ", shader.ExpensiveKeywords)}.",
                        VerifySeverity.Warning,
                        Evidence(("shader", shader.Name),
                            ("platformProfile", platformProfileId),
                            ("expensiveKeywords", string.Join("; ", shader.ExpensiveKeywords)))));
                }

                if (settings.DetectPlatformMismatches && platformProfileId == "mobile" && shader.RenderPipeline == "HDRP")
                {
                    sink.Add(Make(shader, CodePlatformKeywordMismatch,
                        $"Shader '{shader.Name}' render pipeline is HDRP but the profile is mobile.",
                        VerifySeverity.Warning,
                        Evidence(("shader", shader.Name),
                            ("renderPipeline", shader.RenderPipeline),
                            ("platformProfile", platformProfileId))));
                }
            }

            // Duplicate keyword profiles (cross-asset, full-scan only).
            if (settings.DetectDuplicateKeywordProfiles && materialKeywordSets.Count > 0)
            {
                var groups = new Dictionary<string, List<MaterialKeywordSet>>(StringComparer.Ordinal);
                foreach (var set in materialKeywordSets)
                {
                    var sorted = set.Keywords.OrderBy(k => k, StringComparer.Ordinal).ToList();
                    var key = string.Join(",", sorted);
                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = new List<MaterialKeywordSet>();
                        groups[key] = list;
                    }
                    list.Add(set);
                }

                foreach (var group in groups.Values)
                {
                    if (group.Count < 2) continue;
                    var keywords = string.Join(",", group[0].Keywords.OrderBy(k => k, StringComparer.Ordinal));
                    sink.Add(new VerifyIssue("shader_analysis", VerifySeverity.Warning,
                        group[0].MaterialPath, CodeDuplicateKeywordProfiles,
                        $"Duplicate keyword profiles: {group.Count} materials share keywords [{keywords}].",
                        Evidence(("materialCount", group.Count.ToString()),
                            ("keywords", keywords))));
                }
            }
        }

        private static VerifyIssue Make(ShaderData shader, string code, string description, VerifySeverity severity,
            IReadOnlyDictionary<string, string> evidence = null)
        {
            return new VerifyIssue("shader_analysis", severity, shader.Path, code, description, evidence);
        }

        private static IReadOnlyDictionary<string, string> Evidence(params (string, string)[] pairs)
        {
            var dict = new Dictionary<string, string>();
            foreach (var (k, v) in pairs)
            {
                if (!string.IsNullOrEmpty(k) && v != null)
                    dict[k] = v;
            }
            return dict;
        }
    }
}
