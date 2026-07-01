using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class AnimationAnalysisRule : IVerifyRule
    {
        public string Id => "animation_analysis";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            var settings = AnimationAnalysis.AnimationAnalysisScanSettings.Default();
            var animators = new List<AnimationAnalysis.AnimatorData>();
            var clips = new List<AnimationAnalysis.AnimationClipData>();
            // Duplicate-clip detection needs the full clip set as context —
            // full-scan only. Per-controller / per-clip detections run in every
            // mode so a scoped validate_edit still catches them.
            var fullScan = mode == VerifyRunMode.Full;
            AnimationAnalysis.Scanner.ScanPaths(scope.Paths, settings, animators, clips, fullScan);
            AnimationAnalysis.IssueMapper.MapToIssues(animators, clips, settings, sink);
        }
    }
}
