using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class ShaderAnalysisRule : IVerifyRule
    {
        public string Id => "shader_analysis";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            // Platform profile comes from the scan scope: "desktop" is the
            // gate / scan_paths default, while the batch entry threads the
            // caller's --platform-profile through so a mobile scan_all
            // actually runs the mobile-expensive-keyword detection
            // (expensive_feature_platform) instead of silently analyzing as
            // desktop while the result metadata claims mobile.
            var settings = ShaderAnalysis.ShaderAnalysisScanSettings.Default();
            var platformProfileId = scope.PlatformProfile;

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
