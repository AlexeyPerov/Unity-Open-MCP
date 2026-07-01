using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.ShaderAnalysis
{
    public class ShaderData
    {
        public ShaderData(string path)
        {
            Path = path;
        }

        public string Path { get; }
        public string Name { get; set; }
        public int VariantCount { get; set; }
        public int PassCount { get; set; }
        public int KeywordCount { get; set; }
        public List<string> Keywords { get; } = new List<string>();
        public bool IsErrorShader { get; set; }
        public bool IsFallbackShader { get; set; }
        public string FallbackName { get; set; }
        public string RenderPipeline { get; set; }
        public bool FailedToLoad { get; set; }
        public int ReferencingMaterialCount { get; set; }
        public List<string> ExpensiveKeywords { get; } = new List<string>();
    }

    public struct ShaderAnalysisScanSettings
    {
        public bool DetectErrorShaders;
        public bool DetectFallbackShaders;
        public bool DetectVariantExplosion;
        public bool DetectPassCountExceeded;
        public bool DetectPlatformMismatches;
        public bool DetectExpensiveFeatures;
        public bool DetectDuplicateKeywordProfiles;
        public int VariantThreshold;
        public int PassThreshold;
        public int KeywordThreshold;

        public static ShaderAnalysisScanSettings Default()
        {
            return new ShaderAnalysisScanSettings
            {
                DetectErrorShaders = true,
                DetectFallbackShaders = true,
                DetectVariantExplosion = true,
                DetectPassCountExceeded = true,
                DetectPlatformMismatches = true,
                DetectExpensiveFeatures = true,
                DetectDuplicateKeywordProfiles = true,
                // Desktop profile defaults (the gate's default platform_profile).
                VariantThreshold = 1024,
                PassThreshold = 16,
                KeywordThreshold = 128,
            };
        }

        /// <summary>Mobile profile thresholds (use when scanning for a mobile
        /// target — lower variant/pass tolerances, mobile-expensive keyword
        /// detection active).</summary>
        public static ShaderAnalysisScanSettings Mobile()
        {
            return new ShaderAnalysisScanSettings
            {
                DetectErrorShaders = true,
                DetectFallbackShaders = true,
                DetectVariantExplosion = true,
                DetectPassCountExceeded = true,
                DetectPlatformMismatches = true,
                DetectExpensiveFeatures = true,
                DetectDuplicateKeywordProfiles = true,
                VariantThreshold = 128,
                PassThreshold = 4,
                KeywordThreshold = 32,
            };
        }
    }
}
