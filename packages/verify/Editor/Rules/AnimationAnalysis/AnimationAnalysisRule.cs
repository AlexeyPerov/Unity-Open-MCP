using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class AnimationAnalysisRule : IVerifyRule
    {
        public string Id => "animation_analysis";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            var data = new List<AnimationAnalysis.AnimationData>();
            AnimationAnalysis.Scanner.ScanPaths(scope.Paths, data);
            AnimationAnalysis.IssueMapper.MapToIssues(data, sink);
        }
    }
}
