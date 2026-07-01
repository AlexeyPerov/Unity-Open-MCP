using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class ShaderAnalysisRule : IVerifyRule
    {
        public string Id => "shader_analysis";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            // Desktop profile is the gate default (matches scan_paths'
            // platform_profile: "desktop"). A future profile param can switch
            // this to mobile for the mobile-expensive-keyword detection.
            var settings = ShaderAnalysis.ShaderAnalysisScanSettings.Default();
            const string platformProfileId = "desktop";

            var shaders = new List<ShaderAnalysis.ShaderData>();
            var materialKeywordSets = new List<ShaderAnalysis.MaterialKeywordSet>();
            // Duplicate-keyword-profile detection needs the cross-asset material
            // set — full-scan only. Per-shader detections run in every mode.
            var fullScan = mode == VerifyRunMode.Full;
            ShaderAnalysis.Scanner.ScanPaths(scope.Paths, settings, platformProfileId, shaders, materialKeywordSets, fullScan);
            ShaderAnalysis.IssueMapper.MapToIssues(shaders, materialKeywordSets, settings, platformProfileId, sink);
        }
    }
}
